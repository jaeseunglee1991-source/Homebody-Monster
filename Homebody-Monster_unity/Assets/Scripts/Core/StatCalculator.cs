using UnityEngine;

public static class StatCalculator
{
    private static GameBalanceConfig Cfg => GameBalanceConfig.Get();

    public static float GetGradeMultiplier(GradeTier grade)
    {
        float step = Cfg != null ? Cfg.GradeMultiplierStep : 0.111f;
        return 1.0f + (int)grade * step;
    }

    private struct JobStat { public float HpMult, AtkMult, Speed; }

    private static readonly System.Collections.Generic.Dictionary<JobType, JobStat> JobBaseStats
        = new System.Collections.Generic.Dictionary<JobType, JobStat>
    {
        { JobType.Warrior,   new JobStat { HpMult = 1.1f, AtkMult = 1.1f, Speed = 3.0f } },
        { JobType.Tanker,    new JobStat { HpMult = 1.4f, AtkMult = 0.8f, Speed = 2.5f } },
        { JobType.Paladin,   new JobStat { HpMult = 1.1f, AtkMult = 1.0f, Speed = 2.8f } },
        { JobType.Berserker, new JobStat { HpMult = 0.9f, AtkMult = 1.3f, Speed = 3.2f } },
        { JobType.Mage,      new JobStat { HpMult = 0.8f, AtkMult = 1.5f, Speed = 2.8f } },
        { JobType.Archer,    new JobStat { HpMult = 0.9f, AtkMult = 1.2f, Speed = 3.3f } },
        { JobType.Priest,    new JobStat { HpMult = 1.0f, AtkMult = 1.0f, Speed = 2.9f } },
        { JobType.Rogue,     new JobStat { HpMult = 0.9f, AtkMult = 1.3f, Speed = 3.5f } },
        { JobType.Assassin,  new JobStat { HpMult = 0.8f, AtkMult = 1.4f, Speed = 3.8f } },
        { JobType.Chef,      new JobStat { HpMult = 1.0f, AtkMult = 1.0f, Speed = 3.0f } },
    };

    public static CharacterData GenerateCharacter(string name, JobType? forceJob = null)
    {
        var cfg  = Cfg;
        var data = new CharacterData();

        data.playerName = name;
        data.job      = forceJob ?? (JobType)Random.Range(0, 10);
        data.affinity = (AffinityType)Random.Range(0, System.Enum.GetValues(typeof(AffinityType)).Length);
        data.grade    = (GradeTier)Random.Range(0, System.Enum.GetValues(typeof(GradeTier)).Length);

        float hpMin  = cfg != null ? cfg.BaseHpMin  : 20f;
        float hpMax  = cfg != null ? cfg.BaseHpMax  : 50f;
        float atkMin = cfg != null ? cfg.BaseAtkMin : 2f;
        float atkMax = cfg != null ? cfg.BaseAtkMax : 5f;

        float rawHp  = Random.Range(hpMin,  hpMax);
        float rawAtk = Random.Range(atkMin, atkMax);
        float mult   = GetGradeMultiplier(data.grade);
        var   stat   = JobBaseStats[data.job];

        data.maxHp     = Round1(rawHp  * mult * stat.HpMult);
        data.currentHp = data.maxHp;
        data.baseAtk   = Round1(rawAtk * mult * stat.AtkMult);
        data.moveSpeed = stat.Speed;

        RollSkills(data);
        return data;
    }

    public static void RollSkills(CharacterData data)
    {
        data.passiveSkills.Clear();
        data.activeSkills.Clear();
        data.passiveSkills = JobSkillPool.RollPassiveSkills();
        data.activeSkills  = JobSkillPool.RollActiveSkills(data.job);
    }

    public static CharacterData GenerateRandomCharacter(string name) => GenerateCharacter(name);

    public static float GetEffectiveMoveSpeed(CharacterData data, StatusEffectSystem statusFX)
    {
        float speed = data.moveSpeed;
        if (data.HasPassive(PassiveSkillType.Swiftness)) speed *= 1.1f;
        if (statusFX != null) speed *= statusFX.GetMoveSpeedMultiplier();
        return Mathf.Max(0f, speed);
    }

    public static float ModifySlowDuration(CharacterData target, float duration)
    {
        if (target.HasPassive(PassiveSkillType.Swiftness)) duration *= 0.7f;
        return duration;
    }

    private static float Round1(float v) => Mathf.Round(v * 10f) / 10f;
}
