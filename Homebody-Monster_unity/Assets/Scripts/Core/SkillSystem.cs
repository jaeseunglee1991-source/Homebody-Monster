using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;

// ════════════════════════════════════════════════════════════════
//  SkillSystem — 서버 권한(Server-Authoritative) 버전
//
//  [구조 원칙]
//  • ActivateSkillServer : PlayerNetworkSync.RequestUseSkillServerRpc에서만 호출
//  • DealSkillDamageServer : ApplyDamageServer → NotifyHitClientRpc 경로로 전파
//  • CC 적용 : ApplyStatusEffectServer → SyncStatusEffectClientRpc 경로로 전파
//  • 이동 연출(돌진 등) : ForcePositionClientRpc로 Owner에게만 지시
//  • 회복 : HealServer로 NetworkHp 직접 수정
//
//  [원본과의 차이]
//  • DealSkillDamage  → DealSkillDamageServer  (로컬 HP 수정 제거)
//  • t.StatusFX.ApplyEffect → t.networkSync.ApplyStatusEffectServer
//  • caster.Heal → caster.HealServer
//  • 모든 40종 스킬 로직 완전 보존
// ════════════════════════════════════════════════════════════════
public static class SkillSystem
{
    private static readonly Dictionary<ActiveSkillType, float> Cooldowns =
        new Dictionary<ActiveSkillType, float>
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

    public static float GetCooldown(ActiveSkillType skill) =>
        Cooldowns.TryGetValue(skill, out float cd) ? cd : 10f;

    // ════════════════════════════════════════════════════════════
    //  서버 전용 진입점
    // ════════════════════════════════════════════════════════════

    /// <summary>RequestUseSkillServerRpc에서만 호출. 서버에서 코루틴 실행.</summary>
    public static void ActivateSkillServer(ActiveSkillType skill, PlayerController caster, Vector2 targetPos = default)
    {
        if (caster == null || caster.networkSync == null) return;
        if (caster.networkSync.NetworkIsDead.Value) return;
        if (caster.networkSync.ServerData == null) return;
        caster.StartCoroutine(RunSkillServer(skill, caster, targetPos));
    }

    // ════════════════════════════════════════════════════════════
    //  40종 스킬 스위치 — 서버에서 실행
    // ════════════════════════════════════════════════════════════

    private static IEnumerator RunSkillServer(ActiveSkillType skill, PlayerController caster, Vector2 targetPos)
    {
        var cData = caster.networkSync.ServerData;

        switch (skill)
        {
            // ── 전사 ──────────────────────────────────────────────
            case ActiveSkillType.Sweep:
                foreach (var t in GetEnemiesInCone(caster, 2.2f, 120f))
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.2f);
                yield break;

            case ActiveSkillType.ChargeStrike:
            {
                Vector2 dir  = caster.GetFacingDirection();
                Vector2 orig = caster.Rb.position;
                Vector2 dest = orig + dir * 2.5f;
                // 서버: 물리 이동 직접 수행 (ClientNetworkTransform이 owner에게 전달)
                float el = 0f;
                while (el < 0.15f)
                {
                    el += Time.deltaTime;
                    caster.Rb.MovePosition(Vector2.Lerp(orig, dest, el / 0.15f));
                    yield return null;
                }
                foreach (var t in GetEnemiesInRadius(caster, 1.5f))
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.5f);
                    t.networkSync.ApplyStatusEffectServer(StatusEffectType.Stun, 0.5f, 0f, caster.networkSync);
                }
                yield break;
            }

            case ActiveSkillType.DefenseStance:
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.DefenseStance, 3f, 0f, caster.networkSync);
                yield break;

            case ActiveSkillType.EarthquakeStrike:
                yield return new WaitForSeconds(0.1f);
                foreach (var t in GetEnemiesInRadius(caster, 3.5f))
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 2.0f);
                    t.networkSync.ApplyStatusEffectServer(StatusEffectType.Slow, 2f, 0.5f, caster.networkSync);
                }
                yield break;

            // ── 탱커 ──────────────────────────────────────────────
            case ActiveSkillType.ShieldBash:
            {
                var t = GetClosestEnemy(caster, 1.8f);
                if (t != null)
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 0.8f);
                    t.networkSync.ApplyStatusEffectServer(StatusEffectType.Stun, 1f, 0f, caster.networkSync);
                }
                yield break;
            }

            case ActiveSkillType.Shockwave:
            {
                var hits = Physics2D.RaycastAll(caster.transform.position, caster.GetFacingDirection(), 5f, caster.enemyLayer);
                foreach (var h in hits)
                {
                    var t = h.collider.GetComponent<PlayerController>();
                    if (t != null && !t.IsDead && t != caster)
                    {
                        DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                        t.networkSync.ApplyStatusEffectServer(StatusEffectType.AtkReduction, 2f, 0.2f, caster.networkSync);
                    }
                }
                yield break;
            }

            case ActiveSkillType.IronSkin:
                // 최대HP 20% 실드
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.ShieldHp, 4f,
                    cData.maxHp * 0.2f, caster.networkSync);
                yield break;

            case ActiveSkillType.Bulldozer:
            {
                Vector2 dir  = caster.GetFacingDirection();
                Vector2 orig = caster.Rb.position;
                Vector2 dest = orig + dir * 6f;
                float el = 0f;
                var hit = new HashSet<PlayerController>();
                while (el < 0.4f)
                {
                    el += Time.deltaTime;
                    caster.Rb.MovePosition(Vector2.Lerp(orig, dest, el / 0.4f));
                    foreach (var c2 in Physics2D.OverlapCircleAll(caster.Rb.position, 1.2f, caster.enemyLayer))
                    {
                        var t = c2.GetComponent<PlayerController>();
                        if (t != null && !t.IsDead && t != caster && !hit.Contains(t))
                        {
                            hit.Add(t);
                            DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                            caster.StartCoroutine(KnockbackRoutineServer(t, dir, 3f));
                        }
                    }
                    yield return null;
                }
                yield break;
            }

            // ── 팔라딘 ────────────────────────────────────────────
            case ActiveSkillType.HolyStrike:
            {
                var t = GetClosestEnemy(caster, 2f);
                if (t != null)
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.2f);
                    caster.HealServer(cData.maxHp * 0.05f); // 최대HP 5% 회복
                }
                yield break;
            }

            case ActiveSkillType.JudgmentHammer:
                yield return new WaitForSeconds(0.3f);
                foreach (var t in GetEnemiesInRadius(caster, 1.2f, targetPos))
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                    t.networkSync.ApplyStatusEffectServer(StatusEffectType.Slow, 1.5f, 0.3f, caster.networkSync);
                }
                yield break;

            case ActiveSkillType.DivineGrace:
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.DivineGrace, 2f, 0f, caster.networkSync);
                yield break;

            case ActiveSkillType.PillarOfJudgment:
                yield return new WaitForSeconds(0.6f);
                foreach (var t in GetEnemiesInRadius(caster, 1.5f, targetPos))
                    DealSkillDamageServer(caster, t, cData.baseAtk * 3.0f);
                yield break;

            // ── 버서커 ────────────────────────────────────────────
            case ActiveSkillType.RuthlessStrike:
            {
                float cost = cData.maxHp * 0.1f;
                if (caster.networkSync.NetworkHp.Value <= cost) yield break;
                // HP 10% 소모
                caster.networkSync.NetworkHp.Value   -= cost;
                cData.currentHp                       = caster.networkSync.NetworkHp.Value;
                var t = GetClosestEnemy(caster, 2f);
                if (t != null) DealSkillDamageServer(caster, t, cData.baseAtk * 2.0f);
                yield break;
            }

            case ActiveSkillType.BleedSlash:
            {
                var t = GetClosestEnemy(caster, 2f);
                if (t != null)
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                    t.networkSync.ApplyStatusEffectServer(StatusEffectType.Bleed, 3f,
                        cData.baseAtk * 0.3f, caster.networkSync); // 공격력 30% DoT
                }
                yield break;
            }

            case ActiveSkillType.UndyingRage:
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.UndyingRage, 4f, 0f, caster.networkSync);
                yield break;

            case ActiveSkillType.BladeStorm:
            {
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.BladeStormActive, 3f, 0f, caster.networkSync);
                float el = 0f, next = 0.5f;
                while (el < 3f && !caster.networkSync.NetworkIsDead.Value)
                {
                    el += Time.deltaTime;
                    if (el >= next)
                    {
                        next += 0.5f;
                        foreach (var t in GetEnemiesInRadius(caster, 2.0f))
                            DealSkillDamageServer(caster, t, cData.baseAtk * 0.5f);
                    }
                    yield return null;
                }
                yield break;
            }

            // ── 마법사 ────────────────────────────────────────────
            case ActiveSkillType.Fireball:
            {
                // NetworkProjectile.ApplySkillDebuff에서 Burn CC 자동 적용
                Vector2 dir = (targetPos != default && targetPos != (Vector2)caster.transform.position)
                    ? (targetPos - (Vector2)caster.transform.position).normalized
                    : caster.GetFacingDirection();
                if (dir == Vector2.zero) dir = Vector2.right;
                SpawnProjectile("Projectiles/Fireball", ActiveSkillType.Fireball, caster, dir, cData.baseAtk * 1.5f, 8f, 6f);
                yield break;
            }

            case ActiveSkillType.IceShards:
            {
                // 3방향 부채꼴 발사. NetworkProjectile.ApplySkillDebuff에서 Root CC 자동 적용
                Vector2 baseDir = (targetPos != default && targetPos != (Vector2)caster.transform.position)
                    ? (targetPos - (Vector2)caster.transform.position).normalized
                    : caster.GetFacingDirection();
                if (baseDir == Vector2.zero) baseDir = Vector2.right;
                foreach (float offset in new[] { 0f, -25f, 25f })
                    SpawnProjectile("Projectiles/IceShard", ActiveSkillType.IceShards, caster, Rotate(baseDir, offset), cData.baseAtk * 0.8f, 10f, 4f);
                yield break;
            }

            case ActiveSkillType.IceShield:
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.IceShield, 2.5f, 0f, caster.networkSync);
                yield break;

            case ActiveSkillType.Meteor:
                yield return new WaitForSeconds(1f);
                foreach (var t in GetEnemiesInRadius(caster, 3.5f, targetPos))
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 2.5f);
                    t.networkSync.ApplyStatusEffectServer(StatusEffectType.Burn, 3f,
                        cData.baseAtk * 0.5f, caster.networkSync);
                }
                yield break;

            // ── 궁수 ──────────────────────────────────────────────
            case ActiveSkillType.PierceArrow:
            {
                Vector2 dir = (targetPos != default && targetPos != (Vector2)caster.transform.position)
                    ? (targetPos - (Vector2)caster.transform.position).normalized
                    : caster.GetFacingDirection();
                if (dir == Vector2.zero) dir = Vector2.right;
                var proj = SpawnProjectile("Projectiles/PierceArrow", ActiveSkillType.PierceArrow, caster, dir, cData.baseAtk * 2.0f, 12f, 5f);
                if (proj != null) { proj.isPiercing = true; proj.maxPierceCount = 5; }
                yield break;
            }

            case ActiveSkillType.MultiShot:
            {
                Vector2 baseDir = (targetPos != default && targetPos != (Vector2)caster.transform.position)
                    ? (targetPos - (Vector2)caster.transform.position).normalized
                    : caster.GetFacingDirection();
                if (baseDir == Vector2.zero) baseDir = Vector2.right;
                foreach (float offset in new[] { -40f, -20f, 0f, 20f, 40f })
                    SpawnProjectile("Projectiles/PierceArrow", ActiveSkillType.MultiShot, caster, Rotate(baseDir, offset), cData.baseAtk * 0.5f, 12f, 4f);
                yield break;
            }

            case ActiveSkillType.Trap:
                caster.StartCoroutine(TrapRoutineServer(caster, caster.Rb.position));
                yield break;

            case ActiveSkillType.ArrowRain:
            {
                float el = 0f, next = 0.5f;
                while (el < 3f)
                {
                    el += Time.deltaTime;
                    if (el >= next)
                    {
                        next += 0.5f;
                        foreach (var t in GetEnemiesInRadius(caster, 2.5f, targetPos))
                            DealSkillDamageServer(caster, t, cData.baseAtk * 0.6f);
                    }
                    yield return null;
                }
                yield break;
            }

            // ── 사제 ──────────────────────────────────────────────
            case ActiveSkillType.Smite:
            {
                var t = GetClosestEnemy(caster, 6f);
                if (t != null)
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                    t.networkSync.ApplyStatusEffectServer(StatusEffectType.Slow, 2f, 0.2f, caster.networkSync);
                }
                yield break;
            }

            case ActiveSkillType.HolyExplosion:
                foreach (var t in GetEnemiesInRadius(caster, 2.5f))
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                    caster.StartCoroutine(KnockbackRoutineServer(t,
                        ((Vector2)t.transform.position - caster.Rb.position).normalized, 4f));
                }
                yield break;

            case ActiveSkillType.HealingLight:
                caster.HealServer(cData.maxHp * 0.25f); // 최대HP 25% 회복
                yield break;

            case ActiveSkillType.GuardianAngel:
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.GuardianAngel, 3f, 0f, caster.networkSync);
                yield break;

            // ── 도적 ──────────────────────────────────────────────
            case ActiveSkillType.PoisonDagger:
            {
                // NetworkProjectile.ApplySkillDebuff에서 Poison DoT 자동 적용
                Vector2 dir = (targetPos != default && targetPos != (Vector2)caster.transform.position)
                    ? (targetPos - (Vector2)caster.transform.position).normalized
                    : caster.GetFacingDirection();
                if (dir == Vector2.zero) dir = Vector2.right;
                SpawnProjectile("Projectiles/PoisonDagger", ActiveSkillType.PoisonDagger, caster, dir, cData.baseAtk * 1.0f, 11f, 4f);
                yield break;
            }

            case ActiveSkillType.Ambush:
            {
                var t = GetClosestEnemy(caster, 2f);
                if (t != null)
                {
                    bool behind = IsBehind(caster, t);
                    DealSkillDamageServer(caster, t, cData.baseAtk * (behind ? 2.0f : 1.0f));
                }
                yield break;
            }

            case ActiveSkillType.SmokeBomb:
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.Stealth, 3f, 0f, caster.networkSync);
                yield break;

            case ActiveSkillType.ShadowRaid:
            {
                var t = GetClosestEnemy(caster, 12f);
                if (t == null) yield break;
                // 순간이동: 서버에서 위치 설정 (ClientNetworkTransform이 Owner에게 전달)
                caster.Rb.position = (Vector2)t.transform.position - t.GetFacingDirection() * 0.8f;
                yield return null;
                DealSkillDamageServer(caster, t, cData.baseAtk * 2.5f);
                t.networkSync.ApplyStatusEffectServer(StatusEffectType.Stun, 1f, 0f, caster.networkSync);
                yield break;
            }

            // ── 암살자 ────────────────────────────────────────────
            case ActiveSkillType.VitalStrike:
            {
                var t = GetClosestEnemy(caster, 1.8f);
                if (t != null)
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 0.8f);
                    yield return new WaitForSeconds(0.15f);
                    if (!t.networkSync.NetworkIsDead.Value)
                        DealSkillDamageServer(caster, t, cData.baseAtk * 0.8f);
                }
                yield break;
            }

            case ActiveSkillType.Shuriken:
            {
                Vector2 dir = caster.GetFacingDirection();
                foreach (var h in Physics2D.RaycastAll(caster.transform.position, dir, 7f, caster.enemyLayer))
                {
                    var t = h.collider.GetComponent<PlayerController>();
                    if (t != null && !t.IsDead && t != caster)
                        DealSkillDamageServer(caster, t, cData.baseAtk * 0.6f);
                }
                yield return new WaitForSeconds(0.4f);
                foreach (var h in Physics2D.RaycastAll(caster.transform.position, -dir, 7f, caster.enemyLayer))
                {
                    var t = h.collider.GetComponent<PlayerController>();
                    if (t != null && !t.IsDead && t != caster)
                        DealSkillDamageServer(caster, t, cData.baseAtk * 0.6f);
                }
                yield break;
            }

            case ActiveSkillType.StealthSkill:
                caster.networkSync.ApplyStatusEffectServer(StatusEffectType.Stealth, 4f, 0f, caster.networkSync);
                yield break;

            case ActiveSkillType.DeathMark:
            {
                var t = GetClosestEnemy(caster, 15f);
                if (t == null) yield break;
                t.networkSync.ApplyStatusEffectServer(StatusEffectType.DeathMarkTarget, 3f, 0f, caster.networkSync);
                yield return new WaitForSeconds(3f);
                if (!t.networkSync.NetworkIsDead.Value)
                {
                    float bonus = t.networkSync.ServerData.deathMarkAccumulated * 0.3f;
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.5f + bonus);
                }
                yield break;
            }

            // ── 요리사 ────────────────────────────────────────────
            case ActiveSkillType.FryingPan:
            {
                var t = GetClosestEnemy(caster, 1.8f);
                if (t != null)
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                    if (Random.value < 0.2f)
                        t.networkSync.ApplyStatusEffectServer(StatusEffectType.Stun, 1f, 0f, caster.networkSync);
                }
                yield break;
            }

            case ActiveSkillType.BurningOil:
            {
                float el = 0f;
                var applied = new HashSet<PlayerController>();
                while (el < 3f)
                {
                    el += Time.deltaTime;
                    foreach (var t in GetEnemiesInRadius(caster, 2.0f, targetPos))
                    {
                        if (!applied.Contains(t))
                        {
                            applied.Add(t);
                            t.networkSync.ApplyStatusEffectServer(StatusEffectType.Burn, 3f - el,
                                1.5f, caster.networkSync);
                        }
                    }
                    yield return null;
                }
                yield break;
            }

            case ActiveSkillType.SnackTime:
                // 디버프 해제: 서버에서 각 디버프 제거 RPC 전파
                BroadcastRemoveAllDebuffsServer(caster);
                caster.HealServer(caster.networkSync.ServerData.maxHp * 0.15f); // 최대HP 15% 회복
                yield break;

            case ActiveSkillType.FeastTime:
                yield return new WaitForSeconds(4f);
                foreach (var t in GetEnemiesInRadius(caster, 3.0f, targetPos))
                    DealSkillDamageServer(caster, t, cData.baseAtk * 2.5f);
                yield break;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  서버 전용 서브루틴
    // ════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════
    //  투사체 스폰 헬퍼 (서버 전용)
    //  NetworkProjectile이 충돌 시 데미지+CC를 자동 처리합니다.
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ProjectileRegistry에서 prefabKey로 프리팹을 가져와 서버에서 스폰합니다.
    /// 스킬 타입을 함께 전달하면 NetworkProjectile.ApplySkillDebuff에서 CC가 자동 적용됩니다.
    /// </summary>
    private static NetworkProjectile SpawnProjectile(
        string prefabKey, ActiveSkillType skill, PlayerController caster,
        Vector2 dir, float damage, float speed, float lifeTime = 5f)
    {
        GameObject prefab = ProjectileRegistry.GetPrefab(prefabKey);
        if (prefab == null)
        {
            Debug.LogWarning($"[SkillSystem] 투사체 프리팹 없음: {prefabKey}");
            return null;
        }

        Vector2 spawnPos = caster.Rb.position + dir * 0.5f;
        GameObject obj  = Object.Instantiate(prefab, spawnPos, Quaternion.identity);

        var netObj = obj.GetComponent<NetworkObject>();
        if (netObj == null) { Object.Destroy(obj); return null; }
        netObj.Spawn(destroyWithScene: true);

        var projectile = obj.GetComponent<NetworkProjectile>();
        if (projectile == null) { netObj.Despawn(); return null; }

        projectile.Initialize(damage, skill, caster.networkSync, dir, speed, lifeTime);
        return projectile;
    }

    private static IEnumerator TrapRoutineServer(PlayerController caster, Vector2 trapPos)
    {
        float el = 0f; bool triggered = false;
        while (el < 15f && !triggered)
        {
            el += Time.deltaTime;
            foreach (var col in Physics2D.OverlapCircleAll(trapPos, 0.6f, caster.enemyLayer))
            {
                var t = col.GetComponent<PlayerController>();
                if (t != null && !t.IsDead && t.networkSync != null)
                {
                    triggered = true;
                    // 트랩 데미지 — 공격력 150%
                    t.networkSync.ApplyDamageServer(caster.networkSync.ServerData.baseAtk * 1.5f, caster.networkSync);
                    t.networkSync.ApplyStatusEffectServer(StatusEffectType.Slow, 2f, 0.6f, caster.networkSync);
                    break;
                }
            }
            yield return null;
        }
    }

    private static IEnumerator KnockbackRoutineServer(PlayerController target, Vector2 dir, float force)
    {
        float el = 0f, dur = 0.2f;
        while (el < dur)
        {
            el += Time.deltaTime;
            target.Rb.MovePosition(target.Rb.position + dir * force * (1f - el / dur) * Time.deltaTime);
            yield return null;
        }
    }

    /// <summary>SnackTime — 서버에서 대상(caster)의 모든 디버프를 제거하고 클라이언트에 전파</summary>
    private static void BroadcastRemoveAllDebuffsServer(PlayerController caster)
    {
        caster.StatusFX.RemoveAllDebuffs();
        // 모든 클라이언트에 즉시 전파 → 이동 잠금/스턴 등 시각 상태 동기 해제
        caster.networkSync?.BroadcastRemoveAllDebuffs();
    }

    // ════════════════════════════════════════════════════════════
    //  서버 전용 스킬 데미지 처리
    // ════════════════════════════════════════════════════════════

    private static void DealSkillDamageServer(PlayerController caster, PlayerController target, float baseDamage)
    {
        if (caster == null || target == null) return;
        if (target.networkSync == null || target.networkSync.NetworkIsDead.Value) return;

        var cData = caster.networkSync.ServerData;
        var tData = target.networkSync.ServerData;
        if (cData == null || tData == null) return;

        float dmg = baseDamage * caster.StatusFX.GetAtkMultiplier();

        // 행운의 일격 패시브
        if (cData.HasPassive(PassiveSkillType.LuckyStrike) && Random.value < 0.1f)
        {
            dmg *= 1.5f;
            // 서버에서 caster 소유 클라이언트에게만 ClientRpc로 팝업 전파
            caster.networkSync.ShowLuckyPopupOwner();
        }

        // 처형인 패시브 (HP 25% 이하 대상)
        if (cData.HasPassive(PassiveSkillType.Executioner) && tData.currentHp <= tData.maxHp * 0.25f)
            dmg *= 1.3f;

        // 방어 체크
        if (target.StatusFX.ConsumeDivineGrace()) return;
        if (target.StatusFX.IsImmune)             return;

        dmg = target.StatusFX.AbsorbWithShield(dmg);
        if (target.StatusFX.IsInDefenseStance) dmg *= 0.5f;
        if (tData.HasPassive(PassiveSkillType.Guardian) && tData.currentHp <= tData.maxHp * 0.3f) dmg *= 0.8f;
        if (target.StatusFX.IsInUndyingRage)   dmg *= 1.2f;

        // 가시 패시브 — 스킬 데미지의 10% 반사
        if (tData.HasPassive(PassiveSkillType.Thorns) &&
            Vector2.Distance(caster.transform.position, target.transform.position) <= 2.5f)
        {
            float reflectDmg = Mathf.Max(0.5f, dmg * 0.1f);
            caster.networkSync.ApplyDamageServer(reflectDmg, target.networkSync);
        }

        dmg = Mathf.Max(0f, Mathf.Round(dmg * 10f) / 10f);
        if (tData.deathMarkActive) tData.deathMarkAccumulated += dmg;

        // 최종 데미지 적용 (→ NetworkHp 수정 + NotifyHitClientRpc)
        target.networkSync.ApplyDamageServer(dmg, caster.networkSync);

        // 흡혈 패시브
        if (cData.HasPassive(PassiveSkillType.Lifesteal))
            caster.HealServer(Mathf.Max(0.5f, dmg * 0.2f));
        if (caster.StatusFX.IsInUndyingRage)
            caster.HealServer(dmg * 0.5f);
    }

    // ════════════════════════════════════════════════════════════
    //  공간 탐색 유틸 — 원본 동일 유지
    // ════════════════════════════════════════════════════════════

    private static List<PlayerController> GetEnemiesInRadius(
        PlayerController caster, float radius, Vector2? center = null)
    {
        Vector2 pos = center ?? (Vector2)caster.transform.position;
        var list = new List<PlayerController>();
        foreach (var col in Physics2D.OverlapCircleAll(pos, radius, caster.enemyLayer))
        {
            var pc = col.GetComponent<PlayerController>();
            if (pc != null && !pc.IsDead && pc != caster &&
                pc.networkSync != null && !pc.networkSync.NetworkIsDead.Value)
                list.Add(pc);
        }
        return list;
    }

    private static List<PlayerController> GetEnemiesInCone(
        PlayerController caster, float radius, float angleDeg)
    {
        var result = new List<PlayerController>();
        var fwd    = caster.GetFacingDirection();
        foreach (var e in GetEnemiesInRadius(caster, radius))
        {
            Vector2 toE = ((Vector2)e.transform.position - caster.Rb.position).normalized;
            if (Vector2.Angle(fwd, toE) <= angleDeg * 0.5f) result.Add(e);
        }
        return result;
    }

    private static PlayerController GetClosestEnemy(PlayerController caster, float maxRange)
    {
        PlayerController closest = null;
        float minD = float.MaxValue;
        foreach (var e in GetEnemiesInRadius(caster, maxRange))
        {
            float d = Vector2.Distance(caster.transform.position, e.transform.position);
            if (d < minD) { minD = d; closest = e; }
        }
        return closest;
    }

    private static Vector2 Rotate(Vector2 v, float deg)
    {
        float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }

    private static bool IsBehind(PlayerController attacker, PlayerController target) =>
        Vector2.Dot(target.GetFacingDirection(),
            ((Vector2)attacker.transform.position - (Vector2)target.transform.position).normalized) < -0.5f;
}
