using UnityEngine;

public static class StatCalculator
{
    public static float GetGradeMultiplier(GradeTier grade) => 1.0f + (int)grade * 0.111f;

    private struct JobStat { public float HpBonus, AtkBonus, Speed; }

    private static readonly System.Collections.Generic.Dictionary<JobType, JobStat> JobBaseStats
        = new System.Collections.Generic.Dictionary<JobType, JobStat>
    {
        { JobType.Warrior,   new JobStat { HpBonus =  5f, AtkBonus =  0.5f, Speed = 3.0f } },
        { JobType.Tanker,    new JobStat { HpBonus = 15f, AtkBonus = -0.5f, Speed = 2.5f } },
        { JobType.Paladin,   new JobStat { HpBonus =  3f, AtkBonus =  0.5f, Speed = 2.8f } },
        { JobType.Berserker, new JobStat { HpBonus = -3f, AtkBonus =  1.5f, Speed = 3.2f } },
        { JobType.Mage,      new JobStat { HpBonus = -5f, AtkBonus =  3.0f, Speed = 2.8f } },
        { JobType.Archer,    new JobStat { HpBonus = -2f, AtkBonus =  1.0f, Speed = 3.3f } },
        { JobType.Priest,    new JobStat { HpBonus =  0f, AtkBonus =  0.0f, Speed = 2.9f } },
        { JobType.Rogue,     new JobStat { HpBonus = -3f, AtkBonus =  1.2f, Speed = 3.5f } },
        { JobType.Assassin,  new JobStat { HpBonus = -2f, AtkBonus =  1.5f, Speed = 3.8f } },
        { JobType.Chef,      new JobStat { HpBonus =  2f, AtkBonus =  0.3f, Speed = 3.0f } },
    };

    public static CharacterData GenerateCharacter(string name, JobType? forceJob = null)
    {
        var data = new CharacterData();
        data.playerName = name;
        data.job      = forceJob ?? (JobType)Random.Range(0, 10);
        data.affinity = (AffinityType)Random.Range(0, System.Enum.GetValues(typeof(AffinityType)).Length);
        data.grade    = (GradeTier)Random.Range(0, System.Enum.GetValues(typeof(GradeTier)).Length);

        float rawHp  = Random.Range(20f, 50f);
        float rawAtk = Random.Range(2.0f, 5.0f);
        float mult   = GetGradeMultiplier(data.grade);
        var stat = JobBaseStats[data.job];

        data.maxHp     = Round1(rawHp  * mult + stat.HpBonus);
        data.currentHp = data.maxHp;
        data.baseAtk   = Round1(rawAtk * mult + stat.AtkBonus);
        data.moveSpeed = stat.Speed;

        RollSkills(data);
        Debug.Log($"[StatCalc] {data.playerName} | {data.job}/{data.affinity}/{data.grade} HP:{data.maxHp:0.#} ATK:{data.baseAtk:0.#}");
        return data;
    }

    // 게임 시작 시: 패시브 0~4개 + 직업 액티브 1~4개 랜덤 배정
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