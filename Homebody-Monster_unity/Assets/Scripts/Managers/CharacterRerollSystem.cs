using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 캐릭터 리롤 — 상수 + 스킬/직업/등급/속성 한국어 이름 데이터.
///
/// [Pass C] NGO 리롤 UI(NetworkManager.Singleton/PlayerNetworkSync.ResubmitCharacterData 의존)는 제거됨.
/// Fusion 리롤은 NetHUD 준비창 + NetPlayer.RequestRerollLocal(SupabaseManager.SpendPizzaForReroll)이 담당하고,
/// 이 클래스는 상수(RerollCostPizza/RerollWindowSecs)와 GetSkillDisplayName(Fusion HUD가 사용)만 제공한다.
/// MonoBehaviour 유지(씬 컴포넌트 참조 보존) — 인스턴스 로직 없음.
/// </summary>
public class CharacterRerollSystem : MonoBehaviour
{
    public static CharacterRerollSystem Instance { get; private set; }

    public const int   RerollCostPizza    = 20;
    public const int   MaxRerollsPerMatch = 1;
    public const float RerollWindowSecs   = 15f;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    // ── 스킬 한국어 이름 ─────────────────────────────────────────
    private static readonly Dictionary<ActiveSkillType, string> SkillKoreanNames
        = new Dictionary<ActiveSkillType, string>
    {
        { ActiveSkillType.Sweep,            "휩쓸기"        },
        { ActiveSkillType.ChargeStrike,     "돌진 강타"     },
        { ActiveSkillType.DefenseStance,    "방어 태세"     },
        { ActiveSkillType.EarthquakeStrike, "지진 강타"     },
        { ActiveSkillType.ShieldBash,       "방패 강타"     },
        { ActiveSkillType.Shockwave,        "충격파"        },
        { ActiveSkillType.IronSkin,         "강철 피부"     },
        { ActiveSkillType.Bulldozer,        "불도저"        },
        { ActiveSkillType.HolyStrike,       "성스러운 강타" },
        { ActiveSkillType.JudgmentHammer,   "심판의 망치"   },
        { ActiveSkillType.DivineGrace,      "신성한 은총"   },
        { ActiveSkillType.PillarOfJudgment, "심판의 기둥"   },
        { ActiveSkillType.RuthlessStrike,   "무자비한 타격" },
        { ActiveSkillType.BleedSlash,       "출혈 베기"     },
        { ActiveSkillType.UndyingRage,      "불사의 분노"   },
        { ActiveSkillType.BladeStorm,       "칼날 폭풍"     },
        { ActiveSkillType.Fireball,         "화염구"        },
        { ActiveSkillType.IceShards,        "얼음 파편"     },
        { ActiveSkillType.IceShield,        "얼음 방패"     },
        { ActiveSkillType.Meteor,           "메테오"        },
        { ActiveSkillType.PierceArrow,      "관통 화살"     },
        { ActiveSkillType.MultiShot,        "다중 사격"     },
        { ActiveSkillType.Trap,             "함정"          },
        { ActiveSkillType.ArrowRain,        "화살 비"       },
        { ActiveSkillType.Smite,            "응징"          },
        { ActiveSkillType.HolyExplosion,    "성스러운 폭발" },
        { ActiveSkillType.HealingLight,     "치유의 빛"     },
        { ActiveSkillType.GuardianAngel,    "수호 천사"     },
        { ActiveSkillType.PoisonDagger,     "독 단검"       },
        { ActiveSkillType.Ambush,           "기습"          },
        { ActiveSkillType.SmokeBomb,        "연막탄"        },
        { ActiveSkillType.ShadowRaid,       "그림자 습격"   },
        { ActiveSkillType.VitalStrike,      "급소 찌르기"   },
        { ActiveSkillType.Shuriken,         "표창"          },
        { ActiveSkillType.StealthSkill,     "은신"          },
        { ActiveSkillType.DeathMark,        "죽음의 낙인"   },
        { ActiveSkillType.FryingPan,        "프라이팬"      },
        { ActiveSkillType.BurningOil,       "불타는 기름"   },
        { ActiveSkillType.SnackTime,        "간식 타임"     },
        { ActiveSkillType.FeastTime,        "만찬 시간"     },
    };

    /// <summary>스킬 한국어 이름(Fusion HUD가 사용). 매핑 없으면 enum 이름.</summary>
    public static string GetSkillDisplayName(ActiveSkillType skill)
        => SkillKoreanNames.TryGetValue(skill, out var name) ? name : skill.ToString();

    // ── 직업/등급/속성 한국어 이름 (라벨용) ─────────────────────
    private static readonly Dictionary<JobType, string> JobKoreanNames = new Dictionary<JobType, string>
    {
        { JobType.Warrior, "전사" }, { JobType.Tanker, "탱커" }, { JobType.Paladin, "성기사" },
        { JobType.Berserker, "광전사" }, { JobType.Mage, "마법사" }, { JobType.Archer, "궁수" },
        { JobType.Priest, "성직자" }, { JobType.Rogue, "도적" }, { JobType.Assassin, "암살자" },
        { JobType.Chef, "셰프" },
    };

    private static readonly Dictionary<GradeTier, string> GradeKoreanNames = new Dictionary<GradeTier, string>
    {
        { GradeTier.Normal, "일반" }, { GradeTier.Advanced, "고급" }, { GradeTier.Rare, "희귀" },
        { GradeTier.Ancient, "고대" }, { GradeTier.Heroic, "영웅" }, { GradeTier.Legendary, "전설" },
        { GradeTier.Mythic, "신화" }, { GradeTier.Celestial, "천상" }, { GradeTier.Transcendent, "초월" },
        { GradeTier.Absolute, "절대" },
    };

    private static readonly Dictionary<AffinityType, string> AffinityKoreanNames = new Dictionary<AffinityType, string>
    {
        { AffinityType.Spicy, "매운맛" }, { AffinityType.Greasy, "느끼한맛" }, { AffinityType.Fresh, "신선한맛" },
        { AffinityType.Salty, "짠맛" }, { AffinityType.Sweet, "단맛" }, { AffinityType.MintChoco, "민트초코" },
        { AffinityType.Pineapple, "파인애플" },
    };

    public static string GetJobDisplayName(JobType job)
        => JobKoreanNames.TryGetValue(job, out var n) ? n : job.ToString();
    public static string GetGradeDisplayName(GradeTier grade)
        => GradeKoreanNames.TryGetValue(grade, out var n) ? n : grade.ToString();
    public static string GetAffinityDisplayName(AffinityType affinity)
        => AffinityKoreanNames.TryGetValue(affinity, out var n) ? n : affinity.ToString();
}
