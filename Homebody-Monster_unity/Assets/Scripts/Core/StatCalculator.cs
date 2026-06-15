using UnityEngine;

public static class StatCalculator
{
    private static GameBalanceConfig Cfg => GameBalanceConfig.Get();

    public static float GetGradeMultiplier(GradeTier grade)
    {
        float step = Cfg != null ? Cfg.GradeMultiplierStep : 0.111f;
        return 1.0f + (int)grade * step;
    }

    private struct JobStat { public float HpMult, AtkMult, Speed, AttackCooldown; }

    private static readonly System.Collections.Generic.Dictionary<JobType, JobStat> JobBaseStats
        = new System.Collections.Generic.Dictionary<JobType, JobStat>
    {
        { JobType.Warrior,   new JobStat { HpMult = 1.1f, AtkMult = 1.1f, Speed = 3.0f, AttackCooldown = 0.85f } },
        { JobType.Tanker,    new JobStat { HpMult = 1.4f, AtkMult = 0.8f, Speed = 2.5f, AttackCooldown = 0.65f } },
        { JobType.Paladin,   new JobStat { HpMult = 1.1f, AtkMult = 1.0f, Speed = 2.8f, AttackCooldown = 0.80f } },
        { JobType.Berserker, new JobStat { HpMult = 0.9f, AtkMult = 1.3f, Speed = 3.2f, AttackCooldown = 1.00f } },
        { JobType.Mage,      new JobStat { HpMult = 0.8f, AtkMult = 1.5f, Speed = 2.8f, AttackCooldown = 1.20f } },
        { JobType.Archer,    new JobStat { HpMult = 0.9f, AtkMult = 1.2f, Speed = 3.3f, AttackCooldown = 0.90f } },
        { JobType.Priest,    new JobStat { HpMult = 1.0f, AtkMult = 1.0f, Speed = 2.9f, AttackCooldown = 0.80f } },
        { JobType.Rogue,     new JobStat { HpMult = 0.9f, AtkMult = 1.3f, Speed = 3.5f, AttackCooldown = 1.00f } },
        { JobType.Assassin,  new JobStat { HpMult = 0.8f, AtkMult = 1.4f, Speed = 3.8f, AttackCooldown = 1.10f } },
        { JobType.Chef,      new JobStat { HpMult = 1.0f, AtkMult = 1.0f, Speed = 3.0f, AttackCooldown = 0.80f } },
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
        data.baseAtk        = Round1(rawAtk * mult * stat.AtkMult);
        data.moveSpeed      = stat.Speed;
        data.attackCooldown = stat.AttackCooldown;

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

    // [Pass C] GetEffectiveMoveSpeed(CharacterData, StatusEffectSystem)는 NGO PlayerController 전용이라
    // 제거. Fusion은 NetPlayer가 _data.moveSpeed × NetStatus.MoveSpeedMultiplier로 직접 계산한다.

    public static float ModifySlowDuration(CharacterData target, float duration)
    {
        if (target.HasPassive(PassiveSkillType.Swiftness)) duration *= 0.7f;
        return duration;
    }

    private static float Round1(float v) => Mathf.Round(v * 10f) / 10f;
}
