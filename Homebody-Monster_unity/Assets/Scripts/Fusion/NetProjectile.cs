using Fusion;
using UnityEngine;

/// <summary>
/// [Phase 1 / Slice 1-D] Fusion 서버권위 투사체. NGO NetworkProjectile의 Fusion 대체.
///
///  • StateAuthority(호스트)가 runner.Spawn으로 생성하고 FixedUpdateNetwork에서 이동·적중 판정.
///  • 적중 판정은 Physics2D.OverlapCircle (트리거/Rigidbody 타이밍 의존 없이 결정적).
///  • 위치는 NetworkTransform이 동기화(클라는 보간만).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class NetProjectile : NetworkBehaviour
{
    [Tooltip("이동 속도(유닛/초)")] public float speed     = 12f;
    [Tooltip("자동 소멸 시간(초)")] public float lifeTime  = 3f;
    [Tooltip("적중 반경(유닛)")]   public float hitRadius = 0.5f;

    [Networked] private Vector2   Dir  { get; set; }
    [Networked] private TickTimer Life { get; set; }

    private NetPlayer         _owner;  // host 전용 (비네트워크)
    private float             _damage; // host 전용
    private NetSkillSystem.Fx _fx;     // host 전용 — 적중 시 상태이상 (4-C 일반화)
    private float             _fxDur;
    private float             _fxVal;
    private bool              _pierce; // [4-G] 관통: 첫 적중에 소멸하지 않고 통과
    private System.Collections.Generic.HashSet<NetPlayer> _pierced; // host 전용 — 관통 중복타 방지

    /// <summary>runner.Spawn의 onBeforeSpawned 콜백에서 StateAuthority가 호출.</summary>
    public void Setup(NetPlayer owner, Vector2 dir, float damage,
        NetSkillSystem.Fx fx = NetSkillSystem.Fx.None, float fxDur = 0f, float fxVal = 0f, bool pierce = false)
    {
        _owner  = owner;
        _damage = damage;
        _fx     = fx;
        _fxDur  = fxDur;
        _fxVal  = fxVal;
        _pierce = pierce;
        Dir     = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right;
    }

    public override void Spawned()
    {
        if (HasStateAuthority) Life = TickTimer.CreateFromSeconds(Runner, lifeTime);
        float ang = Mathf.Atan2(Dir.y, Dir.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.Euler(0f, 0f, ang);
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return; // 이동·판정은 권위 측만, 클라는 NetworkTransform 동기화

        transform.position += (Vector3)(Dir * speed * Runner.DeltaTime);

        if (Life.Expired(Runner)) { Runner.Despawn(Object); return; }

        // [4-A] 아레나 밖으로 나가면 소멸.
        if (!NetArena.Contains(transform.position)) { Runner.Despawn(Object); return; }

        var hits = Physics2D.OverlapCircleAll(transform.position, hitRadius);
        foreach (var h in hits)
        {
            var t = h.GetComponent<NetPlayer>();

            // [4-A] 벽/장애물(비트리거 콜라이더, 플레이어 아님) 충돌 → 소멸.
            if (t == null)
            {
                if (!h.isTrigger) { Runner.Despawn(Object); return; }
                continue;
            }
            if (t == _owner || t.IsDead) continue;
            if (_owner == null || _owner.Data == null || t.Data == null) { Runner.Despawn(Object); return; }

            // [4-G] 관통 — 이미 맞힌 대상은 통과(중복타 방지).
            if (_pierce && _pierced != null && _pierced.Contains(t)) continue;

            var result = CombatSystem.CalculateDamageWithOverride(
                _owner.Data, t.Data, _damage, _owner.Status, t.Status);
            if (!result.isEvaded && !result.isDivineGraceBlocked && result.finalDamage > 0f)
            {
                t.ReceiveDamage(result.finalDamage, _owner);
                // [4-C] 적중 상태이상 일반화 (Slow/Stun/Poison)
                if (_fx != NetSkillSystem.Fx.None && t.Status != null && !t.IsDead)
                {
                    switch (_fx)
                    {
                        case NetSkillSystem.Fx.Slow:   t.Status.ApplySlow(_fxDur, _fxVal);          break;
                        case NetSkillSystem.Fx.Stun:   t.Status.ApplyStun(_fxDur);                  break;
                        case NetSkillSystem.Fx.Poison: t.Status.ApplyPoison(_fxDur, _fxVal, _owner); break;
                    }
                }
            }

            // [4-G] 관통이면 소멸하지 않고 계속 진행(이 대상은 재타격 제외).
            if (_pierce)
            {
                _pierced ??= new System.Collections.Generic.HashSet<NetPlayer>();
                _pierced.Add(t);
                continue;
            }

            Runner.Despawn(Object);
            return;
        }
    }
}
