using Fusion;
using UnityEngine;

/// <summary>
/// [4-G] 설치형 덫 (궁수 Trap). 호스트가 지정 위치에 설치하고, 무장(arm) 지연 후
/// 적이 범위에 들어오면 발동 — 범위 내 모든 적에게 데미지 + 슬로우 후 소멸.
///
/// 권위 모델: StateAuthority(호스트)만 시뮬레이션/판정. 위치는 NetworkTransform이 동기화(정지).
/// NetPlayer.TrapPrefab이 배선돼 있을 때만 사용되며, 없으면 NetSkillSystem이 즉발 지점AoE로 폴백.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class NetTrap : NetworkBehaviour
{
    [Networked] private TickTimer Life { get; set; } // 자동 소멸
    [Networked] private TickTimer Arm  { get; set; } // 설치 직후 무장 지연(자기 설치 즉시 발동 방지)

    // ── host 전용(비네트워크) ───────────────────────────────────
    private NetPlayer _owner;
    private float     _damage;
    private float     _radius   = 1.4f;
    private float     _slowDur  = 3f;
    private float     _slowVal  = 0.5f;
    private float     _life     = 8f;
    private float     _armDelay = 0.4f;

    /// <summary>runner.Spawn의 onBeforeSpawned 콜백에서 StateAuthority가 호출.</summary>
    public void Setup(NetPlayer owner, float damage, float radius, float slowDur, float slowVal,
        float life = 8f, float armDelay = 0.4f)
    {
        _owner    = owner;
        _damage   = damage;
        _radius   = radius;
        _slowDur  = slowDur;
        _slowVal  = slowVal;
        _life     = life;
        _armDelay = armDelay;
    }

    public override void Spawned()
    {
        if (HasStateAuthority)
        {
            Life = TickTimer.CreateFromSeconds(Runner, _life);
            Arm  = TickTimer.CreateFromSeconds(Runner, _armDelay);
        }
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return; // 판정은 권위 측만, 클라는 NetworkTransform 위치만 동기화

        if (Life.Expired(Runner)) { Runner.Despawn(Object); return; }
        if (_owner == null || _owner.Object == null || !_owner.Object.IsValid) { Runner.Despawn(Object); return; }
        if (!Arm.ExpiredOrNotRunning(Runner)) return; // 아직 무장 전

        // 적이 범위에 들어왔는지 검사.
        bool triggered = false;
        foreach (var h in Physics2D.OverlapCircleAll(transform.position, _radius))
        {
            var t = h.GetComponent<NetPlayer>();
            if (t != null && t != _owner && !t.IsDead) { triggered = true; break; }
        }
        if (!triggered) return;

        // 발동: 범위 내 모든 적에게 데미지 + 슬로우 후 소멸.
        foreach (var h in Physics2D.OverlapCircleAll(transform.position, _radius))
        {
            var t = h.GetComponent<NetPlayer>();
            if (t == null || t == _owner || t.IsDead) continue;
            if (_owner.DealSkillDamage(t, _damage) && t.Status != null && !t.IsDead)
                t.Status.ApplySlow(_slowDur, _slowVal);
        }
        Runner.Despawn(Object);
    }
}
