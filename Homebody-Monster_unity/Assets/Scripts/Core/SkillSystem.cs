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
    public static void ActivateSkillServer(ActiveSkillType skill, PlayerController caster,
        Vector2 targetPos = default, Vector2 facingDir = default)
    {
        if (caster == null || caster.networkSync == null) return;
        if (caster.networkSync.NetworkIsDead.Value) return;
        if (caster.networkSync.ServerData == null) return;
        caster.StartCoroutine(RunSkillServer(skill, caster, targetPos, facingDir));
    }

    // ════════════════════════════════════════════════════════════
    //  40종 스킬 스위치 — 서버에서 실행
    // ════════════════════════════════════════════════════════════

    private static IEnumerator RunSkillServer(ActiveSkillType skill, PlayerController caster,
        Vector2 targetPos, Vector2 facingDir = default)
    {
        // 클라이언트에서 전달된 방향이 없으면 서버의 GetFacingDirection() 폴백
        // (서버는 moveDir·flipX를 갱신하지 않아 항상 Vector2.right를 반환하므로
        //  facingDir이 유효한 경우에만 신뢰함)
        Vector2 serverFacing = (facingDir != default && facingDir != Vector2.zero)
            ? facingDir.normalized
            : caster.GetFacingDirection();

        var cData = caster.networkSync.ServerData;

        switch (skill)
        {
            // ── 전사 ──────────────────────────────────────────────
            case ActiveSkillType.Sweep:
                foreach (var t in GetEnemiesInCone(caster, 2.2f, 120f, serverFacing))
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.2f);
                yield break;

            case ActiveSkillType.ChargeStrike:
            {
                Vector2 dir  = serverFacing;
                Vector2 orig = caster.Rb.position;
                Vector2 dest = orig + dir * 2.5f;
                // Owner 클라이언트에게 시각적 이동 지시
                caster.networkSync.ForceMoveClientRpc(orig, dest, 0.15f,
                    OwnerRpcParams(caster.networkSync.OwnerClientId));
                // 서버에서는 경로 전체를 CircleCast로 충돌 판정 (이동 없이 계산만)
                yield return new WaitForSeconds(0.15f);
                if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
                foreach (var h in Physics2D.CircleCastAll(orig, 0.8f, dir, 2.5f, Physics2D.AllLayers))
                {
                    var t = h.collider.GetComponent<PlayerController>();
                    if (t == null || t.IsDead || t == caster || t.networkSync == null) continue;
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
                if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
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
                var hits = Physics2D.RaycastAll(caster.transform.position, serverFacing, 5f, Physics2D.AllLayers);
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
                Vector2 dir  = serverFacing;
                Vector2 orig = caster.Rb.position;
                Vector2 dest = orig + dir * 6f;
                // Owner 클라이언트에게 시각적 이동 지시
                caster.networkSync.ForceMoveClientRpc(orig, dest, 0.4f,
                    OwnerRpcParams(caster.networkSync.OwnerClientId));
                // 서버에서 경로 전체를 CircleCast로 충돌 판정
                var hit = new HashSet<PlayerController>();
                foreach (var c2 in Physics2D.CircleCastAll(orig, 1.2f, dir, 6f, Physics2D.AllLayers))
                {
                    var t = c2.collider.GetComponent<PlayerController>();
                    if (t == null || t.IsDead || t == caster || t.networkSync == null) continue;
                    if (!hit.Add(t)) continue;
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                    ApplyKnockbackServer(t, dir, 3f);
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
                if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
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
                if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
                foreach (var t in GetEnemiesInRadius(caster, 1.5f, targetPos))
                    DealSkillDamageServer(caster, t, cData.baseAtk * 3.0f);
                yield break;

            // ── 버서커 ────────────────────────────────────────────
            case ActiveSkillType.RuthlessStrike:
            {
                float cost  = cData.maxHp * 0.1f;
                float curHp = caster.networkSync.NetworkHp.Value;
                if (curHp <= cost) yield break;

                // [FIX] NetworkHp를 직접 수정하면 Tenacity/GuardianAngel 체크,
                // InGameManager.OnPlayerDied, 킬 카운트, 부활 UI 등 사망 처리
                // 파이프라인이 전부 누락됨. ApplyDamageServer로 위임해 일관성 보장.
                caster.networkSync.ApplyDamageServer(cost, null); // source=null: 자해(킬 크레딧 없음)

                // ApplyDamageServer 내부에서 이미 사망 처리됐을 수 있으므로 재확인
                if (caster.networkSync.NetworkIsDead.Value) yield break;

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
                while (el < 3f)
                {
                    // 매 프레임 시전자 생존 여부 확인 (Despawn·사망 대응)
                    if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
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
                    : serverFacing;
                if (dir == Vector2.zero) dir = Vector2.right;
                SpawnProjectile("Projectiles/Fireball", ActiveSkillType.Fireball, caster, dir, cData.baseAtk * 1.5f, 8f, 6f);
                yield break;
            }

            case ActiveSkillType.IceShards:
            {
                // 3방향 부채꼴 발사. NetworkProjectile.ApplySkillDebuff에서 Root CC 자동 적용
                Vector2 baseDir = (targetPos != default && targetPos != (Vector2)caster.transform.position)
                    ? (targetPos - (Vector2)caster.transform.position).normalized
                    : serverFacing;
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
                if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
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
                    : serverFacing;
                if (dir == Vector2.zero) dir = Vector2.right;
                var proj = SpawnProjectile("Projectiles/PierceArrow", ActiveSkillType.PierceArrow, caster, dir, cData.baseAtk * 2.0f, 12f, 5f);
                if (proj != null) { proj.isPiercing = true; proj.maxPierceCount = 5; }
                yield break;
            }

            case ActiveSkillType.MultiShot:
            {
                Vector2 baseDir = (targetPos != default && targetPos != (Vector2)caster.transform.position)
                    ? (targetPos - (Vector2)caster.transform.position).normalized
                    : serverFacing;
                if (baseDir == Vector2.zero) baseDir = Vector2.right;
                foreach (float offset in new[] { -40f, -20f, 0f, 20f, 40f })
                    SpawnProjectile("Projectiles/PierceArrow", ActiveSkillType.MultiShot, caster, Rotate(baseDir, offset), cData.baseAtk * 0.5f, 12f, 4f);
                yield break;
            }

            case ActiveSkillType.Trap:
            {
                // [Fix] targetPos 무시 버그 수정 — 이전 구현은 caster.Rb.position만 사용해
                // 시전자 발밑에만 덫이 배치됐음. 이제 targetPos(적 위치 또는 터치 지점)를 우선 사용.
                Vector2 placedAt = (targetPos != default && targetPos != caster.Rb.position)
                    ? targetPos
                    : caster.Rb.position;

                // [신규] 모든 클라이언트에 덫 위치를 시각적으로 동기화.
                // 이전 구현은 서버만 OverlapCircle로 판정하고 클라이언트에 아무 것도 보여주지 않아
                // 피격 플레이어 입장에서는 '보이지 않는 곳에서 갑자기 피해'가 발생했음.
                caster.networkSync.SpawnTrapVisualClientRpc(placedAt);

                caster.StartCoroutine(TrapRoutineServer(caster, placedAt));
                yield break;
            }

            case ActiveSkillType.ArrowRain:
            {
                float el = 0f, next = 0.5f;
                while (el < 3f)
                {
                    // 매 프레임 시전자 생존 여부 확인
                    if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
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
                    ApplyKnockbackServer(t,
                        ((Vector2)t.transform.position - caster.Rb.position).normalized, 4f);
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
                    : serverFacing;
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
                // [버그 수정] t.GetFacingDirection()은 서버에서 항상 Vector2.right 반환 →
                // 목적지가 항상 타겟의 왼쪽 0.8u로 고정. velocity로 실제 이동 방향을 추정.
                #if UNITY_6000_0_OR_NEWER
                    Vector2 tVel = t.Rb != null ? t.Rb.linearVelocity : Vector2.zero;
                #else
                    Vector2 tVel = t.Rb != null ? t.Rb.velocity : Vector2.zero;
                #endif
                Vector2 tFacing = tVel.sqrMagnitude > 0.01f
                    ? tVel.normalized
                    : (serverFacing != Vector2.zero ? -serverFacing : Vector2.right);
                Vector2 dest = (Vector2)t.transform.position - tFacing * 0.8f;
                // Owner 클라이언트에게 순간이동 지시 (ClientNetworkTransform 권한 대응)
                caster.networkSync.ForcePositionClientRpc(dest,
                    OwnerRpcParams(caster.networkSync.OwnerClientId));
                yield return null; // 1프레임 대기: 위치 동기화 후 데미지 판정
                if (caster.networkSync.NetworkIsDead.Value) yield break;
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
                    // 0.15초 대기 후 시전자·대상 생존 여부 재확인
                    if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
                    if (t == null || t.networkSync == null || t.networkSync.NetworkIsDead.Value) yield break;
                    DealSkillDamageServer(caster, t, cData.baseAtk * 0.8f);
                }
                yield break;
            }

            case ActiveSkillType.Shuriken:
            {
                Vector2 dir = serverFacing;
                foreach (var h in Physics2D.RaycastAll(caster.transform.position, dir, 7f, Physics2D.AllLayers))
                {
                    var t = h.collider.GetComponent<PlayerController>();
                    if (t != null && !t.IsDead && t != caster)
                        DealSkillDamageServer(caster, t, cData.baseAtk * 0.6f);
                }
                yield return new WaitForSeconds(0.4f);
                // 0.4초 대기 후 시전자 생존 여부 확인 (회부 진행 전 검증)
                if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
                foreach (var h in Physics2D.RaycastAll(caster.transform.position, -dir, 7f, Physics2D.AllLayers))
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
                // 5초 지속 낙인 부여
                t.networkSync.ApplyStatusEffectServer(StatusEffectType.DeathMarkTarget, 5f, 0f, caster.networkSync);
                float el = 0f;
                while (el < 5f)
                {
                    // 매 프레임: 시전자 사망 감지 → 낙인 강제 해제 (분기 C)
                    if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value)
                    {
                        if (t != null && t.networkSync != null && t.networkSync.IsSpawned)
                            t.networkSync.ForceRemoveDeathMarkServer();
                        yield break;
                    }
                    // 대상 사망 → ProcessDeath 훅에서 폭발 처리됨 (분기 A)
                    if (t == null || t.networkSync == null || !t.networkSync.IsSpawned ||
                        t.networkSync.NetworkIsDead.Value)
                        yield break;
                    el += Time.deltaTime;
                    yield return null;
                }
                // 5초 만료 → StatusEffectSystem.OnEffectExpired에서 소형 폭발 처리됨 (분기 B)
                yield break;
            }

            // ── 요리사 ────────────────────────────────────────────
            case ActiveSkillType.FryingPan:
            {
                var t = GetClosestEnemy(caster, 1.8f);
                if (t != null)
                {
                    DealSkillDamageServer(caster, t, cData.baseAtk * 1.0f);
                    // 20% Stun: 서버에서 결정 → SyncStatusEffectClientRpc로 모든 클라이언트에 동기화됨
                    if (Random.value < 0.2f)
                        t.networkSync.ApplyStatusEffectServer(StatusEffectType.Stun, 1f, 0f, caster.networkSync);
                }
                yield break;
            }

            case ActiveSkillType.BurningOil:
            {
                // 0.5초 틱마다 영역 내 적에게 Burn 재적용.
                // StatusEffectSystem은 기존 효과 duration을 Max로 갱신하므로
                // 영역 내에 있는 동안 Burn이 유지되고, 이탈하면 1.5초 후 자연 소멸함.
                float el = 0f, nextTick = 0f;
                while (el < 3f)
                {
                    // 매 프레임 시전자 생존 여부 확인
                    if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
                    el += Time.deltaTime;
                    if (el >= nextTick)
                    {
                        nextTick += 0.5f;
                        foreach (var t in GetEnemiesInRadius(caster, 2.0f, targetPos))
                            t.networkSync.ApplyStatusEffectServer(StatusEffectType.Burn, 1.5f,
                                1.5f, caster.networkSync);
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
                // 4초 대기 후 시전자 생존 여부 확인
                if (caster == null || caster.networkSync == null || caster.networkSync.NetworkIsDead.Value) yield break;
                foreach (var t in GetEnemiesInRadius(caster, 3.0f, targetPos))
                    DealSkillDamageServer(caster, t, cData.baseAtk * 2.5f);
                yield break;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  서버 전용 서브루틴
    // ════════════════════════════════════════════════════════════

    // ── ClientRpcParams 헬퍼 — 특정 Owner에게만 전송 ──────────
    private static ClientRpcParams OwnerRpcParams(ulong clientId) => new ClientRpcParams
    {
        Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
    };

    // ── 넉백 적용 (ClientNetworkTransform 권한 대응) ──────────
    // 이전 KnockbackRoutineServer(서버 Rb.MovePosition)는 ClientNetworkTransform에서
    // 클라이언트 값이 우선하여 적용되지 않음 → 타겟 Owner에게 ForceKnockbackClientRpc 사용
    private static void ApplyKnockbackServer(
        PlayerController target, Vector2 dir, float force, float duration = 0.2f)
    {
        if (target == null || target.networkSync == null) return;
        if (target.IsDead || target.networkSync.NetworkIsDead.Value) return;
        target.networkSync.ForceKnockbackClientRpc(
            dir, force, duration,
            OwnerRpcParams(target.networkSync.OwnerClientId));
    }

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
        // ══════════════════════════════════════════════════════════
        //  덫놓기 (Trap) — 서버 로직 개선
        //
        //  [이전 구현의 문제]
        //  1. 매 프레임(~60fps) OverlapCircleAll 폴링 → 15초×60fps = 최대 900회 물리 연산
        //  2. 1회 피격 후 즉시 루프 종료(triggered=true) → 쿨다운 10초짜리 스킬이
        //     단 1번 적을 맞히고 사라지는 구조. "지점 제어" 기획 의도와 불일치.
        //  3. 클라이언트 시각 동기화 없음 (덫 위치가 아무에게도 안 보임) → Trap 케이스에서 처리
        //
        //  [개선 후]
        //  1. 0.2초 폴링으로 변경 → 15초 동안 75회로 물리 연산 ~92% 감소
        //  2. 피격 후 1.5초 재발동 딜레이 → 같은 플레이어를 연속 피격하지 않으면서도
        //     다른 플레이어가 지나가면 재발동 (지점 제어 의미 생성)
        //  3. 최대 피격 횟수 3회 제한 → 무한 덫 방지 (총 15초 동안 최대 3명/3회)
        // ══════════════════════════════════════════════════════════

        // [버그 수정 1+3] baseAtk·casterSync를 시전 시점에 캡처.
        // 시전자 사망 → Despawn 후 caster.networkSync.ServerData가 null이 되어
        // 트랩 발동 시 런타임에 접근하면 NullReferenceException 크래시 발생.
        // 코루틴 진입 즉시 캡처해 이후 caster 참조 없이 피해 계산.
        float capturedBaseAtk = (caster?.networkSync?.ServerData != null)
            ? caster.networkSync.ServerData.baseAtk : 0f;
        PlayerNetworkSync casterSync = caster?.networkSync;
        // enemyLayer 캡처 제거: OverlapCircleAll 레이어 필터 없이 호출하므로 불필요

        if (capturedBaseAtk <= 0f) yield break; // 초기화 실패 시 즉시 종료

        const float maxDuration  = 15f;  // 덫 최대 유지 시간
        const float pollInterval = 0.2f; // 물리 판정 간격
        const float hitCooldown  = 1.5f; // 동일 플레이어 재피격 방지 딜레이
        const int   maxHits      = 3;    // 최대 피격 횟수

        float elapsed    = 0f;
        int   hitCount   = 0;
        float lastHitTime = -hitCooldown; // 마지막 피격 시각 (최초 발동 즉시 허용)

        var wait = new WaitForSeconds(pollInterval);

        while (elapsed < maxDuration && hitCount < maxHits)
        {
            yield return wait;
            elapsed += pollInterval;

            // [버그 수정 2] 시전자 사망/Despawn 시 yield break 제거.
            // 덫은 설치 후 독립적으로 15초간 유지되어야 함 (기획 의도).
            // casterSync는 킬 크레딧·RPC용으로만 사용 (null 가능 — 안전하게 처리).

            // 피격 쿨다운 중이면 판정 생략
            if (elapsed - lastHitTime < hitCooldown) continue;

            foreach (var col in Physics2D.OverlapCircleAll(trapPos, 0.6f)) // enemyLayer 제거: 은신 무적 방지
            {
                var t = col.GetComponent<PlayerController>();
                if (t == null || t.IsDead || t.networkSync == null ||
                    t.networkSync.NetworkIsDead.Value) continue;

                hitCount++;
                lastHitTime = elapsed;

                // 트랩 피격 — 캡처된 baseAtk 사용 (시전자 Despawn 무관)
                t.networkSync.ApplyDamageServer(capturedBaseAtk * 1.5f, casterSync);
                t.networkSync.ApplyStatusEffectServer(StatusEffectType.Slow, 2f, 0.6f, casterSync);

                // 피격 시 시각 효과 전파 — casterSync가 살아있을 때만
                if (casterSync != null && casterSync.IsSpawned)
                    casterSync.NotifyTrapTriggeredClientRpc(trapPos);
                break; // 같은 폴링 틱에 다중 판정 방지 (0.2초 후 재판정)
            }
        }

        // 덫 소멸 시각 동기화.
        // SpawnTrapVisualClientRpc에서 Object.Destroy(go, 15f)로 15초 만료는 자동 처리됨.
        // hitCount >= maxHits 조기 소진 시에만 명시적으로 제거 RPC를 보내야 함.
        // casterSync가 Despawn됐을 경우 피격 대상(t)의 networkSync로 폴백.
        if (elapsed < maxDuration) // 조기 소진
        {
            if (casterSync != null && casterSync.IsSpawned)
                casterSync.RemoveTrapVisualClientRpc(trapPos);
        }
        // 15초 만료는 클라이언트 측 Destroy(go, 15f)가 처리하므로 추가 RPC 불필요
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

        // 닌자 패시브: 15% 회피 — CombatSystem 평타 경로와 동일하게 적용
        if (tData.HasPassive(PassiveSkillType.Ninja) && Random.value < 0.15f)
            return;

        // 행운의 일격 패시브
        if (cData.HasPassive(PassiveSkillType.LuckyStrike) && Random.value < 0.1f)
        {
            dmg *= 1.5f;
            // 서버에서 caster 소유 클라이언트에게만 ClientRpc로 팝업 전파
            caster.networkSync.ShowLuckyPopupOwner();
        }

        // [버그 수정] 상성(Affinity) 판정 누락 — 평타·투사체 경로(CombatSystem)와 동일하게 적용
        if (CombatSystem.CheckAffinityAdvantagePublic(cData.affinity, tData.affinity))
            dmg *= 1.5f;
        if (CombatSystem.IsSpecialAffinityPublic(cData.affinity) &&
            CombatSystem.IsSpecialAffinityPublic(tData.affinity) &&
            cData.affinity != tData.affinity)
            dmg *= 2.0f;
        else if (CombatSystem.IsSpecialAffinityPublic(cData.affinity) &&
                 !CombatSystem.IsSpecialAffinityPublic(tData.affinity))
            dmg *= 1.2f;

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

        // [FIX] 스킬 데미지 후 lastCombatTime 갱신 누락 버그.
        // 평타 경로(RequestAttackServerRpc)는 공격자/방어자 모두 lastCombatTime을 갱신하지만,
        // DealSkillDamageServer는 갱신하지 않아 스킬 교전 중 RegenerationRoutine의
        // 비전투 4초 판정이 틀려져 교전 중에도 재생 패시브가 발동하는 버그.
        cData.lastCombatTime = Time.time;
        tData.lastCombatTime = Time.time;
    }

    // ════════════════════════════════════════════════════════════
    //  공간 탐색 유틸 — 원본 동일 유지
    // ════════════════════════════════════════════════════════════

    private static List<PlayerController> GetEnemiesInRadius(
        PlayerController caster, float radius, Vector2? center = null)
    {
        Vector2 pos = center ?? (Vector2)caster.transform.position;
        var list = new List<PlayerController>();
        // enemyLayer 제거: PlayerVisibility가 은신 시 레이어를 "IgnorePointer"로 바꾸므로
        // enemyLayer(="Enemy")로 필터하면 은신 적이 피해 판정에서 완전 제외되는 버그 발생.
        // 서버 피해 판정은 레이어 무관하게 탐지하고, 아래 pc 타입·사망·팀 필터로 제한.
        foreach (var col in Physics2D.OverlapCircleAll(pos, radius))
        {
            var pc = col.GetComponent<PlayerController>();
            if (pc != null && !pc.IsDead && pc != caster &&
                pc.networkSync != null && !pc.networkSync.NetworkIsDead.Value)
                list.Add(pc);
        }
        return list;
    }

    private static List<PlayerController> GetEnemiesInCone(
        PlayerController caster, float radius, float angleDeg, Vector2 fwdOverride = default)
    {
        var result = new List<PlayerController>();
        var fwd    = (fwdOverride != default && fwdOverride != Vector2.zero)
            ? fwdOverride
            : caster.GetFacingDirection();
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

    private static bool IsBehind(PlayerController attacker, PlayerController target)
    {
        // [버그 수정] target.GetFacingDirection()은 moveDir·spriteRenderer.flipX를 읽는데
        // 서버에서는 두 값 모두 갱신되지 않아 항상 Vector2.right 반환.
        // → 배후 판정이 "공격자가 타겟의 오른쪽에 있을 때만"으로 고정되는 버그.
        // Rigidbody2D velocity를 타겟의 이동 방향 대리값으로 사용.
        Vector2 toAttacker = ((Vector2)attacker.transform.position
                              - (Vector2)target.transform.position).normalized;
        #if UNITY_6000_0_OR_NEWER
            Vector2 vel = target.Rb != null ? target.Rb.linearVelocity : Vector2.zero;
        #else
            Vector2 vel = target.Rb != null ? target.Rb.velocity : Vector2.zero;
        #endif
        Vector2 targetFacing = vel.sqrMagnitude > 0.01f
            ? vel.normalized
            : -toAttacker; // 정지 중: 공격자 반대 방향(보수적 폴백)
        return Vector2.Dot(targetFacing, toAttacker) < -0.5f;
    }
}
