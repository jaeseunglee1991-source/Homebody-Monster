using UnityEngine;

public class CombatSystem : MonoBehaviour
{
    public static DamageResult CalculateDamage(CharacterData attacker, CharacterData defender,
        StatusEffectSystem attackerFX, StatusEffectSystem defenderFX)
    {
        var result = new DamageResult();
        float dmg = attacker.baseAtk;

        if (attackerFX != null) dmg *= attackerFX.GetAtkMultiplier();

        // 닌자: 피격 회피 15%
        if (defender.HasPassive(PassiveSkillType.Ninja) && Random.value < 0.15f)
        { result.isEvaded = true; result.finalDamage = 0f; return result; }

        // 신의 가호: 완전 무효화
        if (defenderFX != null && defenderFX.ConsumeDivineGrace())
        { result.isDivineGraceBlocked = true; result.finalDamage = 0f; return result; }

        // 면역
        if (defenderFX != null && defenderFX.IsImmune)
        { result.isEvaded = true; result.finalDamage = 0f; return result; }

        // 행운의 일격: 10% 확률 1.5배
        if (attacker.HasPassive(PassiveSkillType.LuckyStrike) && Random.value < 0.1f)
        { dmg *= 1.5f; result.isLuckyStrike = true; }

        // 상성 판정
        if (CheckAffinityAdvantage(attacker.affinity, defender.affinity))
        { dmg *= 1.5f; result.isCritical = true; }

        // 특수 상성 (MintChoco vs Pineapple) - 즉사(9999) 제거 → 3배 데미지로 완화
        if (IsSpecialAffinity(attacker.affinity) && IsSpecialAffinity(defender.affinity) && attacker.affinity != defender.affinity)
        { dmg *= 3.0f; result.isWorldCollapse = true; }
        else if (IsSpecialAffinity(attacker.affinity) && !IsSpecialAffinity(defender.affinity))
        { dmg *= 1.2f; }

        // 거인 학살자: 최대 HP 차이 10당 5% 피해 증가 (현재HP로 버그있던 기준을 maxHP로 수정)
        if (attacker.HasPassive(PassiveSkillType.GiantKiller))
        {
            float diff = defender.maxHp - attacker.maxHp;
            if (diff >= 10f) { dmg *= (1f + Mathf.Floor(diff / 10f) * 0.05f); result.isGiantKill = true; }
        }

        // 처형인: 적 HP <= 25% 시 피해 30% 증가 (고정값 +1.5에서 배율로 수정)
        if (attacker.HasPassive(PassiveSkillType.Executioner) && defender.currentHp <= defender.maxHp * 0.25f)
        { dmg *= 1.3f; result.isExecutioner = true; }

        // 쉴드 HP 흡수
        if (defenderFX != null && defender.shieldHp > 0f)
        { dmg = defenderFX.AbsorbWithShield(dmg); result.isShielded = true; }

        // 방어 태세: 정면 50% 감소
        if (defenderFX != null && defenderFX.IsInDefenseStance) dmg *= 0.5f;

        // 수호자: HP <= 30% 시 피해 20% 감소
        if (defender.HasPassive(PassiveSkillType.Guardian) && defender.currentHp <= defender.maxHp * 0.3f)
        { dmg *= 0.8f; result.isGuarded = true; }

        // 불굴의 분노: 수신 피해 +20%
        if (defenderFX != null && defenderFX.IsInUndyingRage) dmg *= 1.2f;

        dmg = Mathf.Max(0f, Mathf.Round(dmg * 10f) / 10f);
        result.finalDamage = dmg;
        return result;
    }

    public static void PostDamageEffects(CharacterData attacker, CharacterData defender,
        StatusEffectSystem attackerFX, StatusEffectSystem defenderFX, float dealtDamage)
    {
        if (dealtDamage <= 0f) return;
        if (defender.deathMarkActive) defender.deathMarkAccumulated += dealtDamage;

        // 흡혈: 피해의 20%, 최소 0.5
        if (attacker.HasPassive(PassiveSkillType.Lifesteal))
            attacker.currentHp = Mathf.Min(attacker.currentHp + Mathf.Max(0.5f, dealtDamage * 0.2f), attacker.maxHp);

        // 불굴의 분노 흡혈 50%
        if (attackerFX != null && attackerFX.IsInUndyingRage)
            attacker.currentHp = Mathf.Min(attacker.currentHp + dealtDamage * 0.5f, attacker.maxHp);

        // 가시갑옥: 받은 피해의 10% 반사 (최소 0.5) — 고정 0.5에서 비율로 수정
        if (defender.HasPassive(PassiveSkillType.Thorns))
        {
            float reflectDmg = Mathf.Max(0.5f, dealtDamage * 0.1f);
            attacker.currentHp = Mathf.Max(0f, attacker.currentHp - reflectDmg);
        }

        defender.lastCombatTime = Time.time;
        attacker.lastCombatTime = Time.time;
    }

    // 불굴 패시브: 즉사 방지
    public static bool TryTenacity(CharacterData defender, StatusEffectSystem defenderFX)
    {
        if (defender.tenacityUsed) return false;
        if (!defender.HasPassive(PassiveSkillType.Tenacity)) return false;
        defender.tenacityUsed = true;
        defender.currentHp = 1f;
        defenderFX.ApplyEffect(StatusEffectType.TenacityShield, 1.5f);
        Debug.Log($"[Passive] {defender.playerName} 불굴 발동! 1.5초 무적");
        return true;
    }

    // 수호 천사 궁극기: 즉사 방지
    public static bool TryGuardianAngel(CharacterData defender, StatusEffectSystem defenderFX)
    {
        if (!defenderFX.HasGuardianAngel) return false;
        defender.currentHp = defender.maxHp * 0.3f;
        defenderFX.RemoveEffect(StatusEffectType.GuardianAngel);
        Debug.Log($"[Skill] {defender.playerName} 수호 천사 발동! HP {defender.currentHp:0.#}로 생존");
        return true;
    }

    // 재생 패시브: 비전투 시 2초마다 HP 1.5 회복
    public static System.Collections.IEnumerator RegenerationRoutine(CharacterData data, System.Func<bool> isDead)
    {
        while (true)
        {
            yield return new UnityEngine.WaitForSeconds(2f);
            if (isDead()) yield break;
            if (!data.HasPassive(PassiveSkillType.Regeneration)) continue;
            // 재생패시브: 최대HP의 5% 회복 (최소 1.5) — 고정값에서 비례로 수정
            if (Time.time - data.lastCombatTime >= 4f && data.currentHp < data.maxHp)
            {
                float regenAmount = Mathf.Max(1.5f, data.maxHp * 0.05f);
                data.currentHp = Mathf.Min(data.currentHp + regenAmount, data.maxHp);
                Debug.Log($"[Passive] {data.playerName} 재생: +{regenAmount:0.#} HP");
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
}