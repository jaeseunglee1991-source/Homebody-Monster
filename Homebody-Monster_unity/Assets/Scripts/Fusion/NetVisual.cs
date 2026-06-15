using UnityEngine;

/// <summary>
/// [Phase 2-E] NetPlayer 실제 직업 비주얼 (JobVisualRegistry 재사용 — 실 cutover 준비).
/// 클라 로컬 전용(네트워크 무관) — 모든 판단을 [Networked] 상태(Job/Hp/IsDead/쿨다운)의
/// 변화 관찰로 처리하므로 추가 RPC가 필요 없다.
///
///  • Job 변화      → JobVisualRegistry에서 AnimatorController+스프라이트 적용
///  • 위치 변화     → IsMoving(애니) + flipX (원격 피어는 NetworkTransform 보간 델타로 동작)
///  • 평타 쿨다운 ↑ → "Attack" 트리거 (TickTimer가 [Networked]라 모든 피어에서 감지됨)
///  • Hp 감소       → "Hurt" 트리거
///  • IsDead 변화   → "Die" 트리거 / 부활 시 Rebind
/// </summary>
[RequireComponent(typeof(NetPlayer))]
public class NetVisual : MonoBehaviour
{
    [Tooltip("직업 비주얼 적용 시 캐릭터 스케일 (PoC 사각형은 4였음 — 실 스프라이트는 1 권장)")]
    public float jobVisualScale = 1f;

    /// <summary>직업 비주얼이 적용됐는지 — NetPlayer가 HP 틴트를 양보할지 판단.</summary>
    public bool HasJobVisual { get; private set; }

    private NetPlayer      _p;
    private SpriteRenderer _sr;
    private Animator       _anim;

    private int     _appliedJob = -2;
    private float   _lastHp     = -1f;
    private bool    _lastDead;
    private float   _lastAtkCd;
    private Vector3 _lastPos;

    private void Awake()
    {
        _p  = GetComponent<NetPlayer>();
        _sr = GetComponentInChildren<SpriteRenderer>();
        _lastPos = transform.position;
    }

    private void LateUpdate()
    {
        if (_p == null || _p.Object == null || !_p.Object.IsValid) return;

        ApplyJobIfChanged();

        // ── 이동 애니/방향 (위치 델타 기반 — 원격은 보간 델타) ──
        Vector3 delta = transform.position - _lastPos;
        _lastPos = transform.position;
        bool moving = !_p.IsDead && delta.sqrMagnitude > 1e-8f;
        if (_anim != null && _anim.runtimeAnimatorController != null && HasParam("IsMoving"))
            _anim.SetBool("IsMoving", moving);
        if (_sr != null && Mathf.Abs(delta.x) > 1e-6f)
            _sr.flipX = delta.x < 0f;

        // ── [4-E] 은신 투명도 — 본인은 반투명(상태 인지), 적은 거의 안 보임 ──
        if (_sr != null)
        {
            bool  stealthed = _p.Status != null && _p.Status.IsStealthy;
            float targetA   = stealthed ? (_p.HasInputAuthority ? 0.5f : 0.12f) : 1f;
            var   col       = _sr.color;
            if (!Mathf.Approximately(col.a, targetA)) { col.a = targetA; _sr.color = col; }
        }

        // ── 평타 트리거 (쿨다운 0→양수 점프 = 공격 발생) ──
        float atkCd = _p.CooldownRemaining(0);
        if (atkCd > _lastAtkCd + 0.05f && !_p.IsDead) Trigger("Attack");
        _lastAtkCd = atkCd;

        // ── 피격 ──
        if (_lastHp >= 0f && _p.Hp < _lastHp - 0.01f && !_p.IsDead) Trigger("Hurt");
        _lastHp = _p.Hp;

        // ── 사망/부활 ──
        if (_p.IsDead != _lastDead)
        {
            _lastDead = _p.IsDead;
            if (_p.IsDead) Trigger("Die");
            else if (_anim != null && _anim.runtimeAnimatorController != null)
            {
                _anim.Rebind();
                _anim.Update(0f);
            }
        }
    }

    private void ApplyJobIfChanged()
    {
        int job = _p.Job;
        if (job == _appliedJob || job < 0) return;
        _appliedJob = job;

        var registry = JobVisualRegistry.Instance;
        if (registry == null || !registry.TryGetVisual((JobType)job, out var visual)) return;

        if (_sr != null && visual.defaultSprite != null)
        {
            _sr.sprite = visual.defaultSprite;
            _sr.color  = Color.white; // PoC HP 틴트 제거 (HP는 머리위 바가 담당)
        }

        if (visual.animatorController != null)
        {
            if (_anim == null)
            {
                // PoC 프리팹은 SpriteRenderer가 루트에 있으므로 같은 GO에 Animator 부착.
                _anim = _sr != null ? _sr.GetComponent<Animator>() : GetComponent<Animator>();
                if (_anim == null)
                    _anim = (_sr != null ? _sr.gameObject : gameObject).AddComponent<Animator>();
            }
            _anim.runtimeAnimatorController = visual.animatorController;
            _anim.Rebind();
            _anim.Update(0f);
        }

        // PoC 사각형용 스케일(4)/콜라이더(0.25)를 실 스프라이트 기준으로 보정.
        transform.localScale = Vector3.one * jobVisualScale;
        var bc = GetComponent<BoxCollider2D>();
        if (bc != null) bc.size = Vector2.one;

        HasJobVisual = true;
    }

    private void Trigger(string name)
    {
        if (_anim == null || _anim.runtimeAnimatorController == null) return;
        if (HasParam(name)) _anim.SetTrigger(name);
    }

    private bool HasParam(string name)
    {
        foreach (var prm in _anim.parameters)
            if (prm.name == name) return true;
        return false;
    }
}
