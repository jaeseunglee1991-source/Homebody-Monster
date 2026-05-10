using System.Collections.Generic;

/// <summary>
/// 직업·등급·상성의 한국어 표시명 공통 유틸.
/// CharacterRerollSystem(로비)·PlayerWorldUI(인게임 머리 위 UI) 등에서 공유.
/// </summary>
public static class CharacterLabels
{
    private static readonly Dictionary<JobType, string> JobNames = new Dictionary<JobType, string>
    {
        { JobType.Warrior,   "전사"   },
        { JobType.Tanker,    "탱커"   },
        { JobType.Paladin,   "성기사" },
        { JobType.Berserker, "광전사" },
        { JobType.Mage,      "마법사" },
        { JobType.Archer,    "궁수"   },
        { JobType.Priest,    "성직자" },
        { JobType.Rogue,     "도적"   },
        { JobType.Assassin,  "암살자" },
        { JobType.Chef,      "셰프"   },
    };

    private static readonly Dictionary<GradeTier, string> GradeNames = new Dictionary<GradeTier, string>
    {
        { GradeTier.Normal,       "일반" },
        { GradeTier.Advanced,     "고급" },
        { GradeTier.Rare,         "희귀" },
        { GradeTier.Ancient,      "고대" },
        { GradeTier.Heroic,       "영웅" },
        { GradeTier.Legendary,    "전설" },
        { GradeTier.Mythic,       "신화" },
        { GradeTier.Celestial,    "천상" },
        { GradeTier.Transcendent, "초월" },
        { GradeTier.Absolute,     "절대" },
    };

    private static readonly Dictionary<AffinityType, string> AffinityNames = new Dictionary<AffinityType, string>
    {
        { AffinityType.Spicy,     "매운맛"   },
        { AffinityType.Greasy,    "느끼한맛" },
        { AffinityType.Fresh,     "신선한맛" },
        { AffinityType.Salty,     "짠맛"     },
        { AffinityType.Sweet,     "단맛"     },
        { AffinityType.MintChoco, "민트초코" },
        { AffinityType.Pineapple, "파인애플" },
    };

    public static string GetJobName(JobType job)
        => JobNames.TryGetValue(job, out var n) ? n : job.ToString();

    public static string GetGradeName(GradeTier grade)
        => GradeNames.TryGetValue(grade, out var n) ? n : grade.ToString();

    public static string GetAffinityName(AffinityType affinity)
        => AffinityNames.TryGetValue(affinity, out var n) ? n : affinity.ToString();

    /// <summary>"전사 · 매운맛 · 일반" 형식으로 직업·상성·등급을 한 줄에 합성.</summary>
    public static string FormatJobAffinityGrade(JobType job, AffinityType affinity, GradeTier grade)
        => $"{GetJobName(job)} · {GetAffinityName(affinity)} · {GetGradeName(grade)}";
}
