using System.Collections.Generic;

// ════════════════════════════════════════════════════════════════
//  SkillSystem — 스킬 쿨다운 데이터(순수 함수)
//
//  [Pass C] NGO 서버 오케스트레이터(ActivateSkillServer/RunSkillServer/투사체 스폰 등)는
//  Fusion NetSkillSystem이 대체하여 전부 제거됨. 쿨다운 테이블 + GetCooldown만 남긴다
//  (NetPlayer.UseSkillRpc / NetPlayer.LocalSkillCooldownMax / NetHudBridge가 재사용).
// ════════════════════════════════════════════════════════════════
public static class SkillSystem
{
    private static readonly Dictionary<ActiveSkillType, float> Cooldowns =
        new Dictionary<ActiveSkillType, float>
    {
        { ActiveSkillType.Sweep,6f },{ ActiveSkillType.ChargeStrike,7f },{ ActiveSkillType.DefenseStance,10f },{ ActiveSkillType.EarthquakeStrike,18f },
        { ActiveSkillType.ShieldBash,6f },{ ActiveSkillType.Shockwave,8f },{ ActiveSkillType.IronSkin,12f },{ ActiveSkillType.Bulldozer,16f },
        { ActiveSkillType.HolyStrike,5f },{ ActiveSkillType.JudgmentHammer,7f },{ ActiveSkillType.DivineGrace,14f },{ ActiveSkillType.PillarOfJudgment,20f },
        { ActiveSkillType.RuthlessStrike,4f },{ ActiveSkillType.BleedSlash,6f },{ ActiveSkillType.UndyingRage,15f },{ ActiveSkillType.BladeStorm,20f },
        { ActiveSkillType.Fireball,5f },{ ActiveSkillType.IceShards,7f },{ ActiveSkillType.IceShield,16f },{ ActiveSkillType.Meteor,22f },
        { ActiveSkillType.PierceArrow,4f },{ ActiveSkillType.MultiShot,6f },{ ActiveSkillType.Trap,10f },{ ActiveSkillType.ArrowRain,18f },
        { ActiveSkillType.Smite,5f },{ ActiveSkillType.HolyExplosion,7f },{ ActiveSkillType.HealingLight,14f },{ ActiveSkillType.GuardianAngel,24f },
        { ActiveSkillType.PoisonDagger,5f },{ ActiveSkillType.Ambush,7f },{ ActiveSkillType.SmokeBomb,12f },{ ActiveSkillType.ShadowRaid,20f },
        { ActiveSkillType.VitalStrike,4f },{ ActiveSkillType.Shuriken,5f },{ ActiveSkillType.StealthSkill,14f },{ ActiveSkillType.DeathMark,22f },
        { ActiveSkillType.FryingPan,4f },{ ActiveSkillType.BurningOil,6f },{ ActiveSkillType.SnackTime,12f },{ ActiveSkillType.FeastTime,20f },
    };

    public static float GetCooldown(ActiveSkillType skill)
    {
        // GameBalanceConfig.SkillCooldownOverrides(Inspector) 우선.
        var cfg = GameBalanceConfig.Get();
        if (cfg != null)
        {
            float overridden = cfg.GetSkillCooldownOverride(skill);
            if (overridden > 0f) return overridden;
        }
        return Cooldowns.TryGetValue(skill, out float cd) ? cd : 10f;
    }
}
