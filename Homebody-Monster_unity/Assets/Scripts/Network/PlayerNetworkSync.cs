using System.Collections;
using System.Collections.Generic;
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
            job = (JobType)Job, affinity = (AffinityType)Affinity,
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
//  PlayerNetworkSync
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
    private float              _lastAttackTime  = -999f;
    private bool               _hasUsedRevive   = false;  // 이 플레이어의 부활권 사용 여부 (1회 제한)
    private PlayerNetworkSync  _pendingKiller   = null;   // 부활 대기 중 킬러 참조
    private Coroutine          _reviveTimeout   = null;   // 서버 타임아웃 코루틴

    /// <summary>
    /// 서버에서 관리하는 이 플레이어의 CharacterData 원본.
    /// PlayerController.HealServer() 등 서버 전용 로직에서 참조합니다.
    /// </summary>
    public CharacterData ServerData => _serverData;

    private PlayerController _controller;

    private void Awake() { _controller = GetComponent<PlayerController>(); }

    // ════════════════════════════════════════════════════════════
    //  NGO 생명주기
    // ════════════════════════════════════════════════════════════

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // IsLocalPlayer는 PlayerController에서 '=> IsOwner' 읽기전용 프로퍼티로 정의됨.
        // 외부에서 직접 set하지 않아도 NGO가 IsOwner를 자동 관리합니다.

        NetworkHp.OnValueChanged        += HandleHpChanged;
        NetworkIsDead.OnValueChanged    += HandleDeadChanged;
        NetworkKillCount.OnValueChanged += HandleKillCountChanged;

        if (IsOwner)
        {
            NetworkNickname.Value = new FixedString64Bytes(
                GameManager.Instance?.currentPlayerId ?? $"Player_{OwnerClientId}");
            SubmitCharacterDataServerRpc(BuildNetworkData());
        }

        InGameManager.Instance?.RegisterPlayer(_controller);
    }

    public override void OnNetworkDespawn()
    {
        NetworkHp.OnValueChanged        -= HandleHpChanged;
        NetworkIsDead.OnValueChanged    -= HandleDeadChanged;
        NetworkKillCount.OnValueChanged -= HandleKillCountChanged;
        base.OnNetworkDespawn();
    }

    // ════════════════════════════════════════════════════════════
    //  NetworkVariable 콜백
    // ════════════════════════════════════════════════════════════

    private void HandleHpChanged(float prev, float curr)
    {
        if (_controller.myData != null) _controller.myData.currentHp = curr;
        if (IsOwner) InGameHUD.Instance?.UpdateHealthBar(curr, NetworkMaxHp.Value);
    }

    private void HandleDeadChanged(bool prev, bool curr) { }

    private void HandleKillCountChanged(int prev, int curr) { _controller.SetKillCount(curr); }

    // ════════════════════════════════════════════════════════════
    //  ServerRpc
    // ════════════════════════════════════════════════════════════

    [ServerRpc]
    private void SubmitCharacterDataServerRpc(NetworkCharacterData netData)
    {
        _serverData        = netData.ToCharacterData();
        NetworkHp.Value    = _serverData.maxHp;
        NetworkMaxHp.Value = _serverData.maxHp;
        Debug.Log($"[Server] 플레이어 {OwnerClientId} 데이터 수신 (HP:{_serverData.maxHp}, ATK:{_serverData.baseAtk})");
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
            CombatSystem.PostDamageEffects(_serverData, targetSync._serverData, attackerFx, targetFx, result.finalDamage);
            NetworkHp.Value = Mathf.Clamp(_serverData.currentHp, 0f, NetworkMaxHp.Value);
        }

        float newHp = Mathf.Max(0f, targetSync.NetworkHp.Value - result.finalDamage);
        targetSync.NetworkHp.Value       = newHp;
        targetSync._serverData.currentHp = newHp;

        targetSync.NotifyHitClientRpc(result.finalDamage, result.isEvaded, result.isCritical, result.isDivineGraceBlocked);

        if (newHp <= 0f && !targetSync.NetworkIsDead.Value)
            ProcessDeath(targetSync, attackerFx, targetFx);
    }

    // ════════════════════════════════════════════════════════════
    //  사망 처리
    //
    //  [부활권 제한 조건 추가]
    //  아래 세 가지 조건을 모두 만족해야 부활 UI 표시:
    //    1. 이 플레이어가 아직 부활권을 사용하지 않았을 것 (_hasUsedRevive == false)
    //    2. 게임 시작 후 60초 이내일 것 (InGameManager.ElapsedGameTime <= 60f)
    //    3. 현재 생존자가 3명 이상일 것 (생존자 2명 이하이면 부활 불가)
    //    4. 매치 전체 부활권 사용 횟수가 3회 미만일 것
    // ════════════════════════════════════════════════════════════

    private void ProcessDeath(PlayerNetworkSync target, StatusEffectSystem attackerFx, StatusEffectSystem targetFx)
    {
        // 생존 패시브 우선 체크
        if (CombatSystem.TryGuardianAngel(target._serverData, targetFx))
        { target.NetworkHp.Value = target._serverData.currentHp; return; }
        if (CombatSystem.TryTenacity(target._serverData, targetFx))
        { target.NetworkHp.Value = target._serverData.currentHp; return; }

        // 1단계: 시각적 사망 처리만 (alivePlayers에서 제거는 아직 안 함)
        target.NetworkIsDead.Value = true;
        target.DeclareDeathClientRpc(NetworkObject.NetworkObjectId);

        // ── 부활권 사용 가능 여부 판정 (서버 권한) ──────────────
        bool canRevive = CanOfferRevive(target);

        if (canRevive)
        {
            // 킬러 참조 보관 (부활 포기/타임아웃 시 FinalizeDeath에서 사용)
            target._pendingKiller = this;
            // 클라이언트에 부활 UI 표시
            target.OfferReviveClientRpc();
            // 서버에서 5초 타임아웃 감시 시작
            target._reviveTimeout = target.StartCoroutine(target.ReviveTimeoutRoutine());
        }
        else
        {
            // 조건 불충족 → 즉시 최종 사망
            string reason = GetReviveDeniedReason(target);
            Debug.Log($"[Server] {target.OwnerClientId} 부활권 사용 불가 — {reason}");
            FinalizeDeath(target);
        }
    }

    /// <summary>
    /// 부활 UI를 표시할 수 있는지 서버에서 판정합니다.
    /// 네 가지 조건을 모두 만족해야 true를 반환합니다.
    /// </summary>
    private static bool CanOfferRevive(PlayerNetworkSync target)
    {
        var mgr = InGameManager.Instance;
        if (mgr == null) return false;

        // 조건 1: 이 플레이어 개인이 아직 부활권을 사용하지 않았어야 함
        if (target._hasUsedRevive) return false;

        // 조건 2: 게임 시작 후 60초 이내여야 함
        if (mgr.ElapsedGameTime > 60f) return false;

        // 조건 3: 현재 생존자가 3명 이상이어야 함 (2명 이하이면 불가)
        if (mgr.AliveCount <= 2) return false;

        // 조건 4: 매치 전체 부활권 사용 횟수가 최대치(3회) 미만이어야 함
        if (mgr.MatchReviveUsedCount >= InGameManager.MaxMatchReviveCount) return false;

        return true;
    }

    private static string GetReviveDeniedReason(PlayerNetworkSync target)
    {
        var mgr = InGameManager.Instance;
        if (mgr == null) return "InGameManager 없음";
        if (target._hasUsedRevive)                             return "이미 부활권 사용함";
        if (mgr.ElapsedGameTime > 60f)                        return $"시간 초과 ({mgr.ElapsedGameTime:0}초)";
        if (mgr.AliveCount <= 2)                              return $"생존자 {mgr.AliveCount}명 (최소 3명 필요)";
        if (mgr.MatchReviveUsedCount >= InGameManager.MaxMatchReviveCount) return $"매치 부활 횟수 소진 ({mgr.MatchReviveUsedCount}/{InGameManager.MaxMatchReviveCount})";
        return "알 수 없음";
    }

    /// <summary>
    /// 최종 사망 확정: 킬 카운트 올리고 InGameManager에 통보.
    /// ProcessDeath에서 분리되어 부활 포기/타임아웃 후에 호출됨.
    /// </summary>
    private void FinalizeDeath(PlayerNetworkSync target)
    {
        var killer = target._pendingKiller ?? this;
        killer.NetworkKillCount.Value++;
        target._pendingKiller = null;

        InGameManager.Instance?.OnPlayerDied(target._controller);
        Debug.Log($"[Server] {killer.OwnerClientId} → {target.OwnerClientId} 처치! (킬:{killer.NetworkKillCount.Value})");
    }

    /// <summary>서버: 5.5초 후 부활권 미사용 시 강제 사망 처리</summary>
    private IEnumerator ReviveTimeoutRoutine()
    {
        yield return new WaitForSeconds(5.5f);
        if (NetworkIsDead.Value && !_hasUsedRevive)
        {
            _hasUsedRevive = true;
            FinalizeDeath(this);
        }
    }

    /// <summary>
    /// 새 매치 시작 시 이 플레이어의 부활권 상태를 초기화합니다.
    /// InGameManager.GameStartSequence()에서 호출하세요.
    /// (서버 권한)
    /// </summary>
    public void ResetReviveStateForNewMatch()
    {
        if (!IsServer) return;
        _hasUsedRevive = false;
        if (_reviveTimeout != null) { StopCoroutine(_reviveTimeout); _reviveTimeout = null; }
        if (_pendingKiller != null) _pendingKiller = null;
        Debug.Log($"[Server] {OwnerClientId} 부활권 상태 초기화");
    }

    // ════════════════════════════════════════════════════════════
    //  부활 RPC
    // ════════════════════════════════════════════════════════════

    /// <summary>서버 → 사망한 당사자만: 부활 UI 표시 요청</summary>
    [ClientRpc]
    private void OfferReviveClientRpc()
    {
        if (IsOwner && InGameHUD.Instance != null)
            InGameHUD.Instance.ShowReviveUI(this);
    }

    /// <summary>
    /// 클라이언트 → 서버: 부활 확정 요청.
    /// 1단계: 매치 조건 재검증 (동기 — 시간/생존자/매치횟수)
    /// 2단계: Supabase 티켓 차감 시도 (비동기 코루틴)
    ///   → 티켓 없음: ReviveDenied + FinalizeDeath
    ///   → 티켓 있음: 부활 실행 + 매치 카운터 증가 + alivePlayers 재등록
    /// </summary>
    [ServerRpc]
    public void RequestReviveServerRpc()
    {
        if (_hasUsedRevive || !NetworkIsDead.Value) return;

        // 요청 시점 조건 재검증 (타임아웃/생존자 수 변화 대비)
        if (!CanOfferRevive(this))
        {
            Debug.LogWarning($"[Server] {OwnerClientId} 부활 요청 거부 (조건 변경됨)");
            _hasUsedRevive = true;
            if (_reviveTimeout != null) { StopCoroutine(_reviveTimeout); _reviveTimeout = null; }
            ReviveDeniedClientRpc();
            FinalizeDeath(this);
            return;
        }

        // 타임아웃 중단 — Supabase 응답 대기 중 타임아웃이 겹치지 않도록
        if (_reviveTimeout != null) { StopCoroutine(_reviveTimeout); _reviveTimeout = null; }

        // Supabase 티켓 차감 (비동기) → 코루틴으로 처리
        StartCoroutine(ProcessReviveWithSupabase());
    }

    /// <summary>
    /// Supabase에서 보유 티켓 1장을 차감하고 결과에 따라 부활 또는 즉사를 처리합니다.
    /// (서버 전용, RequestReviveServerRpc 내부에서만 호출)
    ///
    /// Supabase use_revive_ticket() RPC 동작:
    ///   profiles.revive_ticket_count > 0 → 1 차감 → true 반환
    ///   profiles.revive_ticket_count = 0 → false 반환 (티켓 없음)
    /// </summary>
    private IEnumerator ProcessReviveWithSupabase()
    {
        // 응답 대기 중 중복 요청 방지 — 미리 잠금
        _hasUsedRevive = true;

        bool supabaseSuccess = false;

        if (SupabaseManager.Instance != null)
        {
            // Task → 코루틴 브릿지 (Unity에서 async Task를 yield로 대기)
            var task = SupabaseManager.Instance.UseReviveTicket();
            yield return new WaitUntil(() => task.IsCompleted);
            supabaseSuccess = task.Result;
        }
        else
        {
            // Supabase 미연결 환경 (에디터 테스트): 항상 성공 처리
            Debug.LogWarning("[Server] SupabaseManager 없음 — 티켓 차감 생략 (테스트 모드)");
            supabaseSuccess = true;
        }

        if (!supabaseSuccess)
        {
            // 보유 티켓 없음 → 부활 거부 + 즉사
            Debug.LogWarning($"[Server] {OwnerClientId} 부활 거부 — 보유 부활권 없음 (Supabase)");
            ReviveDeniedClientRpc();
            FinalizeDeath(this);
            yield break;
        }

        // ── 부활 실행 ────────────────────────────────────────────
        NetworkIsDead.Value   = false;
        NetworkHp.Value       = NetworkMaxHp.Value;
        _serverData.currentHp = _serverData.maxHp;

        InGameManager.Instance?.OnReviveTicketUsed();
        InGameManager.Instance?.OnPlayerRevived(_controller);

        ExecuteReviveClientRpc();
        Debug.Log($"[Server] {OwnerClientId} 부활 확정! Supabase 티켓 차감 완료 (매치: {InGameManager.Instance?.MatchReviveUsedCount}/{InGameManager.MaxMatchReviveCount})");
    }

    /// <summary>
    /// 클라이언트 → 서버: 포기 확정 요청.
    /// </summary>
    [ServerRpc]
    public void RequestGiveUpServerRpc()
    {
        if (!NetworkIsDead.Value || _hasUsedRevive) return;

        if (_reviveTimeout != null) { StopCoroutine(_reviveTimeout); _reviveTimeout = null; }

        _hasUsedRevive = true;
        FinalizeDeath(this);
    }

    /// <summary>서버 → 모든 클라이언트: 부활 애니메이션 실행</summary>
    [ClientRpc]
    private void ExecuteReviveClientRpc()
    {
        _controller.ReviveNetwork();
    }

    /// <summary>서버 → 당사자: 부활 조건 불충족으로 거부됨을 알림 (HUD 정리용)</summary>
    [ClientRpc]
    private void ReviveDeniedClientRpc()
    {
        if (IsOwner) InGameHUD.Instance?.HideReviveUI();
    }

    [ClientRpc]
    private void NotifyHitClientRpc(float damage, bool isEvaded, bool isCritical, bool isDivineGrace)
    {
        _controller.ShowDamagePopupNetwork(new DamageResult
        {
            finalDamage = damage, isEvaded = isEvaded,
            isCritical = isCritical, isDivineGraceBlocked = isDivineGrace
        });
    }

    [ClientRpc]
    private void DeclareDeathClientRpc(ulong killerNetworkObjectId)
    {
        _controller.PlayDeathAnimation();
        if (IsOwner) Debug.Log("[Network] 내 캐릭터가 사망했습니다.");
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 / 상태이상 (서버에서 호출)
    // ════════════════════════════════════════════════════════════

    public void ApplyDamageServer(float damage, PlayerNetworkSync source)
    {
        ApplyDamageServer(damage, source, new DamageResult { finalDamage = damage });
    }

    /// <summary>NetworkProjectile 등 DamageResult를 이미 가진 경로에서 호출. MISS·CRIT 팝업 포함.</summary>
    public void ApplyDamageServer(float damage, PlayerNetworkSync source, DamageResult result)
    {
        if (!IsServer || NetworkIsDead.Value || _serverData == null) return;
        if (damage <= 0f) return;

        float newHp = Mathf.Max(0f, NetworkHp.Value - damage);
        NetworkHp.Value       = newHp;
        _serverData.currentHp = newHp;

        if (_serverData.deathMarkActive) _serverData.deathMarkAccumulated += damage;

        NotifyHitClientRpc(result.finalDamage, result.isEvaded, result.isCritical, result.isDivineGraceBlocked);

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
        SyncStatusEffectClientRpc((int)type, duration, value);
    }
    
    [ClientRpc]
    private void SyncStatusEffectClientRpc(int type, float duration, float value)
    {
        if (IsServer) return; // 서버는 이미 적용 완료
        _controller.StatusFX.ApplyEffectNetwork((StatusEffectType)type, duration, value);
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 RPC (PlayerController.UseSkill에서 호출)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 로컬 플레이어가 스킬을 사용할 때 PlayerController.UseSkill()에서 호출합니다.
    /// 서버에서 침묵·사망 여부를 재검증한 뒤 SkillSystem을 실행하고,
    /// 모든 클라이언트에 시각 효과를 전파합니다.
    /// </summary>
    [ServerRpc]
    public void RequestUseSkillServerRpc(int slotIndex, Vector2 targetPos)
    {
        if (NetworkIsDead.Value || _serverData == null) return;

        // 침묵 상태 서버 재검증
        var statusFx = _controller.StatusFX;
        if (statusFx != null && statusFx.IsSilenced) return;

        if (slotIndex < 0 || slotIndex >= _serverData.activeSkills.Count) return;

        ActiveSkillType skill = _serverData.activeSkills[slotIndex];

        // 서버에서 스킬 효과 적용 (데미지·버프·상태이상 등)
        SkillSystem.ActivateSkillServer(skill, _controller, targetPos);

        // 모든 클라이언트에 시각 효과 전파
        BroadcastSkillVisualsClientRpc((int)skill, targetPos);
    }

    /// <summary>스킬 시각 효과(애니메이션·파티클)를 모든 클라이언트에서 재생합니다.</summary>
    [ClientRpc]
    private void BroadcastSkillVisualsClientRpc(int skillType, Vector2 targetPos)
    {
        _controller.PlaySkillVisuals((ActiveSkillType)skillType, targetPos);
    }

    // ════════════════════════════════════════════════════════════
    //  직렬화 유틸
    // ════════════════════════════════════════════════════════════

    private NetworkCharacterData BuildNetworkData()
    {
        var d = _controller.myData ?? GameManager.Instance?.myCharacterData;
        if (d == null) { Debug.LogWarning("[PlayerNetworkSync] CharacterData 없음, 기본값 전송"); return default; }
        return new NetworkCharacterData
        {
            Job = (int)d.job, Affinity = (int)d.affinity,
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
