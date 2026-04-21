using UnityEngine;
using System.Collections;

public static class SkillSystem
{
    private static readonly float[] skillCooldowns =
    {
        12f,  // Lifesteal  (패시브지만 버튼 표시용)
        10f,  // Ninja      (패시브)
        15f,  // GiantKiller(패시브)
        8f,   // Shield     (패시브)
        20f,  // Healer     (액티브)
        12f,  // Sniper     (액티브)
        10f,  // Berserker  (패시브)
        14f,  // Guardian   (패시브)
    };

    public static void ActivateSkill(SkillType skill, PlayerController caster)
    {
        switch (skill)
        {
            case SkillType.Healer:
                caster.StartCoroutine(HealerRoutine(caster));
                break;

            case SkillType.Sniper:
                caster.StartCoroutine(SniperRoutine(caster));
                break;

            default:
                Debug.Log($"[SkillSystem] {skill}은 패시브 스킬입니다 (자동 적용).");
                break;
        }
    }

    private static IEnumerator HealerRoutine(PlayerController caster)
    {
        if (caster.myData == null) yield break;

        float healAmount = caster.myData.maxHp * 0.20f;
        float tickHeal   = healAmount / 5f; 

        Debug.Log($"[SkillSystem] Healer 발동: {healAmount:0.#}HP 회복 시작");

        for (int i = 0; i < 5; i++)
        {
            yield return new WaitForSeconds(0.5f);
            if (caster == null || caster.IsDead) yield break;
            caster.Heal(tickHeal);
        }
    }

    private static IEnumerator SniperRoutine(PlayerController caster)
    {
        float originalRange = caster.attackRange;
        caster.attackRange *= 1.8f;

        Debug.Log($"[SkillSystem] Sniper 발동: 사거리 {originalRange:0.#} → {caster.attackRange:0.#}");

        yield return new WaitForSeconds(6f);

        if (caster != null)
        {
            caster.attackRange = originalRange;
            Debug.Log("[SkillSystem] Sniper 종료: 사거리 복구");
        }
    }

    public static float GetCooldown(SkillType skill)
    {
        int idx = (int)skill;
        if (idx < 0 || idx >= skillCooldowns.Length) return 10f;
        return skillCooldowns[idx];
    }
}
