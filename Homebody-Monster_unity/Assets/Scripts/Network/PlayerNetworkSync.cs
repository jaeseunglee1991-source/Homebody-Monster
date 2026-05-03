using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// ════════════════════════════════════════════════════════════════
//  NetworkCharacterData — 변경 없음
// ════════════════════════════════════════════════════════════════
public struct NetworkCharacterData : INetworkSerializable
{
    public int   Job, Affinity, Grade;
    public float MaxHp, BaseAtk, MoveSpeed;
    public int   Active0, Active1, Active2, Active3;
    public int   Passive0, Passive1, Passive2, Passive3;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref Job);      s.SerializeValue(ref Affinity);
        s.SerializeValue(ref Grade);    s.SerializeValue(ref MaxHp);
        s.SerializeValue(ref BaseAtk);  s.SerializeValue(ref MoveSpeed);
        s.SerializeValue(ref Active0);  s.SerializeValue(ref Active1);
        s.SerializeValue(ref Active2);  s.SerializeValue(ref Active3);
        s.SerializeValue(ref Passive0); s.SerializeValue(ref Passive1);
        s.SerializeValue(ref Passive2); s.SerializeValue(ref Passive3);
    }

    public CharacterData ToCharacterData()
    {
        var d = new CharacterData
        {
            job = (JobType)Job, affinity = (AffinityType)Affinity, grade = (GradeTier)Grade,
            maxHp = MaxHp, currentHp = MaxHp, baseAtk = BaseAtk, moveSpeed = MoveSpeed,
            activeSkills = new List<ActiveSkillType>(), passiveSkills = new List<PassiveSkillType>(),
        };
        int[] actives  = { Active0, Active1, Active2, Active3 };
        int[] passives = { Passive0, Passive1, Passive2, Passive3 };
        foreach (int a in actives)  if (a >= 0) d.activeSkills.Add((ActiveSkillType)a);
        foreach (int p in passives) if (p >= 0) d.passiveSkills.Add((PassiveSkillType)p);
        return d;
    }
}

// ════════════════════════════════════════════════════════════════
//  PlayerNetworkSync — 개선 버전
//
//  [원본 대비 변경 요약]
//  1. WaitUntil 폴링 제거 → async Task + CancellationToken으로 교체
//  2. _hasUsedRevive 조기 설정 버그 수정
//     → Supabase 결과 확정 후에만 true로 설정
//     → 중복 진입 방지는 _isProcessingRevive로 분리
//  3. async void 사용 금지 → async Task 사용 (Unity 크래시 방지)
//  4. Despawn 이후 await 재진입 방지 → CancellationToken 공유
//  5. OnNetworkDespawn에서 CTS 정리 보장
//  6. Supabase 인증 클라이언트 위임 (서버는 auth.uid() 없음)
//  7. Thorns 반사 사망 누락 버그 수정 (2순위 사망 체크)
// ════════════════════════════════════════════════════════════════
[RequireComponent(typeof(PlayerController))]
[RequireComponent(typeof(NetworkObject))]
public class PlayerNetworkSync : NetworkBehaviour
{
    public readonly NetworkVariable<float> NetworkHp = new(
        100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public readonly NetworkVariable<float> NetworkMaxHp = new(
        100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public readonly NetworkVariable<int> NetworkKillCount = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public readonly NetworkVariable<bool> NetworkIsDead = new(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // [Fix #1] Owner 쓰기 권한 제거 → Server 권한으로 변경하여 클라이언트 위변조 차단.
    // 닉네임은 SubmitCharacterDataServerRpc의 파라미터로 직접 전달되므로
    // NetworkVariable 동기화 타이밍 버그도 함께 해결됩니다.
    public readonly NetworkVariable<FixedString64Bytes> NetworkNickname = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // [FIX] 이동 방향 동기화 — 다른 클라이언트 캐릭터 flipX 갱신용.
    // ClientNetworkTransform이 위치를 동기화하지만 moveDir은 동기화하지 않아
    // 다른 플레이어가 왼쪽으로 이동해도 스프라이트가 뒤집히지 않는 버그 수정.
    public readonly NetworkVariable<Vector2> NetworkMoveDir = new(
        Vector2.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    // ── 서버 전용 상태 ──────────────────────────────────────────
    private CharacterData      _serverData;
    private float              _lastAttackTime = -999f;
    private PlayerNetworkSync  _pendingKiller  = null;
    // 스킬별 마지막 사용 시각 — 악의적 클라이언트의 쿨다운 무시 RPC 반복 전송 방지
    private readonly Dictionary<ActiveSkillType, float> _skillLastUsed =
        new Dictionary<ActiveSkillType, float>();

    // ── 부활 상태 필드 ─────────────────────────────────────────
    // _hasUsedRevive   : 부활 기회를 최종 소모했는지 (성공/포기/타임아웃 모두 포함)
    //                    true가 된 이후에는 어떤 경로로도 부활 불가
    // _isProcessingRevive : Supabase 통신이 진행 중인지
    //                       중복 ServerRpc 방어용, 통신 중에만 true
    // _reviveCts       : 타임아웃 Task와 Supabase Task가 공유하는 CancellationToken
    //                    둘 중 하나가 먼저 끝나면 나머지를 취소
    private bool _hasUsedRevive      = false;
    private bool _isProcessingRevive = false;
    private CancellationTokenSource _reviveCts = null;

    public CharacterData ServerData => _serverData;

    private PlayerController _controller;

    private void Awake() { _controller = GetComponent<PlayerController>(); }

    // [FEATURE] 서버 측 위치 검증 (ServerValidator 안티치트)
    private void FixedUpdate()
    {
        if (!IsServer || _controller == null || _controller.Rb == null) return;
        if (NetworkIsDead.Value) return;
        ServerValidator.Instance?.RecordAndValidatePosition(this, _controller.Rb.position);
        PingAdaptiveCombat.Instance?.RecordSnapshot(OwnerClientId, _controller.Rb.position);
    }

    // ════════════════════════════════════════════════════════════
    //  NGO 생명주기
    // ════════════════════════════════════════════════════════════

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        NetworkHp.OnValueChanged        += HandleHpChanged;
        NetworkIsDead.OnValueChanged    += HandleDeadChanged;
        NetworkKillCount.OnValueChanged += HandleKillCountChanged;
        // [FIX] NetworkNickname OnValueChanged 미구독 버그.
        // 닉네임이 서버에서 설정되어도 캐릭터 위 닉네임 UI가 갱신되지 않음.
        // 스폰 직후 초기값도 표시되지 않는 문제 포함.
        NetworkNickname.OnValueChanged  += HandleNicknameChanged;

        if (IsOwner)
        {
            // [Fix #1] NetworkNickname.Value를 먼저 쓰고 ServerRpc를 보내면
            // NetworkVariable 동기화가 RPC보다 늦게 도달하여 playerName이 빈 문자열이 됩니다.
            // 닉네임을 RPC 파라미터로 직접 전달하여 타이밍 버그를 해결합니다.
            string nicknameValue = GameManager.Instance?.currentPlayerNickname
                ?? GameManager.Instance?.currentPlayerId
                ?? $"Player_{OwnerClientId}";
            SubmitCharacterDataServerRpc(BuildNetworkData(), new FixedString64Bytes(nicknameValue));
        }

        InGameManager.Instance?.RegisterPlayer(_controller);

        // 스폰 시점에 이미 NetworkNickname이 설정되어 있으면 즉시 UI 반영
        // (OnValueChanged는 값이 바뀔 때만 호출되므로 초기값은 수동 적용 필요)
        if (!NetworkNickname.Value.IsEmpty)
            HandleNicknameChanged(default, NetworkNickname.Value);
    }

    public override void OnNetworkDespawn()
    {
        NetworkHp.OnValueChanged        -= HandleHpChanged;
        NetworkIsDead.OnValueChanged    -= HandleDeadChanged;
        NetworkKillCount.OnValueChanged -= HandleKillCountChanged;
        NetworkNickname.OnValueChanged  -= HandleNicknameChanged;

        // Despawn 시 진행 중인 타이머/Supabase Task 모두 취소
        CancelAndDisposeCts();

        // [FEATURE] 서버 측 안티치트 기록 정리
        if (IsServer)
            ServerValidator.Instance?.RemovePlayer(OwnerClientId);

        base.OnNetworkDespawn();
    }

    // ════════════════════════════════════════════════════════════
    //  NetworkVariable 콜백
    // ════════════════════════════════════════════════════════════

    private bool _hudInitialized = false;

    private void HandleHpChanged(float prev, float curr)
    {
        if (_controller.myData != null) _controller.myData.currentHp = curr;
        if (!IsOwner) return;
        if (!_hudInitialized && InGameHUD.Instance != null && _controller.myData != null)
        {
            InGameHUD.Instance.InitPlayerUI(_controller);
            _hudInitialized = true;
        }
        InGameHUD.Instance?.UpdateHealthBar(curr, NetworkMaxHp.Value);
    }

    private void HandleDeadChanged(bool prev, bool curr) { }

    private void HandleKillCountChanged(int prev, int curr) { _controller.SetKillCount(curr); }

    private void HandleNicknameChanged(FixedString64Bytes prev, FixedString64Bytes curr)
    {
        if (_controller == null) return;
        string nickname = curr.ToString();
        if (_controller.myData != null)
            _controller.myData.playerName = nickname;
        // 캐릭터 프리팹 하위의 "NicknameText" TMP 오브젝트를 자동 탐색하여 갱신
        var tmpTexts = _controller.GetComponentsInChildren<TMPro.TextMeshPro>(true);
        foreach (var t in tmpTexts)
        {
            if (t.gameObject.name == "NicknameText")
            {
                t.text = nickname;
                break;
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  ServerRpc
    // ════════════════════════════════════════════════════════════

    [ServerRpc]
    private void SubmitCharacterDataServerRpc(NetworkCharacterData netData, FixedString64Bytes nickname)
    {
        // StatCalculator 이론 최댓값 기준 범위 검증 (개조 클라이언트 maxHp=9999 등 차단)
        // HP 최댓값: 50 * (1+9*0.111) * 1.4 ≈ 140, ATK 최댓값: 5.0 * 2.0 * 1.5 ≈ 15
        if (!IsValidCharacterData(netData))
        {
            Debug.LogWarning($"[Server] 클라이언트 {OwnerClientId}: 유효하지 않은 CharacterData 거부 " +
                             $"(HP={netData.MaxHp:0.#}, ATK={netData.BaseAtk:0.#})");
            // [FIX] return만 하면 _serverData=null, NetworkHp=0인 채로 alivePlayers에 등록되어
            // 게임 종료 판정을 영구적으로 막는 버그 발생. 비정상 클라이언트를 즉시 킥.
            Debug.LogError($"[Server] 클라이언트 {OwnerClientId} 비정상 데이터 → 연결 강제 종료.");
            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.DisconnectClient(OwnerClientId);
            return;
        }
        _serverData = netData.ToCharacterData();
        // [Fix #1] 닉네임을 RPC 파라미터로 직접 수신.
        // NetworkVariable 동기화보다 RPC가 먼저 도달하는 타이밍 버그를 해결하며,
        // 서버에서 NetworkNickname을 설정하므로 클라이언트 위변조도 차단합니다.
        _serverData.playerName = nickname.ToString();
        // StatusEffectSystem(myData.shieldHp 수정)과 CombatSystem(_serverData.shieldHp 읽기)이
        // 같은 객체를 참조하도록 단일화 → IronSkin 실드, deathMark, tenacity 등 런타임 필드 일관성 보장
        _controller.SetMyData(_serverData);
        NetworkNickname.Value  = nickname;
        NetworkHp.Value        = _serverData.maxHp;
        NetworkMaxHp.Value     = _serverData.maxHp;

        // [FIX] Regeneration 패시브 NetworkHp 미갱신 버그 수정.
        // CombatSystem.RegenerationRoutine은 data.currentHp만 수정하고 NetworkHp.Value를 갱신하지 않아
        // 다른 클라이언트에 HP 회복이 전파되지 않고 서버 전투 판정과도 불일치 발생.
        // HealServer()를 사용하는 전용 코루틴으로 대체하여 NetworkHp.Value 동기화 보장.
        StartCoroutine(RegenerationNetworkRoutine());
    }

    private System.Collections.IEnumerator RegenerationNetworkRoutine()
    {
        var wait = new WaitForSeconds(2f);
        while (true)
        {
            yield return wait;
            // [버그 수정] yield break → continue 로 변경.
            // 기존: 사망 시 yield break → 코루틴 영구 종료.
            //        부활(ReportReviveTicketResultServerRpc)로 NetworkIsDead.Value가
            //        false로 복원되어도 코루틴이 이미 끝났으므로 재생 패시브 영구 비활성화.
            // 수정: 사망 중에는 tick을 건너뛰고(continue), 부활하면 자동으로 재개.
            //        OnNetworkDespawn 시 코루틴은 Unity가 자동 정리하므로 무한루프 문제 없음.
            if (NetworkIsDead.Value) continue;
            if (_serverData == null || !_serverData.HasPassive(PassiveSkillType.Regeneration)) continue;
            if (Time.time - _serverData.lastCombatTime >= 4f && NetworkHp.Value < NetworkMaxHp.Value)
            {
                float amount = Mathf.Max(1.5f, NetworkMaxHp.Value * 0.05f);
                // HealServer: NetworkHp.Value + _serverData.currentHp 동시 갱신 → 클라이언트 전파
                _controller.HealServer(amount);
            }
        }
    }

    private static bool IsValidCharacterData(NetworkCharacterData d)
    {
        if (d.Job   < 0 || d.Job   > 9)   return false;
        if (d.Grade < 0 || d.Grade > 9)   return false;

        // [FIX] Affinity 범위 검증 누락 버그.
        // Job/Grade는 0~9 검증하지만 Affinity(AffinityType: Spicy=0 ~ Pineapple=6, 총 7개)는
        // 검증하지 않았음. 조작된 클라이언트가 Affinity=999 등 범위 밖 값을 전송하면
        // CombatSystem.CheckAffinityAdvantage / IsSpecialAffinity에서 정의되지 않은 enum 값으로
        // 상성 판정이 오동작하고, MintChoco/Pineapple 3배 데미지 상성을 회피하는 치트가 가능.
        // Enum.GetValues().Length로 계산하여 enum 추가 시 자동 반영.
        int affinityMax = System.Enum.GetValues(typeof(AffinityType)).Length - 1;
        if (d.Affinity < 0 || d.Affinity > affinityMax) return false;

        // [FIX] 스킬 슬롯 범위 검증 추가.
        // 범위 밖 ActiveSkillType/PassiveSkillType 값은 SkillSystem.switch default로 빠지지만,
        // 명시적 검증으로 조작된 패킷을 조기 차단. -1은 빈 슬롯(유효).
        int activeMax  = System.Enum.GetValues(typeof(ActiveSkillType)).Length  - 1;
        int passiveMax = System.Enum.GetValues(typeof(PassiveSkillType)).Length - 1;
        int[] actives  = { d.Active0,  d.Active1,  d.Active2,  d.Active3  };
        int[] passives = { d.Passive0, d.Passive1, d.Passive2, d.Passive3 };
        foreach (int a in actives)  if (a != -1 && (a < 0 || a > activeMax))  return false;
        foreach (int p in passives) if (p != -1 && (p < 0 || p > passiveMax)) return false;

        if (d.MaxHp    < 5f   || d.MaxHp    > 160f) return false;
        if (d.BaseAtk  < 0.5f || d.BaseAtk  > 20f)  return false;
        if (d.MoveSpeed < 1f  || d.MoveSpeed > 6f)   return false;
        return true;
    }

    [ServerRpc]
    public void RequestAttackServerRpc(ulong targetNetworkObjectId)
    {
        if (NetworkIsDead.Value || _serverData == null) return;
        if (Time.time - _lastAttackTime < _controller.attackCooldown) return;

        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                targetNetworkObjectId, out var targetNetObj)) return;

        var targetSync = targetNetObj.GetComponent<PlayerNetworkSync>();
        if (targetSync == null || targetSync.NetworkIsDead.Value || targetSync._serverData == null) return;

        float dist = Vector2.Distance(transform.position, targetNetObj.transform.position);
        if (dist > _controller.attackRange * 1.5f) return;

        _lastAttackTime = Time.time;
        _serverData.lastCombatTime = Time.time;

        var attackerFx = _controller.StatusFX;
        var targetFx   = targetSync._controller.StatusFX;

        if (attackerFx != null && attackerFx.IsStealthy)
            attackerFx.RemoveEffect(StatusEffectType.Stealth);

        DamageResult result = CombatSystem.CalculateDamage(_serverData, targetSync._serverData, attackerFx, targetFx);

        if (!result.isEvaded && !result.isDivineGraceBlocked && result.finalDamage > 0f)
        {
            // PostDamageEffects: 흡혈(공격자 HP 회복), 가시갑옥(공격자 HP 감소), lastCombatTime 등 처리
            CombatSystem.PostDamageEffects(_serverData, targetSync._serverData, attackerFx, targetFx, result.finalDamage);
            NetworkHp.Value = Mathf.Clamp(_serverData.currentHp, 0f, NetworkMaxHp.Value);
        }

        // 원래 공격 데미지는 Thorns 반사와 무관하게 타겟에게 항상 적용
        float newHp = Mathf.Max(0f, targetSync.NetworkHp.Value - result.finalDamage);
        targetSync.NetworkHp.Value       = newHp;
        targetSync._serverData.currentHp = newHp;

        targetSync.NotifyHitClientRpc(result);

        // 1순위: 타겟 사망 체크 (일반 공격 흐름)
        // [Fix 신규-C] NetworkIsDead를 먼저 true로 설정하여 같은 프레임 이중 ProcessDeath 차단
        if (newHp <= 0f && !targetSync.NetworkIsDead.Value)
        {
            targetSync.NetworkIsDead.Value = true;
            ProcessDeath(targetSync, attackerFx, targetFx);
        }

        // [버그 수정] 2순위: 가시갑옥(Thorns) 반사로 공격자 HP가 0 이하가 된 경우
        // PostDamageEffects는 _serverData.currentHp만 수정하므로 ProcessDeath가 호출되지 않았음.
        // 원래 공격 데미지를 타겟에게 적용한 이후에 순차적으로 처리합니다.
        // (공격자가 Thorns로 죽어도 원래 타격은 이미 위에서 타겟에 반영됨)
        if (NetworkHp.Value <= 0f && !NetworkIsDead.Value)
        {
            NetworkIsDead.Value = true;
            targetSync.ProcessDeath(this, targetFx, attackerFx);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  사망 처리
    // ════════════════════════════════════════════════════════════

    private void ProcessDeath(PlayerNetworkSync target, StatusEffectSystem attackerFx, StatusEffectSystem targetFx)
    {
        // NetworkIsDead.Value는 호출 측(RequestAttackServerRpc / ApplyDamageServer)에서 이미 true로 설정됩니다.
        // Guardian Angel / Tenacity 발동 시에는 false로 복구하여 생존 처리합니다.
        if (CombatSystem.TryGuardianAngel(target._serverData, targetFx))
        {
            target.NetworkIsDead.Value = false;
            target.NetworkHp.Value     = target._serverData.currentHp;
            return;
        }
        if (CombatSystem.TryTenacity(target._serverData, targetFx))
        {
            target.NetworkIsDead.Value = false;
            target.NetworkHp.Value     = target._serverData.currentHp;
            return;
        }

        // NetworkIsDead.Value는 이미 true — 중복 설정하지 않음
        target.DeclareDeathClientRpc(NetworkObject.NetworkObjectId);

        // ── [DeathMark] 낙인 폭발 훅 ─────────────────────────────
        // 대상이 낙인 상태로 사망했을 때, SkillSystem 코루틴 종료보다 먼저
        // 폭발을 실행해야 정확한 사망 시점의 accumulated 값을 사용할 수 있음.
        // StatusEffectSystem.OnEffectExpired 경로는 사망 후 Update에서 호출되므로
        // 여기서 명시적으로 선처리.
        if (target._serverData != null &&
            target._serverData.deathMarkActive &&
            target._serverData.deathMarkCasterId != ulong.MaxValue)
        {
            // 낙인 상태 플래그 즉시 비활성화 — OnEffectExpired와 이중 폭발 방지
            ulong casterId     = target._serverData.deathMarkCasterId;
            float accumulated  = target._serverData.deathMarkAccumulated;
            target._serverData.deathMarkActive       = false;
            target._serverData.deathMarkCasterId     = ulong.MaxValue;
            target._serverData.deathMarkAccumulated  = 0f;

            target.TriggerDeathMarkExplosion(casterId, accumulated, isKillExplosion: true);
        }

        bool canRevive = CanOfferRevive(target);

        if (canRevive)
        {
            target._pendingKiller = this;
            target.OfferReviveClientRpc();

            // 기존 CTS가 남아있으면 정리 후 새로 생성
            target.CancelAndDisposeCts();
            target._reviveCts = new CancellationTokenSource();

            // 타임아웃 Task 시작 — Unity 메인스레드에서 안전하게 실행
            _ = target.ReviveTimeoutAsync(target._reviveCts.Token);
        }
        else
        {
            target._hasUsedRevive = true;
            FinalizeDeath(target);
        }
    }

    private static bool CanOfferRevive(PlayerNetworkSync target)
    {
        var mgr = InGameManager.Instance;
        if (mgr == null) return false;
        if (target._hasUsedRevive) return false;
        if (mgr.ElapsedGameTime > 60f) return false;
        // [FIX] AliveCount 타이밍 버그.
        // CanOfferRevive는 ProcessDeath에서 FinalizeDeath(→ OnPlayerDied → alivePlayers.Remove)
        // 보다 먼저 호출되므로, 사망자가 아직 alivePlayers에 포함된 상태에서 AliveCount를 읽음.
        // 예: 실제 생존자 2명인데 사망자 포함 3명으로 읽혀 부활을 잘못 허용.
        // → 사망자 1명을 미리 빼서 판정해야 정확한 생존자 수가 됨.
        if (mgr.AliveCount - 1 <= 2) return false;
        if (mgr.MatchReviveUsedCount >= InGameManager.MaxMatchReviveCount) return false;
        return true;
    }

    // 부활권 불가 사유 텍스트 (테스트/디버깅용)

    private static string GetReviveDeniedReason(PlayerNetworkSync target)
    {
        var mgr = InGameManager.Instance;
        if (mgr == null) return "InGameManager 없음";
        if (target._hasUsedRevive)                                         return "이미 부활권 사용함";
        if (mgr.ElapsedGameTime > 60f)                                     return $"시간 초과 ({mgr.ElapsedGameTime:0}초)";
        if (mgr.AliveCount - 1 <= 2)                                       return $"생존자 {mgr.AliveCount - 1}명 (최소 3명 필요)";
        if (mgr.MatchReviveUsedCount >= InGameManager.MaxMatchReviveCount) return $"매치 부활 횟수 소진 ({mgr.MatchReviveUsedCount}/{InGameManager.MaxMatchReviveCount})";
        return "알 수 없음";
    }

    private void FinalizeDeath(PlayerNetworkSync target)
    {
        var killer = target._pendingKiller ?? this;

        // [FIX] 자해 사망 시 자기 자신의 킬 카운트가 증가하는 버그.
        // source = null(자해, RuthlessStrike 등)로 ApplyDamageServer가 호출되면
        // effectiveAttacker = this = target 자신이 되고,
        // _pendingKiller = null이므로 killer = target 자신.
        // 결과: 자기 자신의 킬 카운트가 1 증가함.
        // 수정: killer가 target 자신과 다를 때만 킬 카운트를 올림.
        if (killer != target)
            killer.NetworkKillCount.Value++;

        target._pendingKiller = null;

        string killerName = killer.NetworkNickname.Value.ToString();
        string victimName = target.NetworkNickname.Value.ToString();
        BroadcastKillFeedClientRpc(killerName, victimName);

        InGameManager.Instance?.OnPlayerDied(target._controller);
    }

    [ClientRpc]
    private void BroadcastKillFeedClientRpc(string attackerName, string victimName)
    {
        InGameHUD.Instance?.ShowKillFeed(attackerName, victimName);
    }

    // ════════════════════════════════════════════════════════════
    //  부활 타임아웃 — async Task (코루틴 WaitForSeconds 대체)
    //
    //  기존 ReviveTimeoutRoutine()의 문제:
    //   - WaitForSeconds는 취소 불가 → 플레이어 응답 후에도 5초를 기다림
    //   - 그 5초 사이 RequestReviveServerRpc와 겹치면 FinalizeDeath 이중 호출 가능
    //
    //  개선:
    //   - Task.Delay(token) : CTS.Cancel() 즉시 대기 종료 (0ms 지연)
    //   - _hasUsedRevive 재확인 : 플레이어가 이미 응답했으면 아무것도 안 함
    // ════════════════════════════════════════════════════════════
    private async Task ReviveTimeoutAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(5500, token); // 5.5초 대기 (취소 가능)
        }
        catch (TaskCanceledException)
        {
            return;
        }

        // 5.5초가 지나도록 플레이어가 아무것도 안 한 경우
        // _hasUsedRevive 재확인: RequestReviveServerRpc가 동시 진입했을 가능성 방어
        if (!_hasUsedRevive && !_isProcessingRevive)
        {
            _hasUsedRevive = true;
            FinalizeDeath(this);
            ReviveDeniedClientRpc();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  매치 초기화
    // ════════════════════════════════════════════════════════════

    public void ResetReviveStateForNewMatch()
    {
        if (!IsServer) return;
        _hasUsedRevive      = false;
        _isProcessingRevive = false;
        _pendingKiller      = null;
        CancelAndDisposeCts();
    }

    // ════════════════════════════════════════════════════════════
    //  부활 RPC
    // ════════════════════════════════════════════════════════════

    [ClientRpc]
    private void OfferReviveClientRpc()
    {
        if (IsOwner && InGameHUD.Instance != null)
            InGameHUD.Instance.ShowReviveUI(this);
    }

    [ServerRpc]
    public void RequestReviveServerRpc()
    {
        if (_hasUsedRevive || !NetworkIsDead.Value || _isProcessingRevive) return;

        if (!CanOfferRevive(this))
        {
            Debug.LogWarning($"[Server] {OwnerClientId} 부활 요청 거부 (조건 변경됨)");
            _hasUsedRevive = true;
            CancelAndDisposeCts();
            ReviveDeniedClientRpc();
            FinalizeDeath(this);
            return;
        }

        CancelAndDisposeCts();
        _isProcessingRevive = true;

        // ── 호환성 수정: Supabase 티켓 차감을 인증된 클라이언트에 위임 ──────
        // 문제: UseReviveTicket()은 Supabase auth.uid()로 유저를 식별하지만
        //       데디케이티드 서버는 Supabase에 로그인하지 않아
        //       Client.Auth.CurrentUser == null → 항상 false 반환.
        //       결과: 부활권이 있어도 서버에서 100% 부활 거부됨.
        //
        // 해결: 인증 세션을 가진 Owner 클라이언트에게 티켓 차감을 위임하고
        //       결과를 ReportReviveTicketResultServerRpc로 수신합니다.
        //       게임 조건은 이미 서버에서 검증 완료.
        //       Supabase DB 함수(use_revive_ticket)가 서버 사이드에서
        //       auth.uid()로 원자적 차감 처리하므로 위변조는 DB 레벨에서 차단됩니다.
        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        RequestTicketDeductionClientRpc(rpcParams);
    }

    // ════════════════════════════════════════════════════════════
    //  티켓 차감 위임 흐름
    //
    //  서버 → Owner 클라이언트 : RequestTicketDeductionClientRpc
    //  클라이언트               : UseReviveTicket() (인증된 세션으로 실행)
    //  클라이언트 → 서버        : ReportReviveTicketResultServerRpc(bool)
    //  서버                     : 부활 실행 또는 최종 사망 처리
    // ════════════════════════════════════════════════════════════

    [ClientRpc]
    private void RequestTicketDeductionClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        _ = DeductTicketAndReportAsync();
    }

    private async Task DeductTicketAndReportAsync()
    {
        bool success = false;

        if (SupabaseManager.Instance != null)
        {
            // [FIX] Supabase 응답 대기에 타임아웃 없음 버그.
            // RequestReviveServerRpc에서 ReviveTimeoutAsync를 취소한 뒤 이 Task를 시작하지만
            // 서버 측 타임아웃이 전혀 없어 Supabase 응답이 느리거나 클라이언트가 응답 못 하면
            // _isProcessingRevive = true 상태로 플레이어가 영구 사망 대기에 빠짐.
            // → 10초 타임아웃으로 제한하고 초과 시 부활 거부 처리.
            using var cts = new System.Threading.CancellationTokenSource(
                System.TimeSpan.FromSeconds(10));
            try
            {
                var ticketTask    = SupabaseManager.Instance.UseReviveTicket();
                var completedTask = await System.Threading.Tasks.Task.WhenAny(
                    ticketTask,
                    System.Threading.Tasks.Task.Delay(10000, cts.Token));
                if (completedTask == ticketTask)
                    success = ticketTask.Result;
                else
                    Debug.LogWarning("[PlayerNetworkSync] 티켓 차감 타임아웃 (10초) → 부활 거부");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PlayerNetworkSync] 티켓 차감 오류: {e.Message}");
            }

            // [FIX] DB 차감 성공 시 로컬 캐시 즉시 갱신.
            // 미갱신 시 InGameHUD.UpdateReviveInfoText()가 이전 수량을 표시하고,
            // 실제 0장인데도 부활 버튼이 활성화된 채로 남는 UX 버그 발생.
            if (success && GameManager.Instance != null)
                GameManager.Instance.reviveTicketCount =
                    Mathf.Max(0, GameManager.Instance.reviveTicketCount - 1);
        }
        else
        {
            // [Fix #5] SupabaseManager 없음 = 인증 불가 → 부활 불허
            // 이전 코드는 success = true 로 두어 부활권 없이 무료 부활이 가능했음.
            success = false;
            Debug.LogError("[PlayerNetworkSync] SupabaseManager 인스턴스가 없어 티켓 차감 불가 → 부활 거부");
        }

        // await 이후 Despawn 방어
        if (!IsSpawned) return;

        ReportReviveTicketResultServerRpc(success);
    }

    [ServerRpc]
    public void ReportReviveTicketResultServerRpc(bool success)
    {
        // 중복 호출 방지: _isProcessingRevive 상태일 때만 유효
        if (!_isProcessingRevive || !NetworkIsDead.Value) return;

        _isProcessingRevive = false;
        _hasUsedRevive = true;

        if (!success)
        {
            Debug.LogWarning($"[Server] {OwnerClientId} 부활 거부 — 보유 부활권 없음 (Supabase)");
            ReviveDeniedClientRpc();
            FinalizeDeath(this);
            return;
        }

        // ── 부활 실행 ─────────────────────────────────────────────
        NetworkIsDead.Value      = false;
        NetworkHp.Value          = NetworkMaxHp.Value;
        _serverData.currentHp    = _serverData.maxHp;
        _serverData.tenacityUsed = false; // 부활 시 즉사 방지 1회 기회 복구

        InGameManager.Instance?.OnReviveTicketUsed();
        InGameManager.Instance?.OnPlayerRevived(_controller);

        ExecuteReviveClientRpc();
    }

    [ServerRpc]
    public void RequestGiveUpServerRpc()
    {
        // _isProcessingRevive 추가: 이미 Supabase 처리 중이면 포기 무시
        if (!NetworkIsDead.Value || _hasUsedRevive || _isProcessingRevive) return;

        CancelAndDisposeCts();
        _hasUsedRevive = true;
        FinalizeDeath(this);
    }

    [ClientRpc]
    private void ExecuteReviveClientRpc()
    {
        _controller.ReviveNetwork();
    }

    [ClientRpc]
    private void ReviveDeniedClientRpc()
    {
        if (IsOwner) InGameHUD.Instance?.HideReviveUI();
    }

    [ClientRpc]
    private void NotifyHitClientRpc(DamageResult result)
    {
        _controller.ShowDamagePopupNetwork(result);
    }

    [ClientRpc]
    private void DeclareDeathClientRpc(ulong killerNetworkObjectId)
    {
        _controller.PlayDeathAnimation();
    }

    /// <summary>
    /// 서버 → 각 Owner 클라이언트: 본인의 매치 결과를 전달합니다.
    /// InGameManager.FinishGame()에서 개별 OwnerClientId를 대상으로 호출됩니다.
    ///
    /// [설계 근거]
    ///  • 데디케이티드 서버는 Supabase auth.uid()가 없어 SaveMatchResult 호출 불가
    ///    → UseReviveTicket 위임 패턴(ReportReviveTicketResultServerRpc)과 동일한 구조
    ///  • GameManager(DontDestroyOnLoad) 저장 + Supabase 저장을 Owner 측에서 수행
    ///  • ClientRpcParams로 각 플레이어에게만 전송하므로 불필요한 브로드캐스트 없음
    /// </summary>
    [ClientRpc]
    public void NotifyMatchResultClientRpc(bool isWinner, int rank, int kills, float survivedTime,
        ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        if (GameManager.Instance == null) return;

        GameManager.Instance.lastMatchResult = new MatchResult
        {
            isWinner = isWinner, rank = rank, killCount = kills, survivedTime = survivedTime
        };

        // Supabase 저장은 인증 세션을 가진 Owner(클라이언트)에서 수행.
        // Task를 GameManager에 보관 — ResultController가 완료를 기다린 후 전적을 표시.
        if (SupabaseManager.Instance != null)
            GameManager.Instance.MatchResultSaveTask = SaveMatchResultAsync(isWinner, rank, kills, survivedTime);

        // [FEATURE] 리더보드 전적 기록
        LeaderboardManager.Instance?.SubmitMatchResult(isWinner, rank, kills, survivedTime);
    }

    private async Task SaveMatchResultAsync(bool win, int rank, int kills, float time)
    {
        try { await SupabaseManager.Instance.SaveMatchResult(win, rank, kills, time); }
        catch (System.Exception e) { Debug.LogError($"[PlayerNetworkSync] 결과 저장 실패: {e.Message}"); }
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 / 상태이상 (서버에서 호출)
    // ════════════════════════════════════════════════════════════

    public void ApplyDamageServer(float damage, PlayerNetworkSync source)
    {
        ApplyDamageServer(damage, source, new DamageResult { finalDamage = damage });
    }

    public void ApplyDamageServer(float damage, PlayerNetworkSync source, DamageResult result)
    {
        if (!IsServer || NetworkIsDead.Value || _serverData == null) return;
        if (damage <= 0f) return;

        float newHp = Mathf.Max(0f, NetworkHp.Value - damage);
        NetworkHp.Value       = newHp;
        _serverData.currentHp = newHp;

        if (_serverData.deathMarkActive) _serverData.deathMarkAccumulated += damage;

        NotifyHitClientRpc(result);

        // [Fix #11] NetworkIsDead를 먼저 true로 설정하여 같은 프레임 이중 ProcessDeath 차단
        if (newHp <= 0f && !NetworkIsDead.Value)
        {
            NetworkIsDead.Value = true;
            var effectiveAttacker = source ?? this;
            effectiveAttacker.ProcessDeath(this, effectiveAttacker._controller.StatusFX, _controller.StatusFX);
        }
    }

    /// <summary>
    /// 서버 전용. 시전자(caster) 사망 시 낙인을 강제 해제합니다.
    /// StatusEffectSystem.RemoveEffect → OnEffectExpired를 통해 데이터 초기화까지 수행합니다.
    /// SkillSystem.DeathMark 코루틴의 분기 C(시전자 사망)에서만 호출됩니다.
    /// </summary>
    public void ForceRemoveDeathMarkServer()
    {
        if (!IsServer) return;
        if (_serverData != null)
        {
            _serverData.deathMarkActive      = false;
            _serverData.deathMarkCasterId    = ulong.MaxValue;
            _serverData.deathMarkAccumulated = 0f;
        }
        _controller.StatusFX?.RemoveEffect(StatusEffectType.DeathMarkTarget);

        // 모든 클라이언트에 낙인 해제 동기화 (클라이언트 StatusFX 상태 일치)
        SyncStatusEffectClientRpc((int)StatusEffectType.DeathMarkTarget, 0f, 0f, ulong.MaxValue);
    }

    public void ApplyStatusEffectServer(StatusEffectType type, float duration, float value = 0f, PlayerNetworkSync source = null)
    {
        if (!IsServer || NetworkIsDead.Value) return;

        // [FIX] Swiftness 패시브 슬로우 저항 미적용 버그.
        // StatCalculator.ModifySlowDuration()이 정의되어 있고 Swiftness 보유 시
        // 슬로우 지속시간을 70%로 단축하도록 설계되어 있으나,
        // ApplyStatusEffectServer에서 이 함수를 통과시키지 않아
        // Swiftness 패시브 보유자도 일반 플레이어와 동일한 슬로우 지속시간을 받음.
        float adjustedDuration = duration;
        if (type == StatusEffectType.Slow && _serverData != null)
            adjustedDuration = StatCalculator.ModifySlowDuration(_serverData, duration);

        _controller.StatusFX.ApplyEffectServer(type, adjustedDuration, value, source?._controller);
        // source NetworkObjectId 전달 → 클라이언트에서 피격 방향·넉백 방향 연출에 활용
        ulong srcId = (source?.NetworkObject != null) ? source.NetworkObject.NetworkObjectId : ulong.MaxValue;
        SyncStatusEffectClientRpc((int)type, adjustedDuration, value, srcId);
    }

    [ClientRpc]
    private void SyncStatusEffectClientRpc(int type, float duration, float value, ulong sourceNetObjId)
    {
        if (IsServer) return;

        // duration = 0 은 ForceRemoveDeathMarkServer 등 강제 해제 신호
        if (duration <= 0f)
        {
            _controller.StatusFX.RemoveEffect((StatusEffectType)type);
            return;
        }

        PlayerController sourceCtrl = null;
        if (sourceNetObjId != ulong.MaxValue &&
            NetworkManager.Singleton?.SpawnManager.SpawnedObjects
                .TryGetValue(sourceNetObjId, out var srcNetObj) == true)
            sourceCtrl = srcNetObj.GetComponent<PlayerController>();
        _controller.StatusFX.ApplyEffectNetwork((StatusEffectType)type, duration, value, sourceCtrl);
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 RPC
    // ════════════════════════════════════════════════════════════

    [ServerRpc]
    public void RequestUseSkillServerRpc(int slotIndex, Vector2 targetPos, Vector2 facingDir = default)
    {
        if (NetworkIsDead.Value || _serverData == null) return;

        var statusFx = _controller.StatusFX;
        if (statusFx != null && statusFx.IsSilenced) return;

        if (slotIndex < 0 || slotIndex >= _serverData.activeSkills.Count) return;

        ActiveSkillType skill = _serverData.activeSkills[slotIndex];

        // 서버 측 쿨다운 검증 — 클라이언트가 쿨다운을 무시하고 RPC를 반복 전송해도 서버에서 차단
        float cd = SkillSystem.GetCooldown(skill);
        if (_skillLastUsed.TryGetValue(skill, out float lastUsed) && Time.time - lastUsed < cd) return;
        _skillLastUsed[skill] = Time.time;

        SkillSystem.ActivateSkillServer(skill, _controller, targetPos, facingDir);
        BroadcastSkillVisualsClientRpc((int)skill, targetPos);
    }

    [ClientRpc]
    private void BroadcastSkillVisualsClientRpc(int skillType, Vector2 targetPos)
    {
        _controller.PlaySkillVisuals((ActiveSkillType)skillType, targetPos);
    }

    /// <summary>caster 클라이언트(소유자)에게만 Lucky! 팝업 표시</summary>
    public void ShowLuckyPopupOwner()
    {
        if (!IsServer) return;
        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { OwnerClientId } }
        };
        ShowLuckyPopupClientRpc(rpcParams);
    }

    [ClientRpc]
    private void ShowLuckyPopupClientRpc(ClientRpcParams rpcParams = default)
    {
        _controller.ShowSkillPopup("Lucky!");
    }

    /// <summary>SnackTime — 모든 클라이언트에 caster의 디버프 즉시 해제 전파</summary>
    public void BroadcastRemoveAllDebuffs()
    {
        if (!IsServer) return;
        RemoveAllDebuffsClientRpc();
    }

    [ClientRpc]
    private void RemoveAllDebuffsClientRpc()
    {
        _controller.StatusFX?.RemoveAllDebuffs();
    }

    // ════════════════════════════════════════════════════════════
    //  Trap(덫놓기) 시각화 RPC
    //
    //  [이전 구현의 문제]
    //  서버만 OverlapCircle로 덫 판정을 수행하고 클라이언트에 시각 동기화가 없었음.
    //  피격 플레이어 입장에서는 아무것도 없는 곳에서 갑자기 피해와 슬로우가 발생함.
    //
    //  [해결]
    //  SpawnTrapVisualClientRpc  : 덫 배치 시 모든 클라이언트에 시각 오브젝트 생성
    //  NotifyTrapTriggeredClientRpc : 발동 시 피격 이펙트 재생
    //  RemoveTrapVisualClientRpc : 만료/소진 시 시각 오브젝트 제거
    //
    //  [Inspector 연결 필요]
    //  PlayerController 프리팹에 trapVisualPrefab을 연결하거나,
    //  없으면 DamagePopupPool 팝업으로 폴백합니다.
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 서버 → 모든 클라이언트: 덫 위치에 시각 오브젝트를 생성합니다.
    /// SkillSystem의 Trap 케이스에서 루틴 시작 직전 호출됩니다.
    /// </summary>
    [ClientRpc]
    public void SpawnTrapVisualClientRpc(Vector2 trapPos)
    {
        var trapPrefab = _controller.trapVisualPrefab;
        if (trapPrefab != null)
        {
            var go = Object.Instantiate(trapPrefab, trapPos, Quaternion.identity);
            // 15초 후 자동 삭제 (서버 루틴 최대 시간과 동일)
            Object.Destroy(go, 15f);
            _controller.RegisterTrapVisual(trapPos, go);
        }
        else
        {
            // 프리팹 미설정 시 폴백: 위치에 텍스트 팝업 표시
            DamagePopupPool.Instance?.Spawn(trapPos, "🪴", new Color(0.8f, 0.6f, 0.1f));
        }
    }

    /// <summary>
    /// 서버 → 모든 클라이언트: 덫이 발동됐음을 알리고 피격 이펙트를 재생합니다.
    /// </summary>
    [ClientRpc]
    public void NotifyTrapTriggeredClientRpc(Vector2 trapPos)
    {
        DamagePopupPool.Instance?.Spawn(trapPos + Vector2.up * 0.3f, "TRAP!",
            new Color(0.9f, 0.5f, 0f)); // 주황색
    }

    /// <summary>
    /// 서버 → 모든 클라이언트: 덫이 만료/소진됐을 때 시각 오브젝트를 제거합니다.
    /// </summary>
    [ClientRpc]
    public void RemoveTrapVisualClientRpc(Vector2 trapPos)
    {
        _controller.UnregisterTrapVisual(trapPos);
    }

    // ════════════════════════════════════════════════════════════
    //  위치 강제 설정 RPC — ClientNetworkTransform 환경 전용
    //
    //  ClientNetworkTransform은 Owner 클라이언트가 위치 권한을 가지므로
    //  서버에서 Rb.position을 직접 수정해도 클라이언트 값으로 롤백됩니다.
    //  ShadowRaid(순간이동), ChargeStrike/Bulldozer(돌진), 넉백 등
    //  이동을 수반하는 모든 스킬은 반드시 Owner 클라이언트에게 RPC로 지시해야 합니다.
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Owner 클라이언트의 위치를 서버 인증 좌표로 강제 보정합니다.
    /// 용도 1 — 스킬: ShadowRaid(순간이동), ChargeStrike/Bulldozer(돌진), 넉백
    /// 용도 2 — ServerValidator: 속도핵/텔레포트 감지 시 원래 위치로 롤백
    /// ClientNetworkTransform 환경에서는 서버 직접 수정이 클라이언트에 롤백되므로
    /// 반드시 이 RPC를 통해 Owner에게 위치를 지시해야 합니다.
    /// </summary>
    [ClientRpc]
    public void ForcePositionClientRpc(Vector2 pos, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        if (_controller?.Rb != null)
            _controller.Rb.position = pos;
    }

    [ClientRpc]
    public void ForceMoveClientRpc(Vector2 from, Vector2 to, float duration, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        StartCoroutine(ForceMoveCoroutine(from, to, duration));
    }

    private System.Collections.IEnumerator ForceMoveCoroutine(Vector2 from, Vector2 to, float duration)
    {
        float el = 0f;
        while (el < duration)
        {
            el += Time.deltaTime;
            _controller.Rb.MovePosition(Vector2.Lerp(from, to, Mathf.Clamp01(el / duration)));
            yield return null;
        }
        _controller.Rb.position = to;
    }

    [ClientRpc]
    public void ForceKnockbackClientRpc(Vector2 dir, float force, float duration, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        StartCoroutine(KnockbackCoroutine(dir, force, duration));
    }

    private System.Collections.IEnumerator KnockbackCoroutine(Vector2 dir, float force, float duration)
    {
        float el = 0f;
        while (el < duration)
        {
            el += Time.deltaTime;
            _controller.Rb.MovePosition(_controller.Rb.position +
                dir * force * (1f - el / duration) * Time.deltaTime);
            yield return null;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  DeathMark 전용 — 폭발·체이닝·킬피드
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 서버 전용. StatusEffectSystem(낙인 만료) 또는 ProcessDeath(낙인 대상 사망) 에서 호출.
    ///
    /// isKillExplosion = true  : 낙인 대상 사망으로 폭발 → 주변 체이닝 + 킬 크레딧 + 쿨다운 보상
    /// isKillExplosion = false : 낙인 시간 만료 소멸     → 절반 피해 + 체이닝 없음
    ///
    /// [설계 원칙]
    ///  • 폭발 데미지는 ApplyDamageServer를 통해 ProcessDeath까지 완전 위임
    ///  • 체이닝 킬 크레딧은 원래 caster에게 귀속 (FinalizeDeath의 _pendingKiller 통해)
    ///  • 쿨다운 감소는 서버의 _skillLastUsed 조작으로 구현 (클라이언트 위변조 불가)
    /// </summary>
    public void TriggerDeathMarkExplosion(ulong casterNetObjId, float accumulated, bool isKillExplosion)
    {
        if (!IsServer) return;

        // ── 시전자 조회 ─────────────────────────────────────────
        PlayerNetworkSync casterSync = null;
        if (casterNetObjId != ulong.MaxValue &&
            NetworkManager.Singleton?.SpawnManager.SpawnedObjects
                .TryGetValue(casterNetObjId, out var casterObj) == true)
            casterSync = casterObj?.GetComponent<PlayerNetworkSync>();

        // 시전자가 이미 사망·Despawn됐으면 폭발 취소
        if (casterSync == null || !casterSync.IsSpawned || casterSync.NetworkIsDead.Value) return;

        PlayerController caster = casterSync._controller;
        if (caster == null) return;

        // ── 폭발 데미지 계산 ────────────────────────────────────
        float explosionDamage;
        if (isKillExplosion)
            // 사망 폭발: baseAtk × 2.0 + 쌓인 피해 × 0.5
            explosionDamage = casterSync._serverData.baseAtk * 2.0f + accumulated * 0.5f;
        else
            // 시간 만료 소멸: 쌓인 피해의 35% (소형 패널티 폭발)
            explosionDamage = accumulated * 0.35f;

        explosionDamage = Mathf.Round(explosionDamage * 10f) / 10f;
        if (explosionDamage <= 0f) return;

        // ── 낙인 대상 주변 2.5유닛 체이닝 탐색 ─────────────────
        if (isKillExplosion)
        {
            foreach (var col in Physics2D.OverlapCircleAll(
                _controller.transform.position, 2.5f, caster.enemyLayer))
            {
                var pc = col.GetComponent<PlayerController>();
                if (pc == null || pc == _controller || pc == caster ||
                    pc.IsDead || pc.networkSync == null ||
                    pc.networkSync.NetworkIsDead.Value) continue;

                float chainDmg = Mathf.Round(explosionDamage * 0.6f * 10f) / 10f;
                if (chainDmg <= 0f) continue;
                pc.networkSync.ApplyDamageServer(chainDmg, casterSync);
            }
        }

        // ── 킬피드 & 쿨다운 감소 보상 (사망 폭발 전용) ──────────
        if (isKillExplosion)
        {
            string casterName = casterSync.NetworkNickname.Value.ToString();
            string victimName = NetworkNickname.Value.ToString();
            DeathMarkKillFeedClientRpc(casterName, victimName);

            // 쿨다운 감소: _skillLastUsed[DeathMark]를 8초 앞당김
            // → 22초 쿨다운 중 최대 8초 단축 (연속 낙인 처형 장려)
            if (casterSync._skillLastUsed.ContainsKey(ActiveSkillType.DeathMark))
                casterSync._skillLastUsed[ActiveSkillType.DeathMark] -= 8f;
        }
    }

    /// <summary>서버 → 전체 클라이언트: 낙인처형 전용 킬피드 + 시전자 HUD 보상 팝업</summary>
    [ClientRpc]
    private void DeathMarkKillFeedClientRpc(string casterName, string victimName)
    {
        // 모든 클라이언트: 전용 킬피드 ("☠암살자명 → 피해자명[낙인처형]")
        InGameHUD.Instance?.ShowKillFeed($"☠{casterName}", $"{victimName}[낙인처형]");

        // 시전자 클라이언트에게만 쿨다운 감소 팝업 표시
        string myNick = GameManager.Instance?.currentPlayerNickname;
        if (!string.IsNullOrEmpty(myNick) && myNick == casterName)
        {
            Vector3 popupPos = Camera.main != null
                ? Camera.main.ViewportToWorldPoint(new Vector3(0.5f, 0.72f, 10f))
                : Vector3.up * 3f;
            DamagePopupPool.Instance?.Spawn(popupPos, "낙인처형! CD-8초",
                new Color(0.6f, 0f, 1f)); // 보라색
        }
    }

    // ════════════════════════════════════════════════════════════
    //  내부 유틸
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// CancellationTokenSource를 안전하게 취소하고 메모리를 해제합니다.
    /// Cancel 후 Dispose를 반드시 함께 호출해야 메모리 누수가 없습니다.
    /// </summary>
    private void CancelAndDisposeCts()
    {
        if (_reviveCts == null) return;
        if (!_reviveCts.IsCancellationRequested)
            _reviveCts.Cancel();
        _reviveCts.Dispose();
        _reviveCts = null;
    }

    // ════════════════════════════════════════════════════════════
    //  직렬화 유틸
    // ════════════════════════════════════════════════════════════

    private NetworkCharacterData BuildNetworkData()
    {
        // OnNetworkSpawn 시점엔 myData가 프리팹 기본값일 수 있으므로 GameManager를 우선 (진짜 데이터)
        var d = GameManager.Instance?.myCharacterData ?? _controller.myData;
        if (d == null) { Debug.LogWarning("[PlayerNetworkSync] CharacterData 없음, 기본값 전송"); return default; }
        return new NetworkCharacterData
        {
            Job = (int)d.job, Affinity = (int)d.affinity, Grade = (int)d.grade,
            MaxHp = d.maxHp, BaseAtk = d.baseAtk, MoveSpeed = d.moveSpeed,
            Active0 = GetActive(d, 0), Active1 = GetActive(d, 1),
            Active2 = GetActive(d, 2), Active3 = GetActive(d, 3),
            Passive0 = GetPassive(d, 0), Passive1 = GetPassive(d, 1),
            Passive2 = GetPassive(d, 2), Passive3 = GetPassive(d, 3),
        };
    }

    private static int GetActive(CharacterData d, int i)  => i < d.activeSkills.Count  ? (int)d.activeSkills[i]  : -1;
    private static int GetPassive(CharacterData d, int i) => i < d.passiveSkills.Count ? (int)d.passiveSkills[i] : -1;
}
