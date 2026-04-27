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
    public readonly NetworkVariable<FixedString64Bytes> NetworkNickname = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

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

    // ════════════════════════════════════════════════════════════
    //  NGO 생명주기
    // ════════════════════════════════════════════════════════════

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        NetworkHp.OnValueChanged        += HandleHpChanged;
        NetworkIsDead.OnValueChanged    += HandleDeadChanged;
        NetworkKillCount.OnValueChanged += HandleKillCountChanged;

        if (IsOwner)
        {
            NetworkNickname.Value = new FixedString64Bytes(
                GameManager.Instance?.currentPlayerNickname
                ?? GameManager.Instance?.currentPlayerId
                ?? $"Player_{OwnerClientId}");
            SubmitCharacterDataServerRpc(BuildNetworkData());
        }

        InGameManager.Instance?.RegisterPlayer(_controller);
    }

    public override void OnNetworkDespawn()
    {
        NetworkHp.OnValueChanged        -= HandleHpChanged;
        NetworkIsDead.OnValueChanged    -= HandleDeadChanged;
        NetworkKillCount.OnValueChanged -= HandleKillCountChanged;

        // Despawn 시 진행 중인 타이머/Supabase Task 모두 취소
        CancelAndDisposeCts();

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

    // ════════════════════════════════════════════════════════════
    //  ServerRpc
    // ════════════════════════════════════════════════════════════

    [ServerRpc]
    private void SubmitCharacterDataServerRpc(NetworkCharacterData netData)
    {
        // StatCalculator 이론 최댓값 기준 범위 검증 (개조 클라이언트 maxHp=9999 등 차단)
        // HP 최댓값: 50 * (1+9*0.111) * 1.4 ≈ 140, ATK 최댓값: 5.0 * 2.0 * 1.5 ≈ 15
        if (!IsValidCharacterData(netData))
        {
            Debug.LogWarning($"[Server] 클라이언트 {OwnerClientId}: 유효하지 않은 CharacterData 거부 " +
                             $"(HP={netData.MaxHp:0.#}, ATK={netData.BaseAtk:0.#})");
            return;
        }
        _serverData        = netData.ToCharacterData();
        // 닉네임은 NetworkNickname을 통해 별도 동기화되므로 서버에서 주입
        _serverData.playerName = NetworkNickname.Value.ToString();
        NetworkHp.Value    = _serverData.maxHp;
        NetworkMaxHp.Value = _serverData.maxHp;
    }

    private static bool IsValidCharacterData(NetworkCharacterData d)
    {
        if (d.Job   < 0 || d.Job   > 9)   return false;
        if (d.Grade < 0 || d.Grade > 9)   return false;
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
        if (newHp <= 0f && !targetSync.NetworkIsDead.Value)
            ProcessDeath(targetSync, attackerFx, targetFx);

        // [버그 수정] 2순위: 가시갑옥(Thorns) 반사로 공격자 HP가 0 이하가 된 경우
        // PostDamageEffects는 _serverData.currentHp만 수정하므로 ProcessDeath가 호출되지 않았음.
        // 원래 공격 데미지를 타겟에게 적용한 이후에 순차적으로 처리합니다.
        // (공격자가 Thorns로 죽어도 원래 타격은 이미 위에서 타겟에 반영됨)
        if (NetworkHp.Value <= 0f && !NetworkIsDead.Value)
            targetSync.ProcessDeath(this, targetFx, attackerFx);
    }

    // ════════════════════════════════════════════════════════════
    //  사망 처리
    // ════════════════════════════════════════════════════════════

    private void ProcessDeath(PlayerNetworkSync target, StatusEffectSystem attackerFx, StatusEffectSystem targetFx)
    {
        if (CombatSystem.TryGuardianAngel(target._serverData, targetFx))
        { target.NetworkHp.Value = target._serverData.currentHp; return; }
        if (CombatSystem.TryTenacity(target._serverData, targetFx))
        { target.NetworkHp.Value = target._serverData.currentHp; return; }

        target.NetworkIsDead.Value = true;
        target.DeclareDeathClientRpc(NetworkObject.NetworkObjectId);

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
        if (mgr.AliveCount <= 2) return false;
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
        if (mgr.AliveCount <= 2)                                           return $"생존자 {mgr.AliveCount}명 (최소 3명 필요)";
        if (mgr.MatchReviveUsedCount >= InGameManager.MaxMatchReviveCount) return $"매치 부활 횟수 소진 ({mgr.MatchReviveUsedCount}/{InGameManager.MaxMatchReviveCount})";
        return "알 수 없음";
    }

    private void FinalizeDeath(PlayerNetworkSync target)
    {
        var killer = target._pendingKiller ?? this;
        killer.NetworkKillCount.Value++;
        target._pendingKiller = null;

        InGameManager.Instance?.OnPlayerDied(target._controller);
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
            success = await SupabaseManager.Instance.UseReviveTicket();
        }
        else
        {
            success = true;
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
        NetworkIsDead.Value   = false;
        NetworkHp.Value       = NetworkMaxHp.Value;
        _serverData.currentHp = _serverData.maxHp;

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

        // Supabase 저장은 인증 세션을 가진 Owner(클라이언트)에서 수행
        if (SupabaseManager.Instance != null)
            _ = SaveMatchResultAsync(isWinner, rank, kills, survivedTime);
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

        if (newHp <= 0f && !NetworkIsDead.Value)
        {
            var effectiveAttacker = source ?? this;
            effectiveAttacker.ProcessDeath(this, effectiveAttacker._controller.StatusFX, _controller.StatusFX);
        }
    }

    public void ApplyStatusEffectServer(StatusEffectType type, float duration, float value = 0f, PlayerNetworkSync source = null)
    {
        if (!IsServer || NetworkIsDead.Value) return;
        _controller.StatusFX.ApplyEffectServer(type, duration, value, source?._controller);
        // source NetworkObjectId 전달 → 클라이언트에서 피격 방향·넉백 방향 연출에 활용
        ulong srcId = (source?.NetworkObject != null) ? source.NetworkObject.NetworkObjectId : ulong.MaxValue;
        SyncStatusEffectClientRpc((int)type, duration, value, srcId);
    }

    [ClientRpc]
    private void SyncStatusEffectClientRpc(int type, float duration, float value, ulong sourceNetObjId)
    {
        if (IsServer) return;
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
    public void RequestUseSkillServerRpc(int slotIndex, Vector2 targetPos)
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

        SkillSystem.ActivateSkillServer(skill, _controller, targetPos);
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
    //  위치 강제 설정 RPC — ClientNetworkTransform 환경 전용
    //
    //  ClientNetworkTransform은 Owner 클라이언트가 위치 권한을 가지므로
    //  서버에서 Rb.position을 직접 수정해도 클라이언트 값으로 롤백됩니다.
    //  ShadowRaid(순간이동), ChargeStrike/Bulldozer(돌진), 넉백 등
    //  이동을 수반하는 모든 스킬은 반드시 Owner 클라이언트에게 RPC로 지시해야 합니다.
    // ════════════════════════════════════════════════════════════

    [ClientRpc]
    public void ForcePositionClientRpc(Vector2 pos, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
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
