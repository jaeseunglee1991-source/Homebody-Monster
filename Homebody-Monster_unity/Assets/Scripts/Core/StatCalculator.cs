using UnityEngine;

public static class StatCalculator
{
    public static float GetGradeMultiplier(GradeTier grade) => 1.0f + (int)grade * 0.111f;

    // 고정값(+) 대신 배율(×)로 직업별 스탯 보정 — 등급이 높아져도 비율이 일정하게 유지됨
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
        var data = new CharacterData();
        data.playerName = name;
        data.job      = forceJob ?? (JobType)Random.Range(0, 10);
        data.affinity = (AffinityType)Random.Range(0, System.Enum.GetValues(typeof(AffinityType)).Length);
        data.grade    = (GradeTier)Random.Range(0, System.Enum.GetValues(typeof(GradeTier)).Length);

        float rawHp  = Random.Range(20f, 50f);
        float rawAtk = Random.Range(2.0f, 5.0f);
        float mult   = GetGradeMultiplier(data.grade);
        var stat = JobBaseStats[data.job];

        // 곱산 적용: rawStat × 등급배율 × 직업배율
        data.maxHp     = Round1(rawHp  * mult * stat.HpMult);
        data.currentHp = data.maxHp;
        data.baseAtk   = Round1(rawAtk * mult * stat.AtkMult);
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