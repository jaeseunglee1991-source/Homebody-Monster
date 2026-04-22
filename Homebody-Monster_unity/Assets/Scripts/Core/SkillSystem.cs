using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// SkillSystem: 액티브 스킬 40종 전체 구현
// [VISUAL_HOOK] 주석 위치에 파티클/이펙트 프리팹 Instantiate 연결
public static class SkillSystem
{
    private static readonly Dictionary<ActiveSkillType, float> Cooldowns = new Dictionary<ActiveSkillType, float>
    {
        { ActiveSkillType.Sweep,6f },{ ActiveSkillType.ChargeStrike,7f },{ ActiveSkillType.DefenseStance,10f },{ ActiveSkillType.EarthquakeStrike,18f },
        { ActiveSkillType.ShieldBash,6f },{ ActiveSkillType.Shockwave,8f },{ ActiveSkillType.IronSkin,12f },{ ActiveSkillType.Bulldozer,16f },
        { ActiveSkillType.HolyStrike,5f },{ ActiveSkillType.JudgmentHammer,7f },{ ActiveSkillType.DivineGrace,14f },{ ActiveSkillType.PillarOfJudgment,20f },
        { ActiveSkillType.RuthlessStrike,4f },{ ActiveSkillType.BleedSlash,6f },{ ActiveSkillType.UndyingRage,15f },{ ActiveSkillType.BladeStorm,20f },
        { ActiveSkillType.Fireball,5f },{ ActiveSkillType.IceShards,7f },{ ActiveSkillType.IceShield,16f },{ ActiveSkillType.Meteor,22f },
        { ActiveSkillType.PierceArrow,4f },{ ActiveSkillType.MultiShot,6f },{ ActiveSkillType.Trap,10f },{ ActiveSkillType.ArrowRain,18f },
        { ActiveSkillType.Smite,5f },{ ActiveSkillType.HolyExplosion,7f },{ ActiveSkillType.HealingLight,14f },{ ActiveSkillType.GuardianAngel,24f },
        { ActiveSkillType.PoisonDagger,5f },{ ActiveSkillType.Ambush,7f },{ ActiveSkillType.SmokeBomb,12f },{ ActiveSkillType.ShadowRaid,20f },
        { ActiveSkillType.VitalStrike,4f },{ ActiveSkillType.Shuriken,5f },{ ActiveSkillType.StealthSkill,14f },{ ActiveSkillType.DeathMark,22f },
        { ActiveSkillType.FryingPan,4f },{ ActiveSkillType.BurningOil,6f },{ ActiveSkillType.SnackTime,12f },{ ActiveSkillType.FeastTime,20f },
    };

    public static float GetCooldown(ActiveSkillType skill) => Cooldowns.TryGetValue(skill, out float cd) ? cd : 10f;

    public static void ActivateSkill(ActiveSkillType skill, PlayerController caster, Vector2 targetPos = default)
    {
        if (caster == null || caster.IsDead || caster.myData == null) return;
        if (caster.StatusFX.IsSilenced) return;
        caster.StartCoroutine(RunSkill(skill, caster, targetPos));
    }

    private static IEnumerator RunSkill(ActiveSkillType skill, PlayerController caster, Vector2 targetPos)
    {
        switch (skill)
        {
            // ── 전사 ──
            case ActiveSkillType.Sweep:
                foreach (var t in GetEnemiesInCone(caster, 2.2f, 120f)) DealSkillDamage(caster, t, caster.myData.baseAtk * 1.2f);
                yield break;
            case ActiveSkillType.ChargeStrike:
            {
                Vector2 dir = caster.GetFacingDirection(), orig = caster.Rb.position, dest = orig + dir * 2.5f;
                float el = 0f;
                while (el < 0.15f) { el += Time.deltaTime; caster.Rb.MovePosition(Vector2.Lerp(orig, dest, el / 0.15f)); yield return null; }
                foreach (var t in GetEnemiesInRadius(caster, 1.5f)) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.5f); t.StatusFX.ApplyEffect(StatusEffectType.Stun, 0.5f, 0f, caster); }
                yield break;
            }
            case ActiveSkillType.DefenseStance: caster.StatusFX.ApplyEffect(StatusEffectType.DefenseStance, 3f); yield break;
            case ActiveSkillType.EarthquakeStrike:
                yield return new WaitForSeconds(0.1f);
                foreach (var t in GetEnemiesInRadius(caster, 3.5f)) { DealSkillDamage(caster, t, caster.myData.baseAtk * 2.0f); t.StatusFX.ApplyEffect(StatusEffectType.Slow, 2f, 0.5f, caster); }
                yield break;
            // ── 탱커 ──
            case ActiveSkillType.ShieldBash:
            { var t = GetClosestEnemy(caster, 1.8f); if (t != null) { DealSkillDamage(caster, t, caster.myData.baseAtk * 0.8f); t.StatusFX.ApplyEffect(StatusEffectType.Stun, 1f, 0f, caster); } yield break; }
            case ActiveSkillType.Shockwave:
            { var hits = Physics2D.RaycastAll(caster.transform.position, caster.GetFacingDirection(), 5f, caster.enemyLayer); foreach (var h in hits) { var t = h.collider.GetComponent<PlayerController>(); if (t != null && !t.IsDead && t != caster) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.0f); t.StatusFX.ApplyEffect(StatusEffectType.AtkReduction, 2f, 0.2f, caster); } } yield break; }
            case ActiveSkillType.IronSkin: caster.StatusFX.ApplyEffect(StatusEffectType.ShieldHp, 4f, 15f); yield break;
            case ActiveSkillType.Bulldozer:
            { Vector2 dir = caster.GetFacingDirection(), orig = caster.Rb.position, dest = orig + dir * 6f; float el = 0f; var hit = new HashSet<PlayerController>();
              while (el < 0.4f) { el += Time.deltaTime; caster.Rb.MovePosition(Vector2.Lerp(orig, dest, el / 0.4f)); foreach (var c2 in Physics2D.OverlapCircleAll(caster.Rb.position, 1.2f, caster.enemyLayer)) { var t = c2.GetComponent<PlayerController>(); if (t != null && !t.IsDead && t != caster && !hit.Contains(t)) { hit.Add(t); DealSkillDamage(caster, t, caster.myData.baseAtk * 1.0f); caster.StartCoroutine(KnockbackRoutine(t, dir, 3f)); } } yield return null; } yield break; }
            // ── 팔라딘 ──
            case ActiveSkillType.HolyStrike:
            { var t = GetClosestEnemy(caster, 2f); if (t != null) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.2f); caster.Heal(2f); } yield break; }
            case ActiveSkillType.JudgmentHammer:
            { yield return new WaitForSeconds(0.3f); foreach (var t in GetEnemiesInRadius(caster, 1.2f, targetPos)) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.0f); t.StatusFX.ApplyEffect(StatusEffectType.Slow, 1.5f, 0.3f, caster); } yield break; }
            case ActiveSkillType.DivineGrace: caster.StatusFX.ApplyEffect(StatusEffectType.DivineGrace, 2f); yield break;
            case ActiveSkillType.PillarOfJudgment:
            { yield return new WaitForSeconds(0.6f); foreach (var t in GetEnemiesInRadius(caster, 1.5f, targetPos)) DealSkillDamage(caster, t, caster.myData.baseAtk * 3.0f); yield break; }
            // ── 버서커 ──
            case ActiveSkillType.RuthlessStrike:
            { if (caster.myData.currentHp <= 1.5f) yield break; caster.myData.currentHp -= 1f; if (caster.IsLocalPlayer && InGameHUD.Instance != null) InGameHUD.Instance.UpdateHealthBar(caster.myData.currentHp, caster.myData.maxHp); var t = GetClosestEnemy(caster, 2f); if (t != null) DealSkillDamage(caster, t, caster.myData.baseAtk * 1.8f); yield break; }
            case ActiveSkillType.BleedSlash:
            { var t = GetClosestEnemy(caster, 2f); if (t != null) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.0f); t.StatusFX.ApplyEffect(StatusEffectType.Bleed, 3f, 1.5f, caster); } yield break; }
            case ActiveSkillType.UndyingRage: caster.StatusFX.ApplyEffect(StatusEffectType.UndyingRage, 4f); yield break;
            case ActiveSkillType.BladeStorm:
            { caster.StatusFX.ApplyEffect(StatusEffectType.BladeStormActive, 3f); float el = 0f, next = 0.5f;
              while (el < 3f && !caster.IsDead) { el += Time.deltaTime; if (el >= next) { next += 0.5f; foreach (var t in GetEnemiesInRadius(caster, 2.0f)) DealSkillDamage(caster, t, caster.myData.baseAtk * 0.5f); } yield return null; } yield break; }
            // ── 마법사 ──
            case ActiveSkillType.Fireball:
            { yield return new WaitForSeconds(0.25f); bool direct = false; foreach (var t in GetEnemiesInRadius(caster, 0.8f, targetPos)) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.5f); direct = true; } if (!direct) foreach (var t in GetEnemiesInRadius(caster, 2.2f, targetPos)) DealSkillDamage(caster, t, caster.myData.baseAtk * 0.5f); yield break; }
            case ActiveSkillType.IceShards:
            { foreach (float ao in new[]{0f,-25f,25f}) { Vector2 d = Rotate(caster.GetFacingDirection(), ao); foreach (var h in Physics2D.RaycastAll(caster.transform.position, d, 6f, caster.enemyLayer)) { var t = h.collider.GetComponent<PlayerController>(); if (t != null && !t.IsDead && t != caster) { DealSkillDamage(caster, t, caster.myData.baseAtk * 0.8f); t.StatusFX.ApplyEffect(StatusEffectType.Root, 1.5f, 0f, caster); } } } yield break; }
            case ActiveSkillType.IceShield: caster.StatusFX.ApplyEffect(StatusEffectType.IceShield, 2.5f); yield break;
            case ActiveSkillType.Meteor:
            { yield return new WaitForSeconds(1f); foreach (var t in GetEnemiesInRadius(caster, 3.5f, targetPos)) DealSkillDamage(caster, t, caster.myData.baseAtk * 4.0f); yield break; }
            // ── 궁수 ──
            case ActiveSkillType.PierceArrow:
            { yield return new WaitForSeconds(0.8f); foreach (var h in Physics2D.RaycastAll(caster.transform.position, caster.GetFacingDirection(), 10f, caster.enemyLayer)) { var t = h.collider.GetComponent<PlayerController>(); if (t != null && !t.IsDead && t != caster) DealSkillDamage(caster, t, caster.myData.baseAtk * 2.0f); } yield break; }
            case ActiveSkillType.MultiShot:
            { foreach (float ao in new[]{-40f,-20f,0f,20f,40f}) { var hits = Physics2D.RaycastAll(caster.transform.position, Rotate(caster.GetFacingDirection(), ao), 7f, caster.enemyLayer); if (hits.Length > 0) { var t = hits[0].collider.GetComponent<PlayerController>(); if (t != null && !t.IsDead && t != caster) DealSkillDamage(caster, t, caster.myData.baseAtk * 0.5f); } } yield break; }
            case ActiveSkillType.Trap: caster.StartCoroutine(TrapRoutine(caster, caster.Rb.position)); yield break;
            case ActiveSkillType.ArrowRain:
            { float el = 0f, next = 0.5f; while (el < 3f) { el += Time.deltaTime; if (el >= next) { next += 0.5f; foreach (var t in GetEnemiesInRadius(caster, 2.5f, targetPos)) DealSkillDamage(caster, t, caster.myData.baseAtk * 0.6f); } yield return null; } yield break; }
            // ── 사제 ──
            case ActiveSkillType.Smite:
            { var t = GetClosestEnemy(caster, 6f); if (t != null) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.0f); t.StatusFX.ApplyEffect(StatusEffectType.Slow, 2f, 0.2f, caster); } yield break; }
            case ActiveSkillType.HolyExplosion:
            { foreach (var t in GetEnemiesInRadius(caster, 2.5f)) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.0f); caster.StartCoroutine(KnockbackRoutine(t, ((Vector2)t.transform.position - caster.Rb.position).normalized, 4f)); } yield break; }
            case ActiveSkillType.HealingLight: caster.Heal(8f); yield break;
            case ActiveSkillType.GuardianAngel: caster.StatusFX.ApplyEffect(StatusEffectType.GuardianAngel, 3f); yield break;
            // ── 도적 ──
            case ActiveSkillType.PoisonDagger:
            { yield return new WaitForSeconds(0.2f); var t = GetClosestEnemy(caster, 5f); if (t != null) { DealSkillDamage(caster, t, caster.myData.baseAtk * 0.8f); t.StatusFX.ApplyEffect(StatusEffectType.Poison, 3f, 1.0f, caster); } yield break; }
            case ActiveSkillType.Ambush:
            { var t = GetClosestEnemy(caster, 2f); if (t != null) { bool behind = IsBehind(caster, t); DealSkillDamage(caster, t, caster.myData.baseAtk * (behind ? 2.0f : 1.0f)); } yield break; }
            case ActiveSkillType.SmokeBomb: caster.StatusFX.ApplyEffect(StatusEffectType.Stealth, 3f); yield break;
            case ActiveSkillType.ShadowRaid:
            { var t = GetClosestEnemy(caster, 12f); if (t == null) yield break; caster.Rb.position = (Vector2)t.transform.position - t.GetFacingDirection() * 0.8f; yield return null; DealSkillDamage(caster, t, caster.myData.baseAtk * 2.5f); t.StatusFX.ApplyEffect(StatusEffectType.Stun, 1f, 0f, caster); yield break; }
            // ── 암살자 ──
            case ActiveSkillType.VitalStrike:
            { var t = GetClosestEnemy(caster, 1.8f); if (t != null) { DealSkillDamage(caster, t, caster.myData.baseAtk * 0.8f); yield return new WaitForSeconds(0.15f); if (!t.IsDead) DealSkillDamage(caster, t, caster.myData.baseAtk * 0.8f); } yield break; }
            case ActiveSkillType.Shuriken:
            { Vector2 dir = caster.GetFacingDirection(); foreach (var h in Physics2D.RaycastAll(caster.transform.position, dir, 7f, caster.enemyLayer)) { var t = h.collider.GetComponent<PlayerController>(); if (t != null && !t.IsDead && t != caster) DealSkillDamage(caster, t, caster.myData.baseAtk * 0.6f); } yield return new WaitForSeconds(0.4f); foreach (var h in Physics2D.RaycastAll(caster.transform.position, -dir, 7f, caster.enemyLayer)) { var t = h.collider.GetComponent<PlayerController>(); if (t != null && !t.IsDead && t != caster) DealSkillDamage(caster, t, caster.myData.baseAtk * 0.6f); } yield break; }
            case ActiveSkillType.StealthSkill: caster.StatusFX.ApplyEffect(StatusEffectType.Stealth, 4f); yield break;
            case ActiveSkillType.DeathMark:
            { var t = GetClosestEnemy(caster, 15f); if (t == null) yield break; t.StatusFX.ApplyEffect(StatusEffectType.DeathMarkTarget, 3f, 0f, caster); yield return new WaitForSeconds(3f); if (!t.IsDead) { float bonus = t.myData.deathMarkAccumulated * 0.3f; DealSkillDamage(caster, t, caster.myData.baseAtk * 1.5f + bonus); } yield break; }
            // ── 요리사 ──
            case ActiveSkillType.FryingPan:
            { var t = GetClosestEnemy(caster, 1.8f); if (t != null) { DealSkillDamage(caster, t, caster.myData.baseAtk * 1.0f); if (Random.value < 0.2f) t.StatusFX.ApplyEffect(StatusEffectType.Stun, 1f, 0f, caster); } yield break; }
            case ActiveSkillType.BurningOil:
            { float el = 0f; var applied = new HashSet<PlayerController>(); while (el < 3f) { el += Time.deltaTime; foreach (var t in GetEnemiesInRadius(caster, 2.0f, targetPos)) if (!applied.Contains(t)) { applied.Add(t); t.StatusFX.ApplyEffect(StatusEffectType.Burn, 3f - el, 1.5f, caster); } yield return null; } yield break; }
            case ActiveSkillType.SnackTime: caster.StatusFX.RemoveAllDebuffs(); caster.Heal(5f); yield break;
            case ActiveSkillType.FeastTime:
            { yield return new WaitForSeconds(4f); foreach (var t in GetEnemiesInRadius(caster, 3.0f, targetPos)) DealSkillDamage(caster, t, caster.myData.baseAtk * 2.5f); yield break; }
        }
    }

    private static IEnumerator TrapRoutine(PlayerController caster, Vector2 trapPos)
    {
        float el = 0f; bool triggered = false;
        while (el < 15f && !triggered) { el += Time.deltaTime; foreach (var col in Physics2D.OverlapCircleAll(trapPos, 0.6f, caster.enemyLayer)) { var t = col.GetComponent<PlayerController>(); if (t != null && !t.IsDead) { triggered = true; t.TakeTrapDamage(2f, caster); t.StatusFX.ApplyEffect(StatusEffectType.Slow, 2f, 0.6f, caster); break; } } yield return null; }
    }

    private static IEnumerator KnockbackRoutine(PlayerController target, Vector2 dir, float force)
    {
        float el = 0f, dur = 0.2f;
        while (el < dur) { el += Time.deltaTime; target.Rb.MovePosition(target.Rb.position + dir * force * (1f - el / dur) * Time.deltaTime); yield return null; }
    }

    // 스킬 데미지 (패시브 반영)
    private static void DealSkillDamage(PlayerController caster, PlayerController target, float baseDamage)
    {
        if (target == null || target.IsDead || caster == null) return;
        float dmg = baseDamage * caster.StatusFX.GetAtkMultiplier();
        if (caster.myData.HasPassive(PassiveSkillType.LuckyStrike) && Random.value < 0.1f) { dmg *= 1.5f; caster.ShowSkillPopup("Lucky!"); }
        if (caster.myData.HasPassive(PassiveSkillType.Executioner) && target.myData.currentHp <= target.myData.maxHp * 0.25f) dmg += 1.5f;
        if (target.StatusFX.ConsumeDivineGrace()) return;
        if (target.StatusFX.IsImmune) return;
        dmg = target.StatusFX.AbsorbWithShield(dmg);
        if (target.StatusFX.IsInDefenseStance) dmg *= 0.5f;
        if (target.myData.HasPassive(PassiveSkillType.Guardian) && target.myData.currentHp <= target.myData.maxHp * 0.3f) dmg *= 0.8f;
        if (target.StatusFX.IsInUndyingRage) dmg *= 1.2f;
        if (target.myData.HasPassive(PassiveSkillType.Thorns) && Vector2.Distance(caster.transform.position, target.transform.position) <= 2.5f) { caster.myData.currentHp -= 0.5f; caster.ShowDotPopup(0.5f, Color.gray); }
        dmg = Mathf.Max(0f, Mathf.Round(dmg * 10f) / 10f);
        if (target.myData.deathMarkActive) target.myData.deathMarkAccumulated += dmg;
        target.myData.currentHp = Mathf.Max(target.myData.currentHp - dmg, 0f);
        if (caster.myData.HasPassive(PassiveSkillType.Lifesteal)) caster.Heal(Mathf.Max(0.5f, dmg * 0.2f));
        if (caster.StatusFX.IsInUndyingRage) caster.Heal(dmg * 0.5f);
        target.ShowSkillDamagePopup(dmg, Color.cyan);
        if (target.IsLocalPlayer && InGameHUD.Instance != null) InGameHUD.Instance.UpdateHealthBar(target.myData.currentHp, target.myData.maxHp);
        if (target.myData.currentHp <= 0f) target.TakeSkillDeath(caster);
    }

    private static List<PlayerController> GetEnemiesInRadius(PlayerController caster, float radius, Vector2? center = null)
    {
        Vector2 pos = center ?? (Vector2)caster.transform.position;
        var list = new List<PlayerController>();
        foreach (var col in Physics2D.OverlapCircleAll(pos, radius, caster.enemyLayer))
        { var pc = col.GetComponent<PlayerController>(); if (pc != null && !pc.IsDead && pc != caster) list.Add(pc); }
        return list;
    }

    private static List<PlayerController> GetEnemiesInCone(PlayerController caster, float radius, float angleDeg)
    {
        var result = new List<PlayerController>(); var fwd = caster.GetFacingDirection();
        foreach (var e in GetEnemiesInRadius(caster, radius)) { if (Vector2.Angle(fwd, ((Vector2)e.transform.position - caster.Rb.position).normalized) <= angleDeg * 0.5f) result.Add(e); }
        return result;
    }

    private static PlayerController GetClosestEnemy(PlayerController caster, float maxRange)
    {
        PlayerController closest = null; float minD = float.MaxValue;
        foreach (var e in GetEnemiesInRadius(caster, maxRange)) { float d = Vector2.Distance(caster.transform.position, e.transform.position); if (d < minD) { minD = d; closest = e; } }
        return closest;
    }

    private static Vector2 Rotate(Vector2 v, float deg) { float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r); return new Vector2(v.x*c - v.y*s, v.x*s + v.y*c); }
    private static bool IsBehind(PlayerController attacker, PlayerController target) => Vector2.Dot(target.GetFacingDirection(), ((Vector2)attacker.transform.position - (Vector2)target.transform.position).normalized) < -0.5f;
}