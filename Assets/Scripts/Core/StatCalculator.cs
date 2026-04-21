using UnityEngine;

public static class StatCalculator
{
    // 등급별 배율 계산 (0단계 1.0배 ~ 9단계 2.0배)
    public static float GetGradeMultiplier(GradeTier grade)
    {
        return 1.0f + ((int)grade * 0.111f); 
    }

    public static CharacterData GenerateRandomCharacter(string name)
    {
        CharacterData data = new CharacterData();
        data.playerName = name;
        
        // 랜덤 요소 부여
        data.job = (JobType)Random.Range(0, System.Enum.GetValues(typeof(JobType)).Length);
        data.affinity = (AffinityType)Random.Range(0, System.Enum.GetValues(typeof(AffinityType)).Length);
        data.grade = (GradeTier)Random.Range(0, System.Enum.GetValues(typeof(GradeTier)).Length);

        // 기본 스탯 (로우스탯 기반)
        float rawHp = Random.Range(20f, 50f);
        float rawAtk = Random.Range(2.0f, 5.0f);
        float multiplier = GetGradeMultiplier(data.grade);

        // 직업별 보너스 스탯 (소수점 단위의 작은 차이)
        float jobHpBonus = 0f;
        float jobAtkBonus = 0f;
        float jobSpeed = 3.0f; // 기본 이동속도

        switch (data.job)
        {
            case JobType.Tanker: jobHpBonus = 15f; jobAtkBonus = -0.5f; jobSpeed = 2.5f; break;
            case JobType.Mage: jobHpBonus = -5f; jobAtkBonus = 3.0f; jobSpeed = 2.8f; break;
            case JobType.Assassin: jobHpBonus = -2f; jobAtkBonus = 1.5f; jobSpeed = 3.8f; break;
            // 추가 직업 밸런싱...
        }

        // 최종 스탯 결정 (소수점 첫째 자리까지만 반올림하여 깔끔하게 유지)
        data.maxHp = Mathf.Round((rawHp * multiplier + jobHpBonus) * 10f) / 10f;
        data.currentHp = data.maxHp;
        data.baseAtk = Mathf.Round((rawAtk * multiplier + jobAtkBonus) * 10f) / 10f;
        data.moveSpeed = jobSpeed;

        // 랜덤 스킬 1~4개 부여
        int skillCount = Random.Range(1, 5);
        for (int i = 0; i < skillCount; i++)
        {
            SkillType randomSkill = (SkillType)Random.Range(0, System.Enum.GetValues(typeof(SkillType)).Length);
            if (!data.skills.Contains(randomSkill))
            {
                data.skills.Add(randomSkill);
            }
        }

        return data;
    }
}
