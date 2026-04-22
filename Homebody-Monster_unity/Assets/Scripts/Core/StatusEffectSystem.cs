using UnityEngine;
using System.Collections;
using System.Collections.Generic;

[System.Serializable]
public class StatusEffect
{
    public StatusEffectType type;
    public float duration;
    public float value;
    public float elapsed;
    public float dotTickTimer;
    public PlayerController source;
    public float Remaining => Mathf.Max(0f, duration - elapsed);
}

[RequireComponent(typeof(PlayerController))]
public class StatusEffectSystem : MonoBehaviour
{
    private PlayerController owner;
    private readonly List<StatusEffect> effects = new List<StatusEffect>();

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
    private void Update() { if (owner == null || owner.IsDead) return; TickEffects(); }

    public void ApplyEffect(StatusEffectType type, float duration, float value = 0f, PlayerController source = null)
    {
        if (IsImmune && IsDebuff(type)) return;
        if (IsInBladeStorm && IsCrowdControl(type)) return;
        var existing = effects.Find(e => e.type == type);
        if (existing != null) { existing.duration = Mathf.Max(existing.duration, existing.elapsed + duration); existing.value = value; return; }
        var effect = new StatusEffect { type = type, duration = duration, value = value, elapsed = 0f, dotTickTimer = 0f, source = source };
        effects.Add(effect);
        OnEffectApplied(effect);
    }

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

    private void TickEffects()
    {
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            var e = effects[i];
            e.elapsed += Time.deltaTime;
            if (IsDoT(e.type))
            {
                e.dotTickTimer += Time.deltaTime;
                while (e.dotTickTimer >= 1f) { e.dotTickTimer -= 1f; ApplyDotTick(e); if (owner.IsDead) return; }
            }
            if (e.elapsed >= e.duration) { effects.RemoveAt(i); OnEffectExpired(e.type); }
        }
    }

    private void ApplyDotTick(StatusEffect e)
    {
        if (owner.myData == null) return;
        float dmg = e.value;
        owner.myData.currentHp -= dmg;
        owner.myData.currentHp = Mathf.Max(owner.myData.currentHp, 0f);
        if (owner.myData.deathMarkActive) owner.myData.deathMarkAccumulated += dmg;
        Color col = e.type switch {
            StatusEffectType.Poison => new Color(0.2f, 0.9f, 0.2f),
            StatusEffectType.Bleed  => new Color(0.9f, 0.1f, 0.1f),
            StatusEffectType.Burn   => new Color(1f, 0.45f, 0f),
            _ => Color.white
        };
        owner.ShowDotPopup(dmg, col);
        if (owner.IsLocalPlayer && InGameHUD.Instance != null)
            InGameHUD.Instance.UpdateHealthBar(owner.myData.currentHp, owner.myData.maxHp);
        if (owner.myData.currentHp <= 0f) owner.TakeDotDeath(e.source);
    }

    private void OnEffectApplied(StatusEffect e)
    {
        switch (e.type)
        {
            case StatusEffectType.Slow: owner.RecalculateMoveSpeed(); break;
            case StatusEffectType.Stun: case StatusEffectType.Root: owner.SetMovementLocked(true); break;
            case StatusEffectType.IceShield: owner.SetMovementLocked(true); owner.SetAttackLocked(true); break;
            case StatusEffectType.Stealth: owner.myData.stealthFirstAttack = true; SetSpriteAlpha(0.3f); break;
            case StatusEffectType.TenacityShield: break;
            case StatusEffectType.DivineGrace: break;
            case StatusEffectType.ShieldHp: owner.myData.shieldHp = e.value; break;
            case StatusEffectType.DeathMarkTarget: owner.myData.deathMarkActive = true; owner.myData.deathMarkAccumulated = 0f; break;
        }
    }

    private void OnEffectExpired(StatusEffectType type)
    {
        switch (type)
        {
            case StatusEffectType.Slow: owner.RecalculateMoveSpeed(); break;
            case StatusEffectType.Stun: case StatusEffectType.Root: if (!IsRooted) owner.SetMovementLocked(false); break;
            case StatusEffectType.IceShield: owner.SetMovementLocked(false); owner.SetAttackLocked(false); break;
            case StatusEffectType.Stealth: owner.myData.stealthFirstAttack = false; SetSpriteAlpha(1f); break;
            case StatusEffectType.ShieldHp: owner.myData.shieldHp = 0f; break;
            case StatusEffectType.DeathMarkTarget: owner.myData.deathMarkActive = false; break;
        }
    }

    public bool HasEffect(StatusEffectType type) => effects.Exists(e => e.type == type);
    public float GetEffectValue(StatusEffectType type) { var e = effects.Find(x => x.type == type); return e?.value ?? 0f; }
    public float GetMoveSpeedMultiplier() { if (IsStunned || IsRooted) return 0f; return 1f - GetEffectValue(StatusEffectType.Slow); }
    public float GetAtkMultiplier() => Mathf.Max(0.1f, 1f - GetEffectValue(StatusEffectType.AtkReduction));

    public bool ConsumeDivineGrace()
    {
        if (!HasDivineGrace) return false;
        RemoveEffect(StatusEffectType.DivineGrace);
        Debug.Log($"[Status] {owner.myData?.playerName} 신의 가호로 공격 무효화!");
        return true;
    }

    public float AbsorbWithShield(float incomingDamage)
    {
        if (owner.myData.shieldHp <= 0f) return incomingDamage;
        float absorbed = Mathf.Min(owner.myData.shieldHp, incomingDamage);
        owner.myData.shieldHp -= absorbed;
        if (owner.myData.shieldHp <= 0f) { owner.myData.shieldHp = 0f; RemoveEffect(StatusEffectType.ShieldHp); }
        return incomingDamage - absorbed;
    }

    private void SetSpriteAlpha(float a)
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr == null) return;
        var col = sr.color; col.a = a; sr.color = col;
    }

    private static bool IsDebuff(StatusEffectType t) =>
        t == StatusEffectType.Slow || t == StatusEffectType.Stun || t == StatusEffectType.Root ||
        t == StatusEffectType.Poison || t == StatusEffectType.Bleed || t == StatusEffectType.Burn ||
        t == StatusEffectType.AtkReduction || t == StatusEffectType.Knockback;

    private static bool IsDoT(StatusEffectType t) =>
        t == StatusEffectType.Poison || t == StatusEffectType.Bleed || t == StatusEffectType.Burn;

    private static bool IsCrowdControl(StatusEffectType t) =>
        t == StatusEffectType.Stun || t == StatusEffectType.Root || t == StatusEffectType.Knockback;
}