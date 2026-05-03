using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public enum JobType { Warrior, Tanker, Paladin, Berserker, Mage, Archer, Priest, Rogue, Assassin, Chef }
public enum AffinityType { Spicy, Greasy, Fresh, Salty, Sweet, MintChoco, Pineapple }
public enum GradeTier { Normal, Advanced, Rare, Ancient, Heroic, Legendary, Mythic, Celestial, Transcendent, Absolute }

// 패시브 스킬 10종
public enum PassiveSkillType { Lifesteal, Ninja, GiantKiller, Guardian, Swiftness, Executioner, Regeneration, Thorns, LuckyStrike, Tenacity }

// 액티브 스킬 40종 (직업별 공격2 + 생존1 + 궁극1)
public enum ActiveSkillType
{
    // Warrior
    Sweep, ChargeStrike, DefenseStance, EarthquakeStrike,
    // Tanker
    ShieldBash, Shockwave, IronSkin, Bulldozer,
    // Paladin
    HolyStrike, JudgmentHammer, DivineGrace, PillarOfJudgment,
    // Berserker
    RuthlessStrike, BleedSlash, UndyingRage, BladeStorm,
    // Mage
    Fireball, IceShards, IceShield, Meteor,
    // Archer
    PierceArrow, MultiShot, Trap, ArrowRain,
    // Priest
    Smite, HolyExplosion, HealingLight, GuardianAngel,
    // Rogue
    PoisonDagger, Ambush, SmokeBomb, ShadowRaid,
    // Assassin
    VitalStrike, Shuriken, StealthSkill, DeathMark,
    // Chef
    FryingPan, BurningOil, SnackTime, FeastTime
}

// 상태이상
public enum StatusEffectType
{
    Slow, Stun, Root, Poison, Bleed, Burn, AtkReduction, Knockback,
    DefenseStance, UndyingRage, IceShield, Stealth,
    TenacityShield, DivineGrace, GuardianAngel,
    DeathMarkTarget, BladeStormActive, ShieldHp
}

// 직업별 스킬 풀 (로그라이트 랜덤 배정용)
public static class JobSkillPool
{
    private static readonly Dictionary<JobType, ActiveSkillType[]> ActivePool
        = new Dictionary<JobType, ActiveSkillType[]>
    {
        { JobType.Warrior,   new[] { ActiveSkillType.Sweep, ActiveSkillType.ChargeStrike, ActiveSkillType.DefenseStance, ActiveSkillType.EarthquakeStrike } },
        { JobType.Tanker,    new[] { ActiveSkillType.ShieldBash, ActiveSkillType.Shockwave, ActiveSkillType.IronSkin, ActiveSkillType.Bulldozer } },
        { JobType.Paladin,   new[] { ActiveSkillType.HolyStrike, ActiveSkillType.JudgmentHammer, ActiveSkillType.DivineGrace, ActiveSkillType.PillarOfJudgment } },
        { JobType.Berserker, new[] { ActiveSkillType.RuthlessStrike, ActiveSkillType.BleedSlash, ActiveSkillType.UndyingRage, ActiveSkillType.BladeStorm } },
        { JobType.Mage,      new[] { ActiveSkillType.Fireball, ActiveSkillType.IceShards, ActiveSkillType.IceShield, ActiveSkillType.Meteor } },
        { JobType.Archer,    new[] { ActiveSkillType.PierceArrow, ActiveSkillType.MultiShot, ActiveSkillType.Trap, ActiveSkillType.ArrowRain } },
        { JobType.Priest,    new[] { ActiveSkillType.Smite, ActiveSkillType.HolyExplosion, ActiveSkillType.HealingLight, ActiveSkillType.GuardianAngel } },
        { JobType.Rogue,     new[] { ActiveSkillType.PoisonDagger, ActiveSkillType.Ambush, ActiveSkillType.SmokeBomb, ActiveSkillType.ShadowRaid } },
        { JobType.Assassin,  new[] { ActiveSkillType.VitalStrike, ActiveSkillType.Shuriken, ActiveSkillType.StealthSkill, ActiveSkillType.DeathMark } },
        { JobType.Chef,      new[] { ActiveSkillType.FryingPan, ActiveSkillType.BurningOil, ActiveSkillType.SnackTime, ActiveSkillType.FeastTime } },
    };

    public static ActiveSkillType[] GetActivePool(JobType job) => ActivePool[job];

    public static List<ActiveSkillType> RollActiveSkills(JobType job)
    {
        var pool = ActivePool[job];
        int count = Random.Range(1, 5); // 1~4개 랜덤 (기획 의도)
        var shuffled = new List<ActiveSkillType>(pool);
        shuffled.Shuffle();
        return shuffled.GetRange(0, Mathf.Min(count, shuffled.Count));
    }

    public static List<PassiveSkillType> RollPassiveSkills()
    {
        var all = System.Enum.GetValues(typeof(PassiveSkillType));
        int count = Random.Range(0, 3); // 0~2개 랜덤 (기획 의도)
        var pool = new List<PassiveSkillType>();
        if (count == 0) return pool; // 0개일 경우 즉시 반환
        var indices = new List<int>();
        for (int i = 0; i < all.Length; i++) indices.Add(i);
        indices.Shuffle();
        for (int i = 0; i < Mathf.Min(count, indices.Count); i++)
            pool.Add((PassiveSkillType)all.GetValue(indices[i]));
        return pool;
    }
}

[System.Serializable]
public class CharacterData
{
    public string playerName;
    public JobType job;
    public AffinityType affinity;
    public GradeTier grade;

    public List<PassiveSkillType> passiveSkills = new List<PassiveSkillType>();
    public List<ActiveSkillType>  activeSkills  = new List<ActiveSkillType>();

    public float maxHp;
    public float currentHp;
    public float baseAtk;
    public float moveSpeed;

    [System.NonSerialized] public bool  tenacityUsed          = false;
    [System.NonSerialized] public float lastCombatTime        = -999f;
    [System.NonSerialized] public float shieldHp              = 0f;
    [System.NonSerialized] public float deathMarkAccumulated  = 0f;
    [System.NonSerialized] public bool  deathMarkActive       = false;
    [System.NonSerialized] public ulong deathMarkCasterId     = ulong.MaxValue;
    [System.NonSerialized] public bool  stealthFirstAttack    = false;

    public bool HasPassive(PassiveSkillType p) => passiveSkills.Contains(p);
    public bool HasActive(ActiveSkillType a)   => activeSkills.Contains(a);
}

public struct DamageResult : INetworkSerializable
{
    public float finalDamage;
    public bool  isEvaded, isCritical, isWorldCollapse, isShielded;
    public bool  isGiantKill, isGuarded, isLuckyStrike, isExecutioner, isDivineGraceBlocked;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref finalDamage);
        s.SerializeValue(ref isEvaded);         s.SerializeValue(ref isCritical);
        s.SerializeValue(ref isDivineGraceBlocked); s.SerializeValue(ref isWorldCollapse);
        s.SerializeValue(ref isLuckyStrike);    s.SerializeValue(ref isGiantKill);
        s.SerializeValue(ref isExecutioner);    s.SerializeValue(ref isShielded);
        s.SerializeValue(ref isGuarded);
    }
}

public static class ListExtensions
{
    public static void Shuffle<T>(this List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}