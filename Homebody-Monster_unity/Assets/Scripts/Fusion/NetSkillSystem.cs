using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [Phase 4-C] Fusion 스킬 디스패처 — NGO SkillSystem(40종 switch)의 대체.
/// 검증된 빌딩블록(근접AoE/돌진/투사체/지점AoE/실드/방어/회복/넉백)으로 40종 전부를
/// 파라미터 테이블로 실행한다. StateAuthority(호스트)에서만 호출(NetPlayer.UseSkillRpc).
///
/// 수치는 NGO SkillSystem 원본의 근사치 — 정밀 밸런싱은 cutover 마무리 단계에서 원본 대조.
/// [4-G] 본래 메커니즘 반영: 은신/낙인(4-E·4-F), DivineGrace(1회 완전무효), IceShield(면역+침묵),
/// UndyingRage(흡혈+피해 증감), GuardianAngel(치명타 1회 소생), RuthlessStrike(자해), PierceArrow(관통),
/// SnackTime(정화), Trap(설치형 NetTrap — 프리팹 미배선 시 즉발 지점AoE 폴백).
/// → 40종 전부 본래 메커니즘 동작(잔여는 수치 밸런싱 대조뿐).
/// </summary>
public static class NetSkillSystem
{
    /// <summary>투사체 적중 시 적용할 상태이상 (NetProjectile과 공유).</summary>
    public enum Fx { None, Slow, Stun, Poison }

    private enum Kind
    {
        MeleeAoE,     // p1=반경, p2=데미지배율
        DashStrike,   // p1=거리, p2=반경, p3=데미지배율
        Projectile,   // p1=데미지배율, p2=발수, p3=퍼짐각(도)
        TargetAoE,    // p1=사거리, p2=반경, p3=데미지배율 (조준 방향 지점)
        AoEKnockback, // p1=반경, p2=데미지배율, p3=넉백속도
        SelfShield,   // p1=실드량, p2=지속
        SelfDefense,  // p1=지속
        SelfHeal,     // p1=최대HP 비율
        SelfStealth,  // p1=지속 (4-E)
        DeathMark,    // p1=지속, p2=사거리, p3=초기데미지배율 (4-F)
        // [4-G] 본래 메커니즘 자기 버프
        SelfDivineGrace, // p1=지속 — 1회 완전 무효
        SelfImmune,      // p1=지속 — 면역+이동/공격 잠금(얼음 방패)
        SelfRage,        // p1=지속 — 불굴의 분노(흡혈+피해 증감)
        SelfGuardian,    // p1=지속 — 수호천사(치명타 1회 소생)
        TrapPlace,       // p1=사거리, p2=반경, p3=데미지배율, effDur/effVal=슬로우 (설치형 덫)
    }

    private struct Def
    {
        public Kind  kind;
        public float p1, p2, p3;
        public Fx    effect;   // 피격자 상태이상 (Poison의 effVal=baseAtk 배율, Slow=감속비, Stun=무시)
        public float effDur, effVal;
        public bool  pierce;   // [4-G] Projectile 관통
        public float selfCost; // [4-G] MeleeAoE 자해(시전자 MaxHp 비율)
        public bool  cleanse;  // [4-G] SelfHeal 디버프 정화
    }

    private static Def Melee (float r, float dmg, Fx fx = Fx.None, float fd = 0f, float fv = 0f, float selfCost = 0f) => new Def { kind = Kind.MeleeAoE,     p1 = r, p2 = dmg,        effect = fx, effDur = fd, effVal = fv, selfCost = selfCost };
    private static Def Dash  (float d, float r, float dmg)                                       => new Def { kind = Kind.DashStrike,   p1 = d, p2 = r, p3 = dmg };
    private static Def Proj  (float dmg, int n = 1, float spread = 0f, Fx fx = Fx.None, float fd = 0f, float fv = 0f, bool pierce = false) => new Def { kind = Kind.Projectile, p1 = dmg, p2 = n, p3 = spread, effect = fx, effDur = fd, effVal = fv, pierce = pierce };
    private static Def Target(float range, float r, float dmg, Fx fx = Fx.None, float fd = 0f, float fv = 0f)         => new Def { kind = Kind.TargetAoE,  p1 = range, p2 = r, p3 = dmg, effect = fx, effDur = fd, effVal = fv };
    private static Def Knock (float r, float dmg, float kb)                                      => new Def { kind = Kind.AoEKnockback, p1 = r, p2 = dmg, p3 = kb };
    private static Def Shield(float amount, float dur)                                           => new Def { kind = Kind.SelfShield,   p1 = amount, p2 = dur };
    private static Def Def_  (float dur)                                                         => new Def { kind = Kind.SelfDefense,  p1 = dur };
    private static Def Heal  (float ratio, bool cleanse = false)                                 => new Def { kind = Kind.SelfHeal,     p1 = ratio, cleanse = cleanse };
    private static Def Stealth(float dur)                                                        => new Def { kind = Kind.SelfStealth,  p1 = dur };
    private static Def Mark   (float dur, float range, float initDmg)                            => new Def { kind = Kind.DeathMark,    p1 = dur, p2 = range, p3 = initDmg };
    private static Def Grace   (float dur)                                                       => new Def { kind = Kind.SelfDivineGrace, p1 = dur };
    private static Def Immune  (float dur)                                                       => new Def { kind = Kind.SelfImmune,      p1 = dur };
    private static Def Rage    (float dur)                                                       => new Def { kind = Kind.SelfRage,        p1 = dur };
    private static Def Guardian(float dur)                                                       => new Def { kind = Kind.SelfGuardian,    p1 = dur };
    private static Def Place   (float range, float r, float dmg, float slowDur, float slowVal)   => new Def { kind = Kind.TrapPlace, p1 = range, p2 = r, p3 = dmg, effect = Fx.Slow, effDur = slowDur, effVal = slowVal };

    private static readonly Dictionary<ActiveSkillType, Def> Table = new()
    {
        // ── 전사 ──
        { ActiveSkillType.Sweep,            Melee(2.2f, 1.2f) },
        { ActiveSkillType.ChargeStrike,     Dash(4f, 1.3f, 2.0f) },
        { ActiveSkillType.DefenseStance,    Def_(5f) },
        { ActiveSkillType.EarthquakeStrike, Melee(3f, 1.5f, Fx.Slow, 2f, 0.4f) },
        // ── 탱커 ──
        { ActiveSkillType.ShieldBash,       Melee(1.8f, 1.0f, Fx.Stun, 1f) },
        { ActiveSkillType.Shockwave,        Knock(2.5f, 1.5f, 9f) },
        { ActiveSkillType.IronSkin,         Shield(40f, 6f) },
        { ActiveSkillType.Bulldozer,        Dash(5f, 1.5f, 1.5f) },
        // ── 성기사 ──
        { ActiveSkillType.HolyStrike,       Melee(1.8f, 1.6f) },
        { ActiveSkillType.JudgmentHammer,   Melee(2.2f, 1.8f, Fx.Stun, 0.8f) },
        { ActiveSkillType.DivineGrace,      Grace(5f) },         // [4-G] 1회 완전 무효
        { ActiveSkillType.PillarOfJudgment, Target(6f, 1.8f, 2.2f) },
        // ── 광전사 ──
        { ActiveSkillType.RuthlessStrike,   Melee(1.6f, 2.4f, selfCost: 0.12f) }, // [4-G] 자해 12%
        { ActiveSkillType.BleedSlash,       Melee(1.8f, 1.2f, Fx.Poison, 4f, 0.4f) }, // 출혈≈독
        { ActiveSkillType.UndyingRage,      Rage(6f) },          // [4-G] 흡혈50%+피해 증감
        { ActiveSkillType.BladeStorm,       Melee(2.8f, 2.0f) },
        // ── 마법사 ──
        { ActiveSkillType.Fireball,         Proj(1.2f, 1, 0f, Fx.Poison, 4f, 0.4f) }, // 화상≈독
        { ActiveSkillType.IceShards,        Proj(1.0f, 1, 0f, Fx.Stun, 1.2f) },       // 빙결≈스턴
        { ActiveSkillType.IceShield,        Immune(2.5f) },      // [4-G] 면역+침묵
        { ActiveSkillType.Meteor,           Target(7f, 2.2f, 2.5f, Fx.Poison, 3f, 0.3f) },
        // ── 궁수 ──
        { ActiveSkillType.PierceArrow,      Proj(1.6f, pierce: true) }, // [4-G] 관통
        { ActiveSkillType.MultiShot,        Proj(0.9f, 3, 25f) },
        { ActiveSkillType.Trap,             Place(5f, 1.5f, 1.0f, 3f, 0.5f) }, // [4-G] 설치형 덫(TrapPrefab 미배선 시 즉발AoE 폴백)
        { ActiveSkillType.ArrowRain,        Target(7f, 2.5f, 1.8f) },
        // ── 성직자 ──
        { ActiveSkillType.Smite,            Proj(1.4f) },
        { ActiveSkillType.HolyExplosion,    Melee(2.5f, 1.6f) },
        { ActiveSkillType.HealingLight,     Heal(0.3f) },
        { ActiveSkillType.GuardianAngel,    Guardian(8f) },      // [4-G] 치명타 1회 소생(30% HP)
        // ── 도적 ──
        { ActiveSkillType.PoisonDagger,     Proj(1.0f, 1, 0f, Fx.Poison, 4f, 0.5f) },
        { ActiveSkillType.Ambush,           Dash(4.5f, 1.3f, 2.2f) },
        { ActiveSkillType.SmokeBomb,        Melee(2.5f, 0.5f, Fx.Slow, 3f, 0.5f) },
        { ActiveSkillType.ShadowRaid,       Dash(6f, 1.5f, 2.0f) },
        // ── 암살자 ──
        { ActiveSkillType.VitalStrike,      Melee(1.5f, 2.6f) },
        { ActiveSkillType.Shuriken,         Proj(1.1f, 2, 15f) },
        { ActiveSkillType.StealthSkill,     Stealth(4f) },       // [4-E] 은신
        { ActiveSkillType.DeathMark,        Mark(6f, 7f, 1.0f) }, // [4-F] 낙인: 6초·사거리7·초기 baseAtk
        // ── 셰프 ──
        { ActiveSkillType.FryingPan,        Melee(1.6f, 1.4f, Fx.Stun, 1.2f) },
        { ActiveSkillType.BurningOil,       Target(5f, 2.0f, 1.2f, Fx.Poison, 4f, 0.5f) },
        { ActiveSkillType.SnackTime,        Heal(0.35f, cleanse: true) }, // [4-G] 회복 + 디버프 정화
        { ActiveSkillType.FeastTime,        Heal(0.5f) },
    };

    /// <summary>StateAuthority 전용 — 스킬 실행.</summary>
    public static void Execute(NetPlayer caster, ActiveSkillType skill, Vector2 aim)
    {
        if (caster == null || caster.Data == null) return;
        if (!Table.TryGetValue(skill, out var d))
        {
            Debug.LogWarning($"[NetSkillSystem] 미정의 스킬: {skill}");
            return;
        }

        Vector2 a   = aim.sqrMagnitude > 0.0001f ? aim.normalized : Vector2.right;
        float   atk = caster.Data.baseAtk;

        switch (d.kind)
        {
            case Kind.MeleeAoE:
                DamageArea(caster, caster.transform.position, d.p1, atk * d.p2, d);
                if (d.selfCost > 0f) caster.SelfDamage(caster.MaxHp * d.selfCost); // [4-G] 자해(무자비)
                break;

            case Kind.DashStrike:
                caster.DashMove(a, d.p1);
                DamageArea(caster, caster.transform.position, d.p2, atk * d.p3, d);
                break;

            case Kind.TargetAoE:
                Vector3 center = NetArena.Clamp(caster.transform.position + (Vector3)(a * d.p1));
                DamageArea(caster, center, d.p2, atk * d.p3, d);
                break;

            case Kind.AoEKnockback:
                foreach (var t in Targets(caster, caster.transform.position, d.p1))
                {
                    caster.DealSkillDamage(t, atk * d.p2);
                    // 면역(얼음 방패) 대상은 넉백(CC) 차단 — 원본 IsCrowdControl 면역과 동일.
                    if (t.Status == null || !t.Status.IsImmune)
                        t.ApplyKnockback((Vector2)t.transform.position - (Vector2)caster.transform.position, d.p3, 0.25f);
                }
                break;

            case Kind.Projectile:
            {
                int   count  = Mathf.Max(1, (int)d.p2);
                float spread = d.p3;
                for (int i = 0; i < count; i++)
                {
                    float offset = count > 1 ? Mathf.Lerp(-spread, spread, count == 1 ? 0.5f : (float)i / (count - 1)) : 0f;
                    Vector2 dir  = Quaternion.Euler(0f, 0f, offset) * a;
                    float fxVal  = d.effect == Fx.Poison ? Mathf.Max(1f, atk * d.effVal) : d.effVal;
                    caster.FireProjectile(dir, atk * d.p1, d.effect, d.effDur, fxVal, d.pierce);
                }
                break;
            }

            case Kind.SelfShield:  caster.Status?.ApplyShield(d.p2, d.p1);        break;
            case Kind.SelfDefense: caster.Status?.ApplyDefenseStance(d.p1);       break;
            case Kind.SelfHeal:
                caster.HealSelf(caster.MaxHp * d.p1);
                if (d.cleanse) caster.Status?.CleanseDebuffs();                  // [4-G] 간식타임 정화
                break;
            case Kind.SelfStealth: caster.Status?.ApplyStealth(d.p1);            break;

            // [4-G] 본래 메커니즘 자기 버프
            case Kind.SelfDivineGrace: caster.Status?.ApplyDivineGrace(d.p1);   break;
            case Kind.SelfImmune:      caster.Status?.ApplyIceShield(d.p1);      break;
            case Kind.SelfRage:        caster.Status?.ApplyUndyingRage(d.p1);    break;
            case Kind.SelfGuardian:    caster.Status?.ApplyGuardianAngel(d.p1);  break;

            case Kind.TrapPlace:
            {
                // 조준 방향 d.p1만큼 앞 지점에 설치. TrapPrefab 미배선이면 즉발 지점AoE로 폴백.
                Vector3 pos = NetArena.Clamp(caster.transform.position + (Vector3)(a * d.p1));
                if (!caster.SpawnTrap(pos, atk * d.p3, d.p2, d.effDur, d.effVal))
                    DamageArea(caster, pos, d.p2, atk * d.p3, d);
                break;
            }

            case Kind.DeathMark:
            {
                NetPlayer best = null; float bestD = float.MaxValue;
                foreach (var t in Targets(caster, caster.transform.position, d.p2))
                {
                    float dist = Vector2.Distance(caster.transform.position, t.transform.position);
                    if (dist < bestD) { bestD = dist; best = t; }
                }
                if (best != null)
                {
                    caster.DealSkillDamage(best, atk * d.p3); // 초기 피해(누적 시작)
                    best.Status?.ApplyDeathMark(d.p1, caster);
                }
                break;
            }
        }
    }

    // ── 내부 유틸 ────────────────────────────────────────────────
    private static void DamageArea(NetPlayer caster, Vector3 center, float radius, float dmg, Def d)
    {
        foreach (var t in Targets(caster, center, radius))
        {
            if (!caster.DealSkillDamage(t, dmg)) continue;
            ApplyFx(caster, t, d);
        }
    }

    private static void ApplyFx(NetPlayer caster, NetPlayer target, Def d)
    {
        if (d.effect == Fx.None || target.Status == null || target.IsDead) return;
        switch (d.effect)
        {
            case Fx.Slow:   target.Status.ApplySlow(d.effDur, d.effVal); break;
            case Fx.Stun:   target.Status.ApplyStun(d.effDur);           break;
            case Fx.Poison: target.Status.ApplyPoison(d.effDur, Mathf.Max(1f, caster.Data.baseAtk * d.effVal), caster); break;
        }
    }

    private static IEnumerable<NetPlayer> Targets(NetPlayer caster, Vector3 center, float radius)
    {
        foreach (var h in Physics2D.OverlapCircleAll(center, radius))
        {
            var t = h.GetComponent<NetPlayer>();
            if (t != null && t != caster && !t.IsDead) yield return t;
        }
    }
}
