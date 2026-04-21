using UnityEngine;

/// <summary>
/// 전투 연산을 담당합니다 (서버 권장 로직).
/// 모든 스킬 효과가 여기서 계산됩니다.
/// </summary>
public class CombatSystem : MonoBehaviour
{
    /// <summary>
    /// 공격자가 방어자를 타격했을 때 최종 데미지를 계산합니다.
    /// </summary>
    public static DamageResult CalculateDamage(CharacterData attacker, CharacterData defender)
    {
        DamageResult result = new DamageResult();
        float damage = attacker.baseAtk;

        // ── 1. 회피 판정 (Ninja 스킬) ─────────────────────────
        if (defender.skills.Contains(SkillType.Ninja))
        {
            float evadeChance = 0.30f;
            if (Random.value < evadeChance)
            {
                result.isEvaded = true;
                result.finalDamage = 0f;
                return result;
            }
        }

        // ── 2. Shield 스킬: 방어자의 피해 감소 ───────────────
        float shieldReduction = 0f;
        if (defender.skills.Contains(SkillType.Shield))
        {
            shieldReduction = damage * 0.25f; // 25% 피해 감소
            result.isShielded = true;
        }

        // ── 3. 상성 판정 ─────────────────────────────────────
        bool advantageous = CheckAffinityAdvantage(attacker.affinity, defender.affinity);
        if (advantageous)
        {
            damage *= 1.5f;
            result.isCritical = true;
        }

        // ── 4. 특수 상성 (세계관 붕괴 기믹) ─────────────────
        if (IsSpecialAffinity(attacker.affinity) && IsSpecialAffinity(defender.affinity)
            && attacker.affinity != defender.affinity)
        {
            damage = 9999f;
            result.isWorldCollapse = true;
        }
        else if (IsSpecialAffinity(attacker.affinity) && !IsSpecialAffinity(defender.affinity))
        {
            damage *= 1.2f;
        }

        // ── 5. Berserker: 저체력 시 데미지 증가 ──────────────
        if (attacker.skills.Contains(SkillType.Berserker)
            && attacker.currentHp <= attacker.maxHp * 0.5f)
        {
            damage *= 1.5f;
        }

        // ── 6. GiantKiller: 공격 대상 HP가 높을수록 데미지 증가
        if (attacker.skills.Contains(SkillType.GiantKiller))
        {
            float hpDiff = defender.currentHp - attacker.currentHp;
            if (hpDiff > 0f)
            {
                float bonus = Mathf.Clamp(hpDiff / defender.maxHp, 0f, 0.5f); 
                damage *= 1f + bonus;
                result.isGiantKill = true;
            }
        }

        // ── 7. Guardian: 저체력 방어자 피해 감소 ─────────────
        if (defender.skills.Contains(SkillType.Guardian)
            && defender.currentHp <= defender.maxHp * 0.3f)
        {
            damage *= 0.6f; // 체력 30% 이하 시 40% 피해 감소
            result.isGuarded = true;
        }

        damage -= shieldReduction;
        damage = Mathf.Max(damage, 0f); 

        result.finalDamage = Mathf.Round(damage * 10f) / 10f;
        return result;
    }

    private static bool CheckAffinityAdvantage(AffinityType attacker, AffinityType defender)
    {
        if (attacker == AffinityType.Spicy   && defender == AffinityType.Greasy) return true;
        if (attacker == AffinityType.Greasy  && defender == AffinityType.Fresh)  return true;
        if (attacker == AffinityType.Fresh   && defender == AffinityType.Salty)  return true;
        if (attacker == AffinityType.Salty   && defender == AffinityType.Sweet)  return true;
        if (attacker == AffinityType.Sweet   && defender == AffinityType.Spicy)  return true;
        return false;
    }

    private static bool IsSpecialAffinity(AffinityType affinity)
    {
        return affinity == AffinityType.MintChoco || affinity == AffinityType.Pineapple;
    }
}

public struct DamageResult
{
    public float finalDamage;
    public bool isEvaded;        
    public bool isCritical;      
    public bool isWorldCollapse; 
    public bool isShielded;      
    public bool isGiantKill;     
    public bool isGuarded;       
}
