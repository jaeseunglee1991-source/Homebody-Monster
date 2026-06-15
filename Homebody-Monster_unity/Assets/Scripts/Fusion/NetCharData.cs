using Fusion;

/// <summary>
/// [Phase 3-A] 캐릭터 데이터 전송 구조체 (Fusion INetworkStruct).
/// NGO의 NetworkCharacterData(PlayerNetworkSync.cs)와 동일 역할 — 이름 충돌 회피를 위해 NetCharData.
/// 클라이언트(InputAuthority)가 자기 캐릭터를 SubmitCharacterRpc로 제출할 때 사용한다.
/// </summary>
public struct NetCharData : INetworkStruct
{
    public int   Job, Affinity, Grade;
    public float MaxHp, BaseAtk, MoveSpeed, AttackCooldown;
    public int   Active0, Active1, Active2, Active3;
    public int   Passive0, Passive1, Passive2, Passive3;

    public static NetCharData From(CharacterData d)
    {
        return new NetCharData
        {
            Job = (int)d.job, Affinity = (int)d.affinity, Grade = (int)d.grade,
            MaxHp = d.maxHp, BaseAtk = d.baseAtk, MoveSpeed = d.moveSpeed,
            AttackCooldown = d.attackCooldown,
            Active0  = GetActive(d, 0),  Active1  = GetActive(d, 1),
            Active2  = GetActive(d, 2),  Active3  = GetActive(d, 3),
            Passive0 = GetPassive(d, 0), Passive1 = GetPassive(d, 1),
            Passive2 = GetPassive(d, 2), Passive3 = GetPassive(d, 3),
        };
    }

    public CharacterData ToCharacterData()
    {
        var d = new CharacterData
        {
            job = (JobType)Job, affinity = (AffinityType)Affinity, grade = (GradeTier)Grade,
            maxHp = MaxHp, currentHp = MaxHp, baseAtk = BaseAtk, moveSpeed = MoveSpeed,
            attackCooldown = AttackCooldown,
            activeSkills  = new System.Collections.Generic.List<ActiveSkillType>(),
            passiveSkills = new System.Collections.Generic.List<PassiveSkillType>(),
        };
        int[] actives  = { Active0, Active1, Active2, Active3 };
        int[] passives = { Passive0, Passive1, Passive2, Passive3 };
        foreach (int a in actives)  if (a >= 0) d.activeSkills.Add((ActiveSkillType)a);
        foreach (int p in passives) if (p >= 0) d.passiveSkills.Add((PassiveSkillType)p);
        return d;
    }

    /// <summary>
    /// 서버측 범위 검증 — PlayerNetworkSync.IsValidCharacterData 포팅.
    /// 개조 클라이언트의 maxHp=9999 등 스탯 위변조를 StateAuthority에서 차단한다.
    /// </summary>
    public static bool IsValid(NetCharData d)
    {
        if (d.Job   < 0 || d.Job   > 9) return false;
        if (d.Grade < 0 || d.Grade > 9) return false;

        int affinityMax = System.Enum.GetValues(typeof(AffinityType)).Length - 1;
        if (d.Affinity < 0 || d.Affinity > affinityMax) return false;

        int activeMax  = System.Enum.GetValues(typeof(ActiveSkillType)).Length  - 1;
        int passiveMax = System.Enum.GetValues(typeof(PassiveSkillType)).Length - 1;
        int[] actives  = { d.Active0,  d.Active1,  d.Active2,  d.Active3  };
        int[] passives = { d.Passive0, d.Passive1, d.Passive2, d.Passive3 };
        foreach (int a in actives)  if (a != -1 && (a < 0 || a > activeMax))  return false;
        foreach (int p in passives) if (p != -1 && (p < 0 || p > passiveMax)) return false;

        if (d.MaxHp     < 5f   || d.MaxHp     > 160f) return false;
        if (d.BaseAtk   < 0.5f || d.BaseAtk   > 20f)  return false;
        if (d.MoveSpeed < 1f   || d.MoveSpeed > 6f)   return false;
        if (d.AttackCooldown < 0.5f || d.AttackCooldown > 1.5f) return false;
        return true;
    }

    private static int GetActive(CharacterData d, int i)
        => d.activeSkills != null && i < d.activeSkills.Count ? (int)d.activeSkills[i] : -1;
    private static int GetPassive(CharacterData d, int i)
        => d.passiveSkills != null && i < d.passiveSkills.Count ? (int)d.passiveSkills[i] : -1;
}
