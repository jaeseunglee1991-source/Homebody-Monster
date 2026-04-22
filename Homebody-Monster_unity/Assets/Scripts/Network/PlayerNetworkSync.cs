using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// ════════════════════════════════════════════════════════════════
//  네트워크 전송용 CharacterData 구조체
//  INetworkSerializable로 서버 ↔ 클라이언트 직렬화를 지원합니다.
// ════════════════════════════════════════════════════════════════
public struct NetworkCharacterData : INetworkSerializable
{
    public int   Job;
    public int   Affinity;
    public int   Grade;
    public float MaxHp;
    public float BaseAtk;
    public float MoveSpeed;
    // 액티브 스킬 (최대 4개), -1 = 없음
    public int Active0, Active1, Active2, Active3;
    // 패시브 스킬 (최대 4개), -1 = 없음
    public int Passive0, Passive1, Passive2, Passive3;

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

    /// <summary>구조체에서 CharacterData 인스턴스를 복원합니다 (서버 전용).</summary>
    public CharacterData ToCharacterData()
    {
        var d = new CharacterData
        {
            job       = (JobType)Job,
            affinity  = (AffinityType)Affinity,
            maxHp     = MaxHp,
            currentHp = MaxHp,
            baseAtk   = BaseAtk,
            moveSpeed = MoveSpeed,
            activeSkills  = new List<ActiveSkillType>(),
            passiveSkills = new List<PassiveSkillType>(),
        };

        int[] actives  = { Active0,  Active1,  Active2,  Active3  };
        int[] passives = { Passive0, Passive1, Passive2, Passive3 };
        foreach (int a in actives)  if (a >= 0) d.activeSkills.Add((ActiveSkillType)a);
        foreach (int p in passives) if (p >= 0) d.passiveSkills.Add((PassiveSkillType)p);

        return d;
    }
}

// ════════════════════════════════════════════════════════════════
//  PlayerNetworkSync
//  NGO 기반 플레이어 네트워크 동기화.
//  위치 동기화 → NGO 내장 NetworkTransform 컴포넌트로 처리.
//  HP·킬수·사망 상태·전투 판정 → 이 스크립트에서 서버 권한으로 처리.
// ════════════════════════════════════════════════════════════════
[RequireComponent(typeof(PlayerController))]
[RequireComponent(typeof(NetworkObject))]
public class PlayerNetworkSync : NetworkBehaviour
{
    // ── NetworkVariables (서버 쓰기, 모든 클라이언트 읽기) ─────
    public readonly NetworkVariable<float> NetworkHp = new(
        100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<float> NetworkMaxHp = new(
        100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<int> NetworkKillCount = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<bool> NetworkIsDead = new(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Owner 쓰기 (닉네임은 자신만 설정)
    public readonly NetworkVariable<FixedString64Bytes> NetworkNickname = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── 서버 전용 상태 ─────────────────────────────────────────
    private CharacterData _serverData;     // 서버가 보관하는 이 플레이어 스탯 원본
    private float         _lastAttackTime = -999f;

    // ── 컴포넌트 캐싱 ──────────────────────────────────────────
    private PlayerController _controller;

    private void Awake()
    {
        _controller = GetComponent<PlayerController>();
    }

    // ════════════════════════════════════════════════════════════
    //  NGO 생명주기
    // ════════════════════════════════════════════════════════════

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // PlayerController에 로컬 플레이어 여부 전달
        _controller.IsLocalPlayer = IsOwner;

        // NetworkVariable 변화 구독
        NetworkHp.OnValueChanged        += HandleHpChanged;
        NetworkIsDead.OnValueChanged    += HandleDeadChanged;
        NetworkKillCount.OnValueChanged += HandleKillCountChanged;

        if (IsOwner)
        {
            // 닉네임 등록 (Owner 권한)
            string nick = GameManager.Instance?.currentPlayerId
                          ?? $"Player_{OwnerClientId}";
            NetworkNickname.Value = new FixedString64Bytes(nick);

            // 내 CharacterData를 서버에 전송 (전투 계산에 사용)
            SubmitCharacterDataServerRpc(BuildNetworkData());
        }

        // 생존자 목록에 등록 (모든 클라이언트)
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
    //  NetworkVariable 콜백 (모든 클라이언트에서 실행)
    // ════════════════════════════════════════════════════════════

    private void HandleHpChanged(float prev, float curr)
    {
        // 로컬 CharacterData 동기화 (스킬 계산 등에 사용)
        if (_controller.myData != null)
            _controller.myData.currentHp = curr;

        // 내 HP만 HUD에 표시
        if (IsOwner)
            InGameHUD.Instance?.UpdateHealthBar(curr, NetworkMaxHp.Value);
    }

    private void HandleDeadChanged(bool prev, bool curr)
    {
        // 사망 시각 처리는 DeclareDeathClientRpc에서 담당
    }

    private void HandleKillCountChanged(int prev, int curr)
    {
        _controller.SetKillCount(curr);
    }

    // ════════════════════════════════════════════════════════════
    //  ServerRpc — 클라이언트 → 서버
    // ════════════════════════════════════════════════════════════

    /// <summary>스폰 직후 자신의 CharacterData를 서버에 전송합니다.</summary>
    [ServerRpc]
    private void SubmitCharacterDataServerRpc(NetworkCharacterData netData)
    {
        _serverData = netData.ToCharacterData();
        NetworkHp.Value    = _serverData.maxHp;
        NetworkMaxHp.Value = _serverData.maxHp;
        Debug.Log($"[Server] 플레이어 {OwnerClientId} 데이터 수신 완료 " +
                  $"(HP:{_serverData.maxHp}, ATK:{_serverData.baseAtk})");
    }

    /// <summary>
    /// 로컬 플레이어가 타겟을 공격할 때 PlayerController에서 호출합니다.
    /// 서버에서 거리·쿨다운·데미지·사망을 모두 검증·처리합니다.
    /// </summary>
    [ServerRpc]
    public void RequestAttackServerRpc(ulong targetNetworkObjectId)
    {
        if (NetworkIsDead.Value || _serverData == null) return;

        // 쿨다운 검증 (서버 기준, 클라이언트 조작 방지)
        if (Time.time - _lastAttackTime < _controller.attackCooldown) return;

        // 타겟 오브젝트 탐색
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                targetNetworkObjectId, out var targetNetObj)) return;

        var targetSync = targetNetObj.GetComponent<PlayerNetworkSync>();
        if (targetSync == null || targetSync.NetworkIsDead.Value) return;
        if (targetSync._serverData == null) return;

        // 거리 검증 (공격 범위의 1.5배까지 허용 — 네트워크 지연 보정)
        float dist = Vector2.Distance(transform.position, targetNetObj.transform.position);
        if (dist > _controller.attackRange * 1.5f) return;

        _lastAttackTime = Time.time;
        _serverData.lastCombatTime = Time.time;

        var attackerFx = _controller.StatusFX;
        var targetFx   = targetSync._controller.StatusFX;

        // ── 서버에서 데미지 계산 ──────────────────────────────
        DamageResult result = CombatSystem.CalculateDamage(
            _serverData, targetSync._serverData, attackerFx, targetFx);

        if (!result.isEvaded && !result.isDivineGraceBlocked && result.finalDamage > 0f)
        {
            // 흡혈·가시 반사 등 후처리
            CombatSystem.PostDamageEffects(
                _serverData, targetSync._serverData, attackerFx, targetFx, result.finalDamage);

            // 흡혈로 공격자 HP가 변동된 경우 동기화
            NetworkHp.Value = Mathf.Clamp(_serverData.currentHp, 0f, NetworkMaxHp.Value);
        }

        // 피격자 HP 업데이트
        float newHp = Mathf.Max(0f, targetSync.NetworkHp.Value - result.finalDamage);
        targetSync.NetworkHp.Value        = newHp;
        targetSync._serverData.currentHp = newHp;

        // 피격 이펙트를 모든 클라이언트에 전파
        targetSync.NotifyHitClientRpc(
            result.finalDamage, result.isEvaded, result.isCritical, result.isDivineGraceBlocked);

        // ── 사망 판정 ────────────────────────────────────────
        if (newHp <= 0f && !targetSync.NetworkIsDead.Value)
            ProcessDeath(targetSync, attackerFx, targetFx);
    }

    private void ProcessDeath(PlayerNetworkSync target, StatusEffectSystem attackerFx, StatusEffectSystem targetFx)
    {
        // Tenacity / Guardian Angel 생존 패시브 처리
        if (CombatSystem.TryGuardianAngel(target._serverData, targetFx))
        {
            target.NetworkHp.Value = target._serverData.currentHp;
            return;
        }
        if (CombatSystem.TryTenacity(target._serverData, targetFx))
        {
            target.NetworkHp.Value = target._serverData.currentHp;
            return;
        }

        // 사망 확정
        NetworkKillCount.Value++;
        target.NetworkIsDead.Value = true;
        target.DeclareDeathClientRpc(NetworkObject.NetworkObjectId);
        InGameManager.Instance?.OnPlayerDied(target._controller);

        Debug.Log($"[Server] {OwnerClientId} → {target.OwnerClientId} 처치! " +
                  $"(총 킬: {NetworkKillCount.Value})");
    }

    // ════════════════════════════════════════════════════════════
    //  ClientRpc — 서버 → 모든 클라이언트
    // ════════════════════════════════════════════════════════════

    /// <summary>피격 팝업을 모든 클라이언트 화면에 표시합니다.</summary>
    [ClientRpc]
    private void NotifyHitClientRpc(float damage, bool isEvaded, bool isCritical, bool isDivineGrace)
    {
        _controller.ShowDamagePopupNetwork(new DamageResult
        {
            finalDamage          = damage,
            isEvaded             = isEvaded,
            isCritical           = isCritical,
            isDivineGraceBlocked = isDivineGrace
        });
    }

    /// <summary>사망 애니메이션·효과를 모든 클라이언트에서 재생합니다.</summary>
    [ClientRpc]
    private void DeclareDeathClientRpc(ulong killerNetworkObjectId)
    {
        _controller.PlayDeathAnimation();

        if (IsOwner)
            Debug.Log("[Network] 내 캐릭터가 사망했습니다.");
    }

    // ════════════════════════════════════════════════════════════
    //  CharacterData 직렬화
    // ════════════════════════════════════════════════════════════

    private NetworkCharacterData BuildNetworkData()
    {
        var d = _controller.myData ?? GameManager.Instance?.myCharacterData;
        if (d == null)
        {
            Debug.LogWarning("[PlayerNetworkSync] CharacterData가 없습니다. 기본값으로 전송합니다.");
            return default;
        }

        var nd = new NetworkCharacterData
        {
            Job       = (int)d.job,
            Affinity  = (int)d.affinity,
            MaxHp     = d.maxHp,
            BaseAtk   = d.baseAtk,
            MoveSpeed = d.moveSpeed,
            Active0   = GetActive(d, 0),  Active1 = GetActive(d, 1),
            Active2   = GetActive(d, 2),  Active3 = GetActive(d, 3),
            Passive0  = GetPassive(d, 0), Passive1 = GetPassive(d, 1),
            Passive2  = GetPassive(d, 2), Passive3 = GetPassive(d, 3),
        };
        return nd;
    }

    private static int GetActive(CharacterData d, int i)
        => i < d.activeSkills.Count  ? (int)d.activeSkills[i]  : -1;
    private static int GetPassive(CharacterData d, int i)
        => i < d.passiveSkills.Count ? (int)d.passiveSkills[i] : -1;
}
