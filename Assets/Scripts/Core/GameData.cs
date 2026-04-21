using System.Collections.Generic;
using UnityEngine;

public enum JobType { Warrior, Tanker, Paladin, Berserker, Mage, Archer, Priest, Rogue, Assassin, Chef }
public enum AffinityType { Spicy, Greasy, Fresh, Salty, Sweet, MintChoco, Pineapple }
public enum GradeTier { Normal, Advanced, Rare, Ancient, Heroic, Legendary, Mythic, Celestial, Transcendent, Absolute }
public enum SkillType { Lifesteal, Ninja, GiantKiller, Shield, Healer, Sniper, Berserker, Guardian }

[System.Serializable]
public class CharacterData
{
    public string playerName;
    public JobType job;
    public AffinityType affinity;
    public GradeTier grade;
    public List<SkillType> skills = new List<SkillType>();

    // 로우스탯 기반 밸런스 (소수점 단위 체력/공격력)
    public float maxHp;
    public float currentHp;
    public float baseAtk;
    public float moveSpeed;
}
