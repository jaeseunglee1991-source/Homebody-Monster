using Unity.Netcode;
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 서버 권한 물리 투사체 컴포넌트.
/// 프리팹에 NetworkObject, Rigidbody2D(Kinematic 권장), Collider2D(IsTrigger=true) 필수.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class NetworkProjectile : NetworkBehaviour
{
    [Header("투사체 설정")]
    public bool isPiercing = false;
    public int maxPierceCount = 1;
    public GameObject hitEffectPrefab;

    private float _damage;
    private ActiveSkillType _skillType;
    private PlayerNetworkSync _ownerSync;
    private Rigidbody2D _rb;
    private int _currentPierceCount = 0;
    
    // 이중 Despawn(크래시) 방지용 플래그
    private bool _isDespawned = false; 
    
    // 관통 무기용 중복 타격 방지 세트
    private HashSet<ulong> _hitTargets = new HashSet<ulong>();

    private void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
    }

    public void Initialize(float damage, ActiveSkillType skillType, PlayerNetworkSync owner, Vector2 direction, float speed, float lifeTime = 5f)
    {
        if (!IsServer) return;

        _damage = damage;
        _skillType = skillType;
        _ownerSync = owner;

        // 투사체가 발사 방향을 바라보게 회전
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.Euler(0f, 0f, angle);

        // Unity 6.0 이상 호환성 지원 (velocity -> linearVelocity)
        #if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = direction.normalized * speed;
        #else
            _rb.velocity = direction.normalized * speed;
        #endif

        // lifeTime 후 자동 소멸 (메모리 누수 방지)
        Invoke(nameof(DespawnProjectile), lifeTime);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!IsServer) return;

        // 투사체가 날아가는 도중 발사자가 나갔을 경우 즉시 소멸 (Null 방지)
        if (_ownerSync == null)
        {
            DespawnProjectile();
            return;
        }

        var targetSync = other.GetComponent<PlayerNetworkSync>();

        // 1. 발사자 본인과의 충돌 무시
        if (targetSync != null && targetSync.OwnerClientId == _ownerSync.OwnerClientId) return;

        // 2. 살아있는 적 플레이어 피격 처리
        if (targetSync != null && !targetSync.NetworkIsDead.Value)
        {
            if (_hitTargets.Contains(targetSync.OwnerClientId)) return;
            _hitTargets.Add(targetSync.OwnerClientId);

            ApplyDamageAndStatus(targetSync);

            if (hitEffectPrefab != null)
                SpawnHitEffectClientRpc(transform.position);

            _currentPierceCount++;
            if (!isPiercing || _currentPierceCount >= maxPierceCount)
            {
                DespawnProjectile();
            }
        }
        // 3. 장애물 충돌 처리
        else if (other.gameObject.layer == LayerMask.NameToLayer("Obstacle"))
        {
            if (hitEffectPrefab != null)
                SpawnHitEffectClientRpc(transform.position);
            DespawnProjectile();
        }
    }

    private void ApplyDamageAndStatus(PlayerNetworkSync targetSync)
    {
        var ownerController = _ownerSync.GetComponent<PlayerController>();
        var targetController = targetSync.GetComponent<PlayerController>();
        
        if (ownerController == null || targetController == null) return;

        // [Fix #2] baseAtk 임시 교체 패턴 제거 — 레이스 컨디션 방지.
        // 같은 소유자의 투사체가 같은 프레임에 여러 개 충돌하면 baseAtk 복구 전에
        // 다른 투사체가 덮어쓰는 경쟁 상태가 발생했었음.
        // CalculateDamageWithOverride로 overrideDamage를 직접 전달하여 해결.
        DamageResult result = CombatSystem.CalculateDamageWithOverride(
            _ownerSync.ServerData,
            targetSync.ServerData,
            _damage,
            ownerController.StatusFX,
            targetController.StatusFX
        );

        // [중요] result를 전달하여 UI 데미지 팝업(MISS, CRITICAL 등)이 표시되도록 연동
        if (!result.isEvaded && !result.isDivineGraceBlocked && result.finalDamage > 0f)
        {
            targetSync.ApplyDamageServer(result.finalDamage, _ownerSync, result);
        }

        ApplySkillDebuff(targetSync);
    }

    private void ApplySkillDebuff(PlayerNetworkSync targetSync)
    {
        switch (_skillType)
        {
            case ActiveSkillType.IceShards:
                targetSync.ApplyStatusEffectServer(StatusEffectType.Root, 1.5f, 0f, _ownerSync);
                break;
            case ActiveSkillType.Fireball:
                targetSync.ApplyStatusEffectServer(StatusEffectType.Burn, 3f, _ownerSync.ServerData.baseAtk * 0.5f, _ownerSync);
                break;
            case ActiveSkillType.PoisonDagger:
                targetSync.ApplyStatusEffectServer(StatusEffectType.Poison, 3f, _ownerSync.ServerData.baseAtk * 0.4f, _ownerSync);
                break;
        }
    }

    [ClientRpc]
    private void SpawnHitEffectClientRpc(Vector2 position)
    {
        if (hitEffectPrefab != null)
            Instantiate(hitEffectPrefab, position, Quaternion.identity);
    }

    private void DespawnProjectile()
    {
        if (!IsServer || _isDespawned) return;
        _isDespawned = true;
        
        CancelInvoke(nameof(DespawnProjectile));

        var netObj = GetComponent<NetworkObject>();
        if (netObj != null && netObj.IsSpawned)
            netObj.Despawn();
    }

    public override void OnNetworkDespawn()
    {
        _isDespawned = true;
        CancelInvoke(nameof(DespawnProjectile));
        base.OnNetworkDespawn();
    }
}
