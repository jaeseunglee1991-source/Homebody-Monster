using UnityEngine;

public class CombatSystem : MonoBehaviour
{
    // 공격자(Attacker)가 방어자(Defender)를 타격했을 때 호출 (서버 전용 로직으로 사용 권장)
    public static DamageResult CalculateDamage(CharacterData attacker, CharacterData defender)
    {
        DamageResult result = new DamageResult();
        float finalDamage = attacker.baseAtk;

        // 1. 스킬 판정: 방어자의 회피 (Ninja 스킬)
        if (defender.skills.Contains(SkillType.Ninja) && Random.value < 0.3f)
        {
            result.isEvaded = true;
            result.finalDamage = 0f;
            return result;
        }

        // 2. 상성 판정 (가위바위보 식 꼬리물기)
        bool isAdvantage = CheckAffinityAdvantage(attacker.affinity, defender.affinity);
        if (isAdvantage)
        {
            finalDamage *= 1.5f; // 상성 우위 시 50% 추가 피해
            result.isCritical = true;
        }

        // 3. 특수 상성 (세계관 붕괴 기믹)
        if (IsSpecialAffinity(attacker.affinity) && IsSpecialAffinity(defender.affinity) && attacker.affinity != defender.affinity)
        {
            finalDamage = 9999f; // 즉사급 데미지
            result.isWorldCollapse = true;
        }
        else if (IsSpecialAffinity(attacker.affinity) && !IsSpecialAffinity(defender.affinity))
        {
            finalDamage *= 1.2f; // 특수 상성이 일반 상성 타격 시 약간의 보너스
        }

        // 4. 스킬 판정: 공격자의 체력 비례 데미지 증가 (Berserker)
        if (attacker.skills.Contains(SkillType.Berserker) && attacker.currentHp <= attacker.maxHp * 0.5f)
        {
            finalDamage *= 1.5f;
        }

        result.finalDamage = Mathf.Round(finalDamage * 10f) / 10f; // 0.5, 1.2 등 세밀한 데미지 적용
        return result;
    }

    private static bool CheckAffinityAdvantage(AffinityType attacker, AffinityType defender)
    {
        if (attacker == AffinityType.Spicy && defender == AffinityType.Greasy) return true;
        if (attacker == AffinityType.Greasy && defender == AffinityType.Fresh) return true;
        if (attacker == AffinityType.Fresh && defender == AffinityType.Salty) return true;
        if (attacker == AffinityType.Salty && defender == AffinityType.Sweet) return true;
        if (attacker == AffinityType.Sweet && defender == AffinityType.Spicy) return true;
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
}
