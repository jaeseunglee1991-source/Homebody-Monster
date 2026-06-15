using Fusion;
using UnityEngine;

/// <summary>
/// [Phase 1 / Slice 1-C] Fusion 네트워크 상태이상.
/// NGO StatusEffectSystem(PlayerController 강결합)을 NetPlayer에 붙일 수 없어 [Networked] 기반으로 재구현.
/// CombatSystem과는 ICombatStatus 인터페이스로 연결되어 데미지 공식을 공유한다.
///
/// 권위 모델: 적용/틱은 StateAuthority(호스트)에서만. 효과 상태는 [Networked]라 자동 동기화.
/// 범위(1-C): Slow / Stun·Root / ShieldHp / DefenseStance / Poison(DoT). 나머지는 후속.
/// </summary>
[RequireComponent(typeof(NetPlayer))]
public class NetStatus : NetworkBehaviour, ICombatStatus
{
    [Networked] private TickTimer SlowTimer    { get; set; }
    [Networked] private float     SlowValue    { get; set; }
    [Networked] private TickTimer StunTimer    { get; set; } // Stun/Root → 이동잠금
    [Networked] private TickTimer ShieldTimer  { get; set; }
    [Networked] private float     ShieldHp     { get; set; }
    [Networked] private TickTimer DefenseTimer { get; set; }
    [Networked] private TickTimer PoisonTimer  { get; set; }
    [Networked] private float     PoisonDps    { get; set; }
    [Networked] private TickTimer PoisonNext   { get; set; }
    [Networked] private TickTimer StealthTimer { get; set; } // [4-E] 은신

    // ── [4-G] 본래 메커니즘 버프 ────────────────────────────────
    [Networked] private TickTimer DivineGraceTimer   { get; set; } // 신의 가호: 1회 완전 무효
    [Networked] private TickTimer IceShieldTimer     { get; set; } // 얼음 방패: 면역 + 이동·공격 잠금(침묵)
    [Networked] private TickTimer UndyingRageTimer   { get; set; } // 불굴의 분노: 흡혈 50% + 피해 증가/수신 증가
    [Networked] private TickTimer GuardianAngelTimer { get; set; } // 수호천사: 치명타 시 1회 소생(30% HP)

    private NetPlayer _player;
    private bool      _stealthApplied; // host: 자연 만료 시 stealthFirstAttack 정리용
    private void Awake() => _player = GetComponent<NetPlayer>();

    /// <summary>은신 중 — 적의 클릭 타겟 불가 + 시각 투명 (4-E).</summary>
    public bool IsStealthy => !StealthTimer.ExpiredOrNotRunning(Runner);

    public void ApplyStealth(float dur)
    {
        StealthTimer    = TickTimer.CreateFromSeconds(Runner, dur);
        _stealthApplied = true;
        if (_player != null && _player.Data != null) _player.Data.stealthFirstAttack = true; // CombatSystem 첫타 1.5배
    }

    public void BreakStealth()
    {
        StealthTimer    = default;
        _stealthApplied = false;
        if (_player != null && _player.Data != null) _player.Data.stealthFirstAttack = false;
    }

    // ── [4-F] 죽음의 낙인 ───────────────────────────────────────
    [Networked] private TickTimer DeathMarkTimer { get; set; }
    private NetPlayer _deathMarkCaster; // host 전용
    private float     _deathMarkAccum;  // host 전용 — 낙인 중 대상이 받은 누적 피해

    public bool IsDeathMarked => !DeathMarkTimer.ExpiredOrNotRunning(Runner);

    public void ApplyDeathMark(float dur, NetPlayer caster)
    {
        if (IsImmune) return;
        DeathMarkTimer   = TickTimer.CreateFromSeconds(Runner, dur);
        _deathMarkCaster = caster;
        _deathMarkAccum  = 0f;
    }

    /// <summary>대상이 피해를 받을 때 누적 (NetPlayer.ReceiveDamage에서 호스트 호출).</summary>
    public void NotifyDeathMarkDamage(float dmg)
    {
        if (_deathMarkCaster != null && !DeathMarkTimer.ExpiredOrNotRunning(Runner))
            _deathMarkAccum += dmg;
    }

    /// <summary>
    /// 낙인 폭발 (host). isKill=사망 시 주변 체이닝 AoE(baseAtk*2+누적*0.5),
    /// false=시간 만료 시 대상에게 잔여(누적*0.35). 호출 전 먼저 상태를 비워 재진입을 막는다.
    /// </summary>
    public void ExplodeDeathMark(bool isKill)
    {
        var   caster = _deathMarkCaster;
        float accum  = _deathMarkAccum;
        DeathMarkTimer = default; _deathMarkCaster = null; _deathMarkAccum = 0f; // 선-정리

        if (caster == null || caster.Object == null || !caster.Object.IsValid
            || caster.IsDead || caster.Data == null || _player == null) return;

        if (isKill)
        {
            float dmg = caster.Data.baseAtk * 2f + accum * 0.5f;
            if (dmg <= 0f) return;
            foreach (var h in Physics2D.OverlapCircleAll(_player.transform.position, 2.5f))
            {
                var t = h.GetComponent<NetPlayer>();
                if (t == null || t == caster || t == _player || t.IsDead) continue;
                caster.DealSkillDamage(t, dmg * 0.6f); // 체이닝 0.6배
            }
        }
        else if (accum > 0f && !_player.IsDead)
        {
            _player.ReceiveDamage(accum * 0.35f, caster); // 도망친 대상 잔여 폭발
        }
    }

    // ── 적용 (StateAuthority) ──────────────────────────────────
    // 면역(IceShield) 중에는 디버프가 적용되지 않는다(원본 InternalApplyEffect와 동일).
    public void ApplySlow(float dur, float val)
    {
        if (IsImmune) return;
        SlowTimer = TickTimer.CreateFromSeconds(Runner, dur);
        SlowValue = Mathf.Max(SlowValue, val);
    }

    public void ApplyStun(float dur)          { if (!IsImmune) StunTimer = TickTimer.CreateFromSeconds(Runner, dur); }
    public void ApplyDefenseStance(float dur) => DefenseTimer = TickTimer.CreateFromSeconds(Runner, dur);

    // ── [4-G] 본래 메커니즘 버프 적용 (StateAuthority) ──────────
    public void ApplyDivineGrace(float dur)   => DivineGraceTimer   = TickTimer.CreateFromSeconds(Runner, dur);
    public void ApplyIceShield(float dur)     => IceShieldTimer     = TickTimer.CreateFromSeconds(Runner, dur);
    public void ApplyUndyingRage(float dur)   => UndyingRageTimer   = TickTimer.CreateFromSeconds(Runner, dur);
    public void ApplyGuardianAngel(float dur) => GuardianAngelTimer = TickTimer.CreateFromSeconds(Runner, dur);

    /// <summary>디버프 정화(SnackTime) — 슬로우/스턴/독/낙인 제거.</summary>
    public void CleanseDebuffs()
    {
        SlowTimer = default; SlowValue = 0f;
        StunTimer = default;
        PoisonTimer = default;
        DeathMarkTimer = default; _deathMarkCaster = null; _deathMarkAccum = 0f;
    }

    public void ApplyShield(float dur, float val)
    {
        ShieldTimer = TickTimer.CreateFromSeconds(Runner, dur);
        ShieldHp    = Mathf.Max(ShieldHp, val);
        if (_player != null && _player.Data != null) _player.Data.shieldHp = ShieldHp; // CombatSystem 분기용 미러
    }

    // DoT 시전자 — 호스트 전용(틱·킬크레딧 모두 호스트에서 처리되므로 비네트워크 참조로 충분).
    private NetPlayer _poisonSource;

    public void ApplyPoison(float dur, float dps, NetPlayer source = null)
    {
        if (IsImmune) return;
        PoisonTimer   = TickTimer.CreateFromSeconds(Runner, dur);
        PoisonDps     = dps;
        _poisonSource = source;
        if (PoisonNext.ExpiredOrNotRunning(Runner))
            PoisonNext = TickTimer.CreateFromSeconds(Runner, 1f);
    }

    // ── 틱 (StateAuthority): DoT + 실드 만료 정리 ───────────────
    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;

        // 실드 만료 → CharacterData 미러 정리
        if (ShieldTimer.ExpiredOrNotRunning(Runner) && ShieldHp > 0f)
        {
            ShieldHp = 0f;
            if (_player != null && _player.Data != null) _player.Data.shieldHp = 0f;
        }

        // Poison DoT — 1초마다 권위 HP 차감 (시전자에게 킬 크레딧)
        if (!PoisonTimer.ExpiredOrNotRunning(Runner) && PoisonNext.Expired(Runner))
        {
            PoisonNext = TickTimer.CreateFromSeconds(Runner, 1f);
            if (_player != null) _player.ReceiveDamage(PoisonDps, _poisonSource);
        }

        // [4-E] 은신 자연 만료 → 첫타 보너스 정리 (공격 없이 시간만 흘려보낸 경우).
        if (_stealthApplied && StealthTimer.ExpiredOrNotRunning(Runner))
        {
            _stealthApplied = false;
            if (_player != null && _player.Data != null) _player.Data.stealthFirstAttack = false;
        }

        // [4-F] 낙인 시간 만료(대상 생존) → 잔여 폭발 + 정리. (사망 폭발은 ReceiveDamage 경로)
        if (_deathMarkCaster != null && DeathMarkTimer.ExpiredOrNotRunning(Runner))
            ExplodeDeathMark(false);
    }

    // ── NetPlayer 이동/행동 질의 ───────────────────────────────
    // 이동 잠금 = 스턴/루트 또는 얼음방패(면역 중 자기 행동 정지).
    public bool IsMovementLocked =>
        !StunTimer.ExpiredOrNotRunning(Runner) || !IceShieldTimer.ExpiredOrNotRunning(Runner);

    /// <summary>행동(평타/스킬) 잠금 = 얼음방패(침묵). NetPlayer가 공격/스킬 RPC에서 검사.</summary>
    public bool IsActionLocked => !IceShieldTimer.ExpiredOrNotRunning(Runner);

    public float MoveSpeedMultiplier => IsMovementLocked
        ? 0f
        : Mathf.Max(0f, 1f - (SlowTimer.ExpiredOrNotRunning(Runner) ? 0f : SlowValue));

    // ── ICombatStatus (StateAuthority 컨텍스트=공격 RPC에서 호출) ──
    // 불굴의 분노 중 가하는 피해 +30% (CombatSystem이 공격자 GetAtkMultiplier를 곱함).
    public float GetAtkMultiplier() => IsInUndyingRage ? 1.3f : 1f;

    /// <summary>신의 가호: 1회 완전 무효(소모성). 피격 1회당 호출돼 활성 시 소진하고 true.</summary>
    public bool ConsumeDivineGrace()
    {
        if (DivineGraceTimer.ExpiredOrNotRunning(Runner)) return false;
        DivineGraceTimer = default; // 1회 소진
        return true;
    }

    /// <summary>얼음 방패 면역 — 모든 피해 회피(CombatSystem) + 디버프 차단.</summary>
    public bool IsImmune => !IceShieldTimer.ExpiredOrNotRunning(Runner);

    public bool IsInDefenseStance => !DefenseTimer.ExpiredOrNotRunning(Runner);
    public bool IsInUndyingRage   => !UndyingRageTimer.ExpiredOrNotRunning(Runner);

    /// <summary>수호천사 보유(치명타 소생 가능) 여부.</summary>
    public bool HasGuardianAngel => !GuardianAngelTimer.ExpiredOrNotRunning(Runner);

    /// <summary>신의 가호(1회 무효) 보유 여부 — HUD 표시용.</summary>
    public bool HasDivineGrace => !DivineGraceTimer.ExpiredOrNotRunning(Runner);

    // ── HUD 표시용 상태 질의 (읽기 전용) ────────────────────────
    public bool IsSlowed   => !SlowTimer.ExpiredOrNotRunning(Runner);
    public bool IsStunned  => !StunTimer.ExpiredOrNotRunning(Runner);
    public bool IsPoisoned => !PoisonTimer.ExpiredOrNotRunning(Runner);
    public bool HasShield  => !ShieldTimer.ExpiredOrNotRunning(Runner) && ShieldHp > 0f;

    /// <summary>치명타 시 1회 소생을 소모(NetPlayer.ReceiveDamage에서 호출). 소진 시 true.</summary>
    public bool ConsumeGuardianAngel()
    {
        if (GuardianAngelTimer.ExpiredOrNotRunning(Runner)) return false;
        GuardianAngelTimer = default;
        return true;
    }

    public float AbsorbWithShield(float incomingDamage)
    {
        if (ShieldTimer.ExpiredOrNotRunning(Runner) || ShieldHp <= 0f) return incomingDamage;
        float absorbed = Mathf.Min(ShieldHp, incomingDamage);
        ShieldHp -= absorbed;
        if (_player != null && _player.Data != null) _player.Data.shieldHp = ShieldHp;
        return incomingDamage - absorbed;
    }
}
