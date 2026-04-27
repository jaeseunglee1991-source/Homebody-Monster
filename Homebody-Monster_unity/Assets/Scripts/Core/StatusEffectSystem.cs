using UnityEngine;
using System.Collections.Generic;

// ════════════════════════════════════════════════════════════════
//  StatusEffect 데이터 클래스 — 원본 동일 유지
// ════════════════════════════════════════════════════════════════
[System.Serializable]
public class StatusEffect
{
    public StatusEffectType  type;
    public float             duration;
    public float             value;
    public float             elapsed;
    public float             dotTickTimer;
    public PlayerController  source;
    public float Remaining => Mathf.Max(0f, duration - elapsed);
}

// ════════════════════════════════════════════════════════════════
//  StatusEffectSystem
//
//  [네트워크 구조]
//  • ApplyEffectNetwork : ClientRpc 경유로 서버/클라이언트 모두에 호출됨 (주 진입점)
//  • ApplyEffectServer  : 서버 전용 직접 적용 (하위 호환 유지)
//  • TickEffects        : DoT는 서버에서만 ApplyDamageServer 호출 → 팝업은 NotifyHitClientRpc
// ════════════════════════════════════════════════════════════════
[RequireComponent(typeof(PlayerController))]
public class StatusEffectSystem : MonoBehaviour
{
    private PlayerController     owner;
    private readonly List<StatusEffect> effects = new List<StatusEffect>();

    // ── 프로퍼티 — 원본 동일 유지 ────────────────────────────────
    public bool IsStunned         => HasEffect(StatusEffectType.Stun);
    public bool IsRooted          => HasEffect(StatusEffectType.Root) || IsStunned;
    public bool IsSilenced        => HasEffect(StatusEffectType.IceShield);
    public bool IsImmune          => HasEffect(StatusEffectType.IceShield) || HasEffect(StatusEffectType.TenacityShield);
    public bool IsStealthy        => HasEffect(StatusEffectType.Stealth);
    public bool IsInDefenseStance => HasEffect(StatusEffectType.DefenseStance);
    public bool IsInUndyingRage   => HasEffect(StatusEffectType.UndyingRage);
    public bool IsInBladeStorm    => HasEffect(StatusEffectType.BladeStormActive);
    public bool HasDivineGrace    => HasEffect(StatusEffectType.DivineGrace);
    public bool HasGuardianAngel  => HasEffect(StatusEffectType.GuardianAngel);
    public bool HasDeathMark      => HasEffect(StatusEffectType.DeathMarkTarget);

    private void Awake() { owner = GetComponent<PlayerController>(); }

    private void Update()
    {
        if (owner == null || owner.IsDead) return;
        TickEffects();
    }

    // ════════════════════════════════════════════════════════════
    //  상태이상 적용 진입점
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// [주 진입점] PlayerNetworkSync.SyncStatusEffectClientRpc에서 호출됨.
    /// 서버와 모든 클라이언트에서 동일하게 실행되어 로컬 효과를 적용합니다.
    /// </summary>
    public void ApplyEffectNetwork(StatusEffectType type, float duration, float value = 0f, PlayerController source = null)
    {
        InternalApplyEffect(type, duration, value, source);
    }

    /// <summary>
    /// [하위 호환] 서버 로컬에서 직접 적용이 필요한 경우 (단독 사용 시 클라이언트에 전파 안 됨).
    /// 반드시 ApplyStatusEffectServer(PlayerNetworkSync)를 통해 호출하세요.
    /// </summary>
    public void ApplyEffectServer(StatusEffectType type, float duration, float value = 0f, PlayerController source = null)
    {
        InternalApplyEffect(type, duration, value, source);
    }

    /// <summary>[구형 로컬 전용 API — 싱글/테스트용으로 유지]</summary>
    public void ApplyEffect(StatusEffectType type, float duration, float value = 0f, PlayerController source = null)
    {
        InternalApplyEffect(type, duration, value, source);
    }

    private void InternalApplyEffect(StatusEffectType type, float duration, float value, PlayerController source)
    {
        if (IsImmune && IsDebuff(type))          return;
        if (IsInBladeStorm && IsCrowdControl(type)) return;

        var existing = effects.Find(e => e.type == type);
        if (existing != null)
        {
            // 지속시간 갱신 (서버-클라이언트 타이머 차이 보정)
            existing.duration = Mathf.Max(existing.duration, existing.elapsed + duration);
            existing.value    = value;
            return;
        }

        var effect = new StatusEffect
        {
            type = type, duration = duration, value = value,
            elapsed = 0f, dotTickTimer = 0f, source = source
        };
        effects.Add(effect);
        OnEffectApplied(effect);
    }

    // ════════════════════════════════════════════════════════════
    //  틱 처리
    // ════════════════════════════════════════════════════════════

    private void TickEffects()
    {
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            var e = effects[i];
            e.elapsed += Time.deltaTime;

            // DoT: 서버에서만 데미지 연산 (클라이언트는 팝업만 받음)
            if (IsDoT(e.type))
            {
                e.dotTickTimer += Time.deltaTime;
                while (e.dotTickTimer >= 1f)
                {
                    e.dotTickTimer -= 1f;
                    ApplyDotTick(e);
                    if (owner.IsDead) return;
                }
            }

            if (e.elapsed >= e.duration)
            {
                effects.RemoveAt(i);
                OnEffectExpired(e.type);
            }
        }
    }

    private void ApplyDotTick(StatusEffect e)
    {
        var netSync = owner.networkSync;

        if (netSync != null && netSync.IsServer)
        {
            // 서버: ApplyDamageServer → NotifyHitClientRpc로 팝업 전파
            // attacker가 null인 경우(소환물 DoT 등) 안전하게 null 전달
            PlayerNetworkSync sourceSync = e.source != null ? e.source.networkSync : null;
            netSync.ApplyDamageServer(e.value, sourceSync);
        }
        else if (netSync == null)
        {
            // 싱글/테스트 폴백: 로컬 데미지 처리
            if (owner.myData == null) return;
            owner.myData.currentHp = Mathf.Max(owner.myData.currentHp - e.value, 0f);
            if (owner.myData.deathMarkActive) owner.myData.deathMarkAccumulated += e.value;
            ShowDoTPopupLocal(e);
            if (owner.IsLocalPlayer && InGameHUD.Instance != null)
                InGameHUD.Instance.UpdateHealthBar(owner.myData.currentHp, owner.myData.maxHp);
            if (owner.myData.currentHp <= 0f) owner.PlayDeathAnimation();
        }
        // 클라이언트(비호스트): 팝업은 NotifyHitClientRpc가 처리하므로 여기서 아무것도 안 함
    }

    private void ShowDoTPopupLocal(StatusEffect e)
    {
        Color col = e.type switch
        {
            StatusEffectType.Poison => new Color(0.2f, 0.9f, 0.2f),
            StatusEffectType.Bleed  => new Color(0.9f, 0.1f, 0.1f),
            StatusEffectType.Burn   => new Color(1f, 0.45f, 0f),
            _                       => Color.white
        };
        owner.ShowDotPopup(e.value, col);
    }

    // ════════════════════════════════════════════════════════════
    //  효과 적용/만료 — 원본 동일 유지
    // ════════════════════════════════════════════════════════════

    private void OnEffectApplied(StatusEffect e)
    {
        switch (e.type)
        {
            case StatusEffectType.Slow:
                owner.RecalculateMoveSpeed(); break;
            case StatusEffectType.Stun:
            case StatusEffectType.Root:
                owner.SetMovementLocked(true); break;
            case StatusEffectType.IceShield:
                owner.SetMovementLocked(true);
                owner.SetAttackLocked(true); break;
            case StatusEffectType.Stealth:
                if (owner.myData != null) owner.myData.stealthFirstAttack = true;
                SetSpriteAlpha(0.3f); break;
            case StatusEffectType.ShieldHp:
                if (owner.myData != null) owner.myData.shieldHp = e.value; break;
            case StatusEffectType.DeathMarkTarget:
                if (owner.myData != null)
                {
                    owner.myData.deathMarkActive      = true;
                    owner.myData.deathMarkAccumulated = 0f;
                }
                break;
        }
    }

    private void OnEffectExpired(StatusEffectType type)
    {
        switch (type)
        {
            case StatusEffectType.Slow:
                owner.RecalculateMoveSpeed(); break;
            case StatusEffectType.Stun:
            case StatusEffectType.Root:
                if (!IsRooted) owner.SetMovementLocked(false); break;
            case StatusEffectType.IceShield:
                owner.SetMovementLocked(false);
                owner.SetAttackLocked(false); break;
            case StatusEffectType.Stealth:
                if (owner.myData != null) owner.myData.stealthFirstAttack = false;
                SetSpriteAlpha(1f); break;
            case StatusEffectType.ShieldHp:
                if (owner.myData != null) owner.myData.shieldHp = 0f; break;
            case StatusEffectType.DeathMarkTarget:
                if (owner.myData != null) owner.myData.deathMarkActive = false; break;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  공개 유틸 — 원본 동일 유지
    // ════════════════════════════════════════════════════════════

    public void RemoveEffect(StatusEffectType type)
    {
        int removed = effects.RemoveAll(e => e.type == type);
        if (removed > 0) OnEffectExpired(type);
    }

    public void RemoveAllDebuffs()
    {
        var toRemove = new List<StatusEffectType>();
        foreach (var e in effects) if (IsDebuff(e.type)) toRemove.Add(e.type);
        foreach (var t in toRemove) RemoveEffect(t);
        owner.RecalculateMoveSpeed();
    }

    public bool  HasEffect(StatusEffectType type) => effects.Exists(e => e.type == type);
    public float GetEffectValue(StatusEffectType type) { var e = effects.Find(x => x.type == type); return e?.value ?? 0f; }

    public float GetMoveSpeedMultiplier()
    {
        if (IsStunned || IsRooted) return 0f;
        return 1f - GetEffectValue(StatusEffectType.Slow);
    }

    public float GetAtkMultiplier() => Mathf.Max(0.1f, 1f - GetEffectValue(StatusEffectType.AtkReduction));

    public bool ConsumeDivineGrace()
    {
        if (!HasDivineGrace) return false;
        RemoveEffect(StatusEffectType.DivineGrace);
        return true;
    }

    public float AbsorbWithShield(float incomingDamage)
    {
        if (owner.myData == null || owner.myData.shieldHp <= 0f) return incomingDamage;
        float absorbed = Mathf.Min(owner.myData.shieldHp, incomingDamage);
        owner.myData.shieldHp -= absorbed;
        if (owner.myData.shieldHp <= 0f)
        {
            owner.myData.shieldHp = 0f;
            RemoveEffect(StatusEffectType.ShieldHp);
        }
        return incomingDamage - absorbed;
    }

    // ════════════════════════════════════════════════════════════
    //  내부 유틸
    // ════════════════════════════════════════════════════════════

    private void SetSpriteAlpha(float a)
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr == null) return;
        var col = sr.color; col.a = a; sr.color = col;
    }

    private static bool IsDebuff(StatusEffectType t) =>
        t == StatusEffectType.Slow     || t == StatusEffectType.Stun   ||
        t == StatusEffectType.Root     || t == StatusEffectType.Poison  ||
        t == StatusEffectType.Bleed    || t == StatusEffectType.Burn    ||
        t == StatusEffectType.AtkReduction || t == StatusEffectType.Knockback;

    private static bool IsDoT(StatusEffectType t) =>
        t == StatusEffectType.Poison || t == StatusEffectType.Bleed || t == StatusEffectType.Burn;

    private static bool IsCrowdControl(StatusEffectType t) =>
        t == StatusEffectType.Stun || t == StatusEffectType.Root || t == StatusEffectType.Knockback;
}
