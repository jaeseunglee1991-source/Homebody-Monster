using UnityEngine;

public class CombatSystem : MonoBehaviour
{
    private static GameBalanceConfig Cfg => GameBalanceConfig.Get();

    /// <summary>기본 평타 데미지 계산.</summary>
    public static DamageResult CalculateDamage(CharacterData attacker, CharacterData defender,
        StatusEffectSystem attackerFX, StatusEffectSystem defenderFX)
        => CalculateDamageWithOverride(attacker, defender, attacker.baseAtk, attackerFX, defenderFX);

    /// <summary>
    /// 투사체 / 스킬처럼 고정 데미지값을 쓰는 경우.
    /// overrideDamage를 baseAtk 대신 출발점으로 사용합니다.
    /// </summary>
    public static DamageResult CalculateDamageWithOverride(CharacterData attacker, CharacterData defender,
        float overrideDamage, StatusEffectSystem attackerFX, StatusEffectSystem defenderFX)
    {
        var   result = new DamageResult();
        float dmg    = overrideDamage;
        var   cfg    = Cfg;

        // ── 은신 첫 타 보너스 ────────────────────────────────
        if (attacker != null && attacker.stealthFirstAttack)
        {
            float stealthMult = cfg != null ? cfg.StealthFirstHitMultiplier : 1.5f;
            dmg *= stealthMult;
            attacker.stealthFirstAttack = false;
            result.isCritical = true;
        }

        if (attackerFX != null) dmg *= attackerFX.GetAtkMultiplier();

        // ── 닌자: 피격 회피 ───────────────────────────────────
        float ninjaChance = cfg != null ? cfg.NinjaEvadeChance : 0.15f;
        if (defender.HasPassive(PassiveSkillType.Ninja) && Random.value < ninjaChance)
        { result.isEvaded = true; result.finalDamage = 0f; return result; }

        // ── 신의 가호: 완전 무효화 ───────────────────────────
        if (defenderFX != null && defenderFX.ConsumeDivineGrace())
        { result.isDivineGraceBlocked = true; result.finalDamage = 0f; return result; }

        // ── 면역 ──────────────────────────────────────────────
        if (defenderFX != null && defenderFX.IsImmune)
        { result.isEvaded = true; result.finalDamage = 0f; return result; }

        // ── 행운의 일격 ───────────────────────────────────────
        float luckyChance = cfg != null ? cfg.LuckyStrikeChance    : 0.1f;
        float luckyMult   = cfg != null ? cfg.LuckyStrikeMultiplier : 1.5f;
        if (attacker.HasPassive(PassiveSkillType.LuckyStrike) && Random.value < luckyChance)
        { dmg *= luckyMult; result.isLuckyStrike = true; }

        // ── 상성 판정 ─────────────────────────────────────────
        float affinityMult = cfg != null ? cfg.AffinityAdvantageMultiplier : 1.5f;
        if (CheckAffinityAdvantage(attacker.affinity, defender.affinity))
        { dmg *= affinityMult; result.isCritical = true; }

        // ── 특수 상성 (MintChoco vs Pineapple) ───────────────
        float specialMult = cfg != null ? cfg.SpecialAffinityMultiplier : 2.0f;
        if (IsSpecialAffinity(attacker.affinity) && IsSpecialAffinity(defender.affinity)
            && attacker.affinity != defender.affinity)
        { dmg *= specialMult; result.isWorldCollapse = true; }
        else if (IsSpecialAffinity(attacker.affinity) && !IsSpecialAffinity(defender.affinity))
        { dmg *= 1.2f; }

        // ── 거인 학살자 ───────────────────────────────────────
        float giantBonus = cfg != null ? cfg.GiantKillerBonusPerTen : 0.05f;
        if (attacker.HasPassive(PassiveSkillType.GiantKiller))
        {
            float diff = defender.maxHp - attacker.maxHp;
            if (diff >= 10f)
            { dmg *= (1f + Mathf.Floor(diff / 10f) * giantBonus); result.isGiantKill = true; }
        }

        // ── 처형인 ────────────────────────────────────────────
        float execThreshold = cfg != null ? cfg.ExecutionerHpThreshold : 0.25f;
        float execMult      = cfg != null ? cfg.ExecutionerMultiplier   : 1.3f;
        if (attacker.HasPassive(PassiveSkillType.Executioner)
            && defender.currentHp <= defender.maxHp * execThreshold)
        { dmg *= execMult; result.isExecutioner = true; }

        // ── 쉴드 HP 흡수 ─────────────────────────────────────
        if (defenderFX != null && defender.shieldHp > 0f)
        { dmg = defenderFX.AbsorbWithShield(dmg); result.isShielded = true; }

        // ── 방어 태세 ─────────────────────────────────────────
        float defStanceReduction = cfg != null ? cfg.DefenseStanceDamageReduction : 0.5f;
        if (defenderFX != null && defenderFX.IsInDefenseStance)
            dmg *= (1f - defStanceReduction);

        // ── 수호자 ────────────────────────────────────────────
        float guardThreshold = cfg != null ? cfg.GuardianHpThreshold     : 0.3f;
        float guardReduction = cfg != null ? cfg.GuardianDamageReduction  : 0.2f;
        if (defender.HasPassive(PassiveSkillType.Guardian)
            && defender.currentHp <= defender.maxHp * guardThreshold)
        { dmg *= (1f - guardReduction); result.isGuarded = true; }

        // ── 불굴의 분노: 수신 피해 +20% ──────────────────────
        if (defenderFX != null && defenderFX.IsInUndyingRage) dmg *= 1.2f;

        dmg = Mathf.Max(0f, Mathf.Round(dmg * 10f) / 10f);
        result.finalDamage = dmg;
        return result;
    }

    public static void PostDamageEffects(CharacterData attacker, CharacterData defender,
        StatusEffectSystem attackerFX, StatusEffectSystem defenderFX, float dealtDamage)
    {
        if (dealtDamage <= 0f) return;
        var cfg = Cfg;

        if (defender.deathMarkActive) defender.deathMarkAccumulated += dealtDamage;

        // ── 흡혈 ──────────────────────────────────────────────
        float lifestealRate = cfg != null ? cfg.LifestealRate : 0.2f;
        float lifestealMin  = cfg != null ? cfg.LifestealMin  : 0.5f;
        if (attacker.HasPassive(PassiveSkillType.Lifesteal))
            attacker.currentHp = Mathf.Min(
                attacker.currentHp + Mathf.Max(lifestealMin, dealtDamage * lifestealRate),
                attacker.maxHp);

        // ── 불굴의 분노 흡혈 50% ─────────────────────────────
        if (attackerFX != null && attackerFX.IsInUndyingRage)
            attacker.currentHp = Mathf.Min(attacker.currentHp + dealtDamage * 0.5f, attacker.maxHp);

        // ── 가시갑옥 반사 ─────────────────────────────────────
        float thornsRate = cfg != null ? cfg.ThornsReflectRate : 0.1f;
        float thornsMin  = cfg != null ? cfg.ThornsReflectMin  : 0.5f;
        if (defender.HasPassive(PassiveSkillType.Thorns))
        {
            float reflectDmg = Mathf.Max(thornsMin, dealtDamage * thornsRate);
            attacker.currentHp = Mathf.Max(0f, attacker.currentHp - reflectDmg);
        }

        defender.lastCombatTime = Time.time;
        attacker.lastCombatTime = Time.time;
    }

    /// <summary>불굴 패시브: 즉사 방지</summary>
    public static bool TryTenacity(CharacterData defender, StatusEffectSystem defenderFX)
    {
        if (defender.tenacityUsed) return false;
        if (!defender.HasPassive(PassiveSkillType.Tenacity)) return false;
        defender.tenacityUsed = true;
        defender.currentHp    = 1f;
        defenderFX.ApplyEffect(StatusEffectType.TenacityShield, 1.5f);
        return true;
    }

    /// <summary>수호 천사 궁극기: 즉사 방지</summary>
    public static bool TryGuardianAngel(CharacterData defender, StatusEffectSystem defenderFX)
    {
        if (!defenderFX.HasGuardianAngel) return false;
        defender.currentHp = defender.maxHp * 0.3f;
        defenderFX.RemoveEffect(StatusEffectType.GuardianAngel);
        return true;
    }

    /// <summary>재생 패시브: 비전투 시 HP 회복 (GameBalanceConfig 연동)</summary>
    public static System.Collections.IEnumerator RegenerationRoutine(
        CharacterData data, System.Func<bool> isDead)
    {
        while (true)
        {
            var   cfg      = Cfg;
            float interval = cfg != null ? cfg.RegenerationTickInterval : 2f;
            yield return new UnityEngine.WaitForSeconds(interval);

            if (isDead()) yield break;
            if (!data.HasPassive(PassiveSkillType.Regeneration)) continue;

            float cooldown  = cfg != null ? cfg.RegenerationCooldown : 4f;
            float regenRate = cfg != null ? cfg.RegenerationHpRate   : 0.05f;
            float regenMin  = cfg != null ? cfg.RegenerationMin      : 1.5f;

            if (Time.time - data.lastCombatTime >= cooldown && data.currentHp < data.maxHp)
            {
                float amount = Mathf.Max(regenMin, data.maxHp * regenRate);
                data.currentHp = Mathf.Min(data.currentHp + amount, data.maxHp);
            }
        }
    }

    private static bool CheckAffinityAdvantage(AffinityType atk, AffinityType def)
    {
        if (atk == AffinityType.Spicy  && def == AffinityType.Greasy) return true;
        if (atk == AffinityType.Greasy && def == AffinityType.Fresh)  return true;
        if (atk == AffinityType.Fresh  && def == AffinityType.Salty)  return true;
        if (atk == AffinityType.Salty  && def == AffinityType.Sweet)  return true;
        if (atk == AffinityType.Sweet  && def == AffinityType.Spicy)  return true;
        return false;
    }

    private static bool IsSpecialAffinity(AffinityType affinity) =>
        affinity == AffinityType.MintChoco || affinity == AffinityType.Pineapple;

    public static bool CheckAffinityAdvantagePublic(AffinityType atk, AffinityType def)
        => CheckAffinityAdvantage(atk, def);

    public static bool IsSpecialAffinityPublic(AffinityType affinity)
        => IsSpecialAffinity(affinity);
}
