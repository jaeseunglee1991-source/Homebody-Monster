using System.Threading.Tasks;
using Fusion;
using UnityEngine;

/// <summary>
/// [Phase 1 / Slice 1-A~1-F] 실제 데이터를 쓰는 Fusion 네트워크 플레이어.
/// NGO PlayerNetworkSync(1691줄)의 점진적 대체물.
///
/// 권위 모델:
///  • StateAuthority(호스트) : HP/사망/상태이상/전투/스킬 권위 + 시뮬레이션
///  • InputAuthority(소유 클라): 입력(이동) + 평타/스킬 타겟
/// 조작: WASD 이동 / 좌클릭 평타 / 1 돌진베기 / 2 화염구 / 3 보호막 / 4 충격파.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class NetPlayer : NetworkBehaviour
{
    [Networked] public float              Hp       { get; set; }
    [Networked] public float              MaxHp    { get; set; }
    [Networked] public NetworkBool        IsDead   { get; set; }
    [Networked] public NetworkString<_32> Nickname { get; set; }
    [Networked] public int                Job      { get; set; } // (int)JobType, -1=미설정
    [Networked] public int                KillCount { get; set; }

    // ── [3-D] 부활권 상태 (기존 OfferRevive/RequestRevive/ReportReviveTicketResult 흐름 포팅) ──
    [Networked] public NetworkBool ReviveOffered    { get; set; } // 소유 클라 UI 표시 플래그
    [Networked] public NetworkBool ReviveProcessing { get; set; } // Supabase 차감 진행 중
    [Networked] public TickTimer   ReviveDeadline   { get; set; } // 응답 제한(6초)
    [Networked] private NetworkBool ReviveUsed      { get; set; } // 이 매치 부활 기회 소모됨

    /// <summary>부활 결정 대기 중 — NetMatch가 이 동안 매치 종료를 보류한다.</summary>
    public bool  RevivePending  => ReviveOffered || ReviveProcessing;
    public float ReviveRemaining => ReviveDeadline.RemainingTime(Runner) ?? 0f;

    private NetMatch _match; // 호스트 캐시

    [Tooltip("평타 사거리(유닛)")]
    public float attackRange = 3f;
    [Tooltip("발사할 투사체 프리팹 (NetworkObject + NetProjectile)")]
    public NetworkObject ProjectilePrefab;
    [Tooltip("설치할 덫 프리팹 (NetworkObject + NetTrap). 미배선 시 Trap은 즉발 지점AoE로 폴백.")]
    public NetworkObject TrapPrefab;

    [Networked] private TickTimer AttackCooldown { get; set; }
    [Networked] private TickTimer Skill1CD       { get; set; }
    [Networked] private TickTimer Skill2CD       { get; set; }
    [Networked] private TickTimer Skill3CD       { get; set; }
    [Networked] private TickTimer Skill4CD       { get; set; }
    [Networked] private Vector2   KnockbackVel   { get; set; }
    [Networked] private TickTimer KnockbackTimer { get; set; }

    // StateAuthority 권위 데이터 (네트워크 무관 게임 로직 재사용).
    private CharacterData  _data;
    private float          _moveSpeed = 5f;
    private SpriteRenderer _sr;
    private ChangeDetector _changes;
    private Camera         _cam;
    private NetStatus      _status;

    /// <summary>StateAuthority 권위 캐릭터 데이터(NetStatus/NetProjectile/스킬에서 사용).</summary>
    public CharacterData Data   => _data;
    /// <summary>상태이상 컴포넌트(ICombatStatus) — CombatSystem FX 인자용.</summary>
    public NetStatus     Status => _status;

    private void Awake()
    {
        _sr     = GetComponentInChildren<SpriteRenderer>();
        _status = GetComponent<NetStatus>();
    }

    public override void Spawned()
    {
        _changes = GetChangeDetector(ChangeDetector.Source.SnapshotFrom);
        ApplyVisual();

        // [3-A] 캐릭터 데이터는 소유 클라이언트가 제출한다 (기존 SubmitCharacterDataServerRpc 흐름 포팅).
        // 실제 게임: GameManager.myCharacterData(계정 캐릭터) / PoC 단독: 랜덤 폴백.
        // 호스트 자신의 플레이어도 같은 RPC 경로를 타므로(셀프 RPC=즉시 실행) 분기 불필요.
        if (HasInputAuthority)
        {
            var src = GameManager.Instance != null ? GameManager.Instance.myCharacterData : null;
            var cd  = src ?? StatCalculator.GenerateRandomCharacter($"P{Object.InputAuthority.PlayerId}");
            CaptureLocalSkills(cd); // [4-C] 버튼 라벨/슬롯 수

            string nick = GameManager.Instance != null ? GameManager.Instance.currentPlayerNickname : null;
            if (string.IsNullOrEmpty(nick)) nick = $"P{Object.InputAuthority.PlayerId}";

            Debug.Log($"[NetPlayer] 캐릭터 제출: nick={nick}, job={(JobType)(int)cd.job}, hp={cd.maxHp:0}");
            SubmitCharacterRpc(NetCharData.From(cd), nick);
        }
    }

    // 캐릭터 제출: 소유 클라 → StateAuthority(호스트)가 검증 후 적용.
    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    private void SubmitCharacterRpc(NetCharData data, NetworkString<_32> nickname)
    {
        // [3-E] 재제출은 리롤 윈도우(매치 시작 후 15초) 내에서만 허용 — 서버측 검증.
        // 윈도우는 게임 시작 전이므로 만피 리셋이 정당하다 (기존 NEW-02의 !gameActive 경로 대응).
        if (_data != null && !IsRerollWindowOpen())
        {
            Debug.LogWarning($"[NetPlayer] 리롤 윈도우 밖 재제출 거부 (client={Object.InputAuthority.PlayerId})");
            return;
        }

        // 서버측 범위 검증 — 위변조 클라이언트 차단 (기존 안티치트 포팅).
        if (!NetCharData.IsValid(data))
        {
            Debug.LogError($"[NetPlayer] 유효하지 않은 캐릭터 데이터 거부 (client={Object.InputAuthority.PlayerId}, " +
                           $"hp={data.MaxHp:0.#}, atk={data.BaseAtk:0.#}) → 연결 종료");
            if (Object.InputAuthority != Runner.LocalPlayer)
                Runner.Disconnect(Object.InputAuthority);
            return;
        }

        _data            = data.ToCharacterData();
        _data.playerName = nickname.ToString();
        _data.currentHp  = _data.maxHp;

        MaxHp      = _data.maxHp;
        Hp         = _data.maxHp;
        Job        = (int)_data.job;
        Nickname   = nickname;
        _moveSpeed = _data.moveSpeed;

        Debug.Log($"[NetPlayer] 캐릭터 수신 적용: client={Object.InputAuthority.PlayerId}, " +
                  $"nick={_data.playerName}, job={_data.job}, hp={_data.maxHp:0}, atk={_data.baseAtk:0.#}");
    }

    // 이동: 입력→StateAuthority 시뮬→NetworkTransform 동기화(InputAuthority는 예측). 상태이상/넉백 반영.
    public override void FixedUpdateNetwork()
    {
        // [3-D] 부활 타임아웃 (호스트) — ReviveDeadline은 제안(6초)→수락 후 처리(10초)로 재사용.
        if (HasStateAuthority && ReviveDeadline.Expired(Runner))
        {
            if (ReviveOffered) ReviveOffered = false; // 제안 무응답 → 사망 확정
            else if (ReviveProcessing)                // [Fix] 처리 무응답(차감 보고 누락) → 사망 확정 (무한 대기 방지)
            {
                ReviveProcessing = false;
                ReviveUsed       = true;
                Debug.LogWarning("[NetPlayer] 부활 처리 타임아웃 → 사망 확정");
            }
        }

        if (IsDead) return;

        // 넉백 (서버 권위, 입력 무시) — [Networked]라 InputAuthority도 동일 적용(예측).
        if (!KnockbackTimer.ExpiredOrNotRunning(Runner))
        {
            transform.position = NetArena.Clamp(
                transform.position + (Vector3)(KnockbackVel * Runner.DeltaTime));
            return;
        }

        if (IsRerollWindowOpen()) return;                        // [준비/리롤 시간] 이동도 잠금
        if (_status != null && _status.IsMovementLocked) return; // 스턴/루트
        if (!GetInput(out NetInputData input)) return;

        Vector2 dir = input.Direction;
        if (dir.sqrMagnitude > 0.0001f)
        {
            float mult = _status != null ? _status.MoveSpeedMultiplier : 1f; // 슬로우
            transform.position = NetArena.Clamp(
                transform.position + (Vector3)(dir.normalized * _moveSpeed * mult * Runner.DeltaTime));
            _lastAimDir = dir.normalized; // [4-B] 모바일 스킬 조준 폴백 (마지막 이동 방향)
        }
    }

    // 입력권한 클라: 좌클릭=평타, 1~4=스킬. (리롤 윈도우 중엔 전투 입력 잠금)
    private void Update()
    {
        if (!HasInputAuthority || IsDead) return;

        // [3-E] 새 리롤 윈도우 시작 감지 → 매치당 리롤 1회 카운터 리셋.
        bool windowOpen = IsRerollWindowOpen();
        if (windowOpen && !_rerollWindowSeen) { _rerollWindowSeen = true; _rerollUsedLocal = false; }
        else if (!windowOpen)                 { _rerollWindowSeen = false; }
        if (windowOpen) return; // 준비 시간 — 전투 입력 차단 (이동은 FixedUpdateNetwork에서 계속 허용)

        // [4-B] 평타(포인터 탭)는 NetMobileInput이 터치/마우스 통합 처리.
        // 여기서는 에디터 편의용 키보드 1~4 스킬만 유지 (커서 방향 조준).
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        var mouse = UnityEngine.InputSystem.Mouse.current;
        Vector2 dir = mouse != null
            ? (Vector2)_cam.ScreenToWorldPoint(mouse.position.ReadValue()) - (Vector2)transform.position
            : _lastAimDir;

        if      (kb.digit1Key.wasPressedThisFrame) UseSkillWithAim(1, dir);
        else if (kb.digit2Key.wasPressedThisFrame) UseSkillWithAim(2, dir);
        else if (kb.digit3Key.wasPressedThisFrame) UseSkillWithAim(3, dir);
        else if (kb.digit4Key.wasPressedThisFrame) UseSkillWithAim(4, dir);
    }

    // ════════════════════════════════════════════════════════════
    //  [4-B] 모바일 입력 진입점 (NetMobileInput에서 호출)
    // ════════════════════════════════════════════════════════════

    private Vector2 _lastAimDir = Vector2.right;

    /// <summary>전투 입력 잠금 상태(준비/리롤 시간) — 입력 레이어 공용.</summary>
    public bool CombatLocked => IsRerollWindowOpen();

    /// <summary>화면 좌표 탭 → 적 타겟이면 평타 요청. 타겟이 있었는지 반환(조이스틱 판정용).</summary>
    public bool TryAttackAt(Vector2 screenPos)
    {
        if (!HasInputAuthority || IsDead || CombatLocked) return false;
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return false;

        Vector2 world  = _cam.ScreenToWorldPoint(screenPos);
        var      hit    = Physics2D.OverlapPoint(world);
        var      target = hit != null ? hit.GetComponent<NetPlayer>() : null;
        if (target == null || target == this || target.IsDead) return false;
        if (target.Status != null && target.Status.IsStealthy) return false; // [4-E] 은신 적 타겟 불가

        RequestAttackRpc(target);
        return true;
    }

    // ── [4-C] 클라 측 스킬 정보 (버튼 라벨/슬롯 수 — 내 캐릭터만) ──
    private System.Collections.Generic.List<ActiveSkillType> _localSkills;

    private void CaptureLocalSkills(CharacterData cd)
    {
        _localSkills = cd != null && cd.activeSkills != null
            ? new System.Collections.Generic.List<ActiveSkillType>(cd.activeSkills)
            : new System.Collections.Generic.List<ActiveSkillType>();
        _localSkillVersion++; // 리롤로 스킬셋이 바뀌면 HUD가 버튼을 다시 배선하도록 신호
    }

    // [Fix] 리롤 후 캔버스 스킬 버튼이 stale 라벨을 유지하는 문제 — 버전 변화로 재배선 트리거.
    private int _localSkillVersion;
    /// <summary>로컬 스킬셋 버전 — 리롤마다 증가. HUD 버튼 재배선 판단용.</summary>
    public int LocalSkillVersion => _localSkillVersion;

    /// <summary>내가 굴린 액티브 스킬 수(1~4). 원격 플레이어는 0.</summary>
    public int LocalSkillCount => _localSkills?.Count ?? 0;

    /// <summary>슬롯(1~4)의 스킬 한국어 이름. 빈 슬롯이면 "".</summary>
    public string LocalSkillLabel(int slot)
        => _localSkills != null && slot >= 1 && slot <= _localSkills.Count
            ? CharacterRerollSystem.GetSkillDisplayName(_localSkills[slot - 1])
            : "";

    /// <summary>슬롯(1~4) 스킬의 최대 쿨다운(초) — HUD 쿨다운 fill 비율 계산용. 빈 슬롯이면 0.</summary>
    public float LocalSkillCooldownMax(int slot)
        => _localSkills != null && slot >= 1 && slot <= _localSkills.Count
            ? SkillSystem.GetCooldown(_localSkills[slot - 1])
            : 0f;

    /// <summary>슬롯(1~4) 스킬 사용 — 명시적 조준 방향.</summary>
    public void UseSkillWithAim(int slot, Vector2 aim)
    {
        if (!HasInputAuthority || IsDead || CombatLocked) return;
        if (slot < 1 || slot > LocalSkillCount) return; // 굴리지 못한 슬롯
        UseSkillRpc(slot - 1, aim);
    }

    /// <summary>스킬 버튼 → 조준 방향은 조이스틱(활성 시) 또는 마지막 이동 방향.</summary>
    public void UseSkillAimed(int slot)
    {
        Vector2 aim = NetMobileInput.JoystickDir.sqrMagnitude > 0.01f
            ? NetMobileInput.JoystickDir
            : _lastAimDir;
        UseSkillWithAim(slot, aim);
    }

    // ── 평타 ────────────────────────────────────────────────────
    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    private void RequestAttackRpc(NetPlayer target)
    {
        if (IsDead || target == null || target.IsDead) return;
        if (_data == null || target._data == null) return;
        if (IsRerollWindowOpen()) return; // [3-E] 준비 시간 — 전투 잠금 (서버 검증)
        if (_status != null && _status.IsActionLocked) return; // [4-G] 얼음방패 침묵
        if (!AttackCooldown.ExpiredOrNotRunning(Runner)) return;
        if (Vector2.Distance(transform.position, target.transform.position) > attackRange) return;

        AttackCooldown = TickTimer.CreateFromSeconds(Runner, _data.attackCooldown);

        var result = CombatSystem.CalculateDamage(_data, target._data, _status, target._status);
        if (!result.isEvaded && !result.isDivineGraceBlocked && result.finalDamage > 0f)
        {
            target.ReceiveDamage(result.finalDamage, this);
            CombatSystem.PostDamageEffects(_data, target._data, _status, target._status, result.finalDamage);
            Hp = Mathf.Clamp(_data.currentHp, 0f, MaxHp); // 흡혈 등 공격자 HP 반영
        }

        // [4-E] 평타 = 은신 해제 (첫타 1.5배는 CalculateDamage에서 이미 소비됨).
        if (_status != null && _status.IsStealthy) _status.BreakStealth();
    }

    // ── [4-C] 스킬 — 굴린 캐릭터의 실제 activeSkills 슬롯 실행 ──
    // 슬롯 → _data.activeSkills[slot], 쿨다운 = 기존 SkillSystem.GetCooldown(순수 함수 재사용),
    // 실행 = NetSkillSystem 디스패치(40종 파라미터 테이블).
    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    private void UseSkillRpc(int slot, Vector2 aim)
    {
        if (IsDead || _data == null || IsRerollWindowOpen()) return;
        if (_status != null && _status.IsActionLocked) return; // [4-G] 얼음방패 침묵
        if (_data.activeSkills == null || slot < 0 || slot >= _data.activeSkills.Count) return;
        if (!GetSlotCD(slot).ExpiredOrNotRunning(Runner)) return;

        ActiveSkillType skill = _data.activeSkills[slot];
        SetSlotCD(slot, TickTimer.CreateFromSeconds(Runner, SkillSystem.GetCooldown(skill)));
        NetSkillSystem.Execute(this, skill, aim);
    }

    private TickTimer GetSlotCD(int slot) => slot switch
    {
        0 => Skill1CD, 1 => Skill2CD, 2 => Skill3CD, _ => Skill4CD,
    };

    private void SetSlotCD(int slot, TickTimer t)
    {
        switch (slot)
        {
            case 0: Skill1CD = t; break;
            case 1: Skill2CD = t; break;
            case 2: Skill3CD = t; break;
            default: Skill4CD = t; break;
        }
    }

    // ── [4-C] NetSkillSystem용 서버 헬퍼 ────────────────────────

    /// <summary>StateAuthority 전용 — 스킬 데미지 1회(공식 재사용). 실제 피해 적용 여부 반환.</summary>
    public bool DealSkillDamage(NetPlayer target, float dmg)
    {
        if (!HasStateAuthority || target == null || target.IsDead) return false;
        if (_data == null || target._data == null) return false;

        var result = CombatSystem.CalculateDamageWithOverride(_data, target._data, dmg, _status, target._status);
        if (result.isEvaded || result.isDivineGraceBlocked || result.finalDamage <= 0f) return false;
        target.ReceiveDamage(result.finalDamage, this);
        return true;
    }

    /// <summary>StateAuthority 전용 — 돌진(아레나 클램프 포함).</summary>
    public void DashMove(Vector2 dir, float dist)
    {
        if (!HasStateAuthority) return;
        Vector2 d = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right;
        transform.position = NetArena.Clamp(transform.position + (Vector3)(d * dist));
    }

    /// <summary>StateAuthority 전용 — 자기 회복.</summary>
    public void HealSelf(float amount)
    {
        if (!HasStateAuthority || IsDead || amount <= 0f) return;
        Hp = Mathf.Min(MaxHp, Hp + amount);
        if (_data != null) _data.currentHp = Hp;
    }

    /// <summary>StateAuthority 전용 — 자기 피해(스킬 비용). canKill=false면 1 HP 미만으로 내려가지 않음(자살 방지).</summary>
    public void SelfDamage(float amount, bool canKill = false)
    {
        if (!HasStateAuthority || IsDead || amount <= 0f) return;
        Hp = Mathf.Max(canKill ? 0f : 1f, Hp - amount);
        if (_data != null) _data.currentHp = Hp;
        ShowDamageRpc(transform.position, amount);
        if (canKill && Hp <= 0f) ReceiveDamage(0f); // 사망 처리 트리거(자해=킬크레딧 없음)
    }

    /// <summary>StateAuthority 전용 — 투사체 발사(적중 상태이상 옵션, pierce=관통).</summary>
    public void FireProjectile(Vector2 dir, float dmg, NetSkillSystem.Fx fx, float fxDur, float fxVal, bool pierce = false)
    {
        if (!HasStateAuthority || ProjectilePrefab == null) return;
        Vector2 d        = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right;
        Vector3 spawnPos = transform.position + (Vector3)(d * 0.7f); // 자기 충돌 방지 오프셋

        Runner.Spawn(ProjectilePrefab, spawnPos, Quaternion.identity, Object.InputAuthority,
            (r, o) => o.GetComponent<NetProjectile>().Setup(this, d, dmg, fx, fxDur, fxVal, pierce));
    }

    /// <summary>StateAuthority 전용 — 지정 위치에 덫 설치. TrapPrefab 미배선 시 false(폴백용).</summary>
    public bool SpawnTrap(Vector3 pos, float dmg, float radius, float slowDur, float slowVal)
    {
        if (!HasStateAuthority || TrapPrefab == null) return false;
        Runner.Spawn(TrapPrefab, pos, Quaternion.identity, Object.InputAuthority,
            (r, o) => o.GetComponent<NetTrap>().Setup(this, dmg, radius, slowDur, slowVal));
        return true;
    }

    /// <summary>StateAuthority 전용 넉백 — 짧은 시간 입력을 무시하고 밀려난다.</summary>
    public void ApplyKnockback(Vector2 dir, float speed, float dur)
    {
        if (!HasStateAuthority) return;
        KnockbackVel   = (dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right) * speed;
        KnockbackTimer = TickTimer.CreateFromSeconds(Runner, dur);
    }

    /// <summary>StateAuthority 전용 — 부활/재배치(매치 Restart용).</summary>
    public void ReviveAt(Vector3 pos, bool resetKills = true)
    {
        if (!HasStateAuthority) return;
        IsDead = false;
        Hp     = MaxHp;
        if (resetKills) // 새 매치(Restart) — 킬·부활 기회 리셋 / 부활권 부활은 킬 유지
        {
            KillCount  = 0;
            ReviveUsed = false;
        }
        ReviveOffered    = false;
        ReviveProcessing = false;
        if (_data != null) _data.currentHp = _data.maxHp;
        transform.position = pos;
    }

    /// <summary>슬롯 쿨다운 잔여 초 (0=평타, 1~4=스킬). 0이면 사용 가능. HUD 표시용.</summary>
    public float CooldownRemaining(int slot)
    {
        TickTimer t = slot switch
        {
            1 => Skill1CD,
            2 => Skill2CD,
            3 => Skill3CD,
            4 => Skill4CD,
            _ => AttackCooldown,
        };
        return t.RemainingTime(Runner) ?? 0f;
    }

    /// <summary>
    /// StateAuthority 컨텍스트에서 호출되는 데미지 수신(평타/스킬/투사체/DoT 공용).
    /// attacker가 본인/null이 아니면 사망 시 킬 크레딧 부여 (자해·무주공 DoT는 크레딧 없음 — 기존 [자멸] 분기와 동일).
    /// </summary>
    public void ReceiveDamage(float dmg, NetPlayer attacker = null)
    {
        if (!HasStateAuthority || IsDead) return;
        Hp = Mathf.Max(0f, Hp - dmg);
        if (_data != null) _data.currentHp = Hp;
        if (_status != null) _status.NotifyDeathMarkDamage(dmg); // [4-F] 낙인 피해 누적

        ShowDamageRpc(transform.position, dmg); // [4-D] 데미지 팝업 (전 피어)

        if (Hp <= 0f)
        {
            // [4-G] 수호천사: 치명타 1회 소생(30% HP) → 사망 처리 건너뜀.
            if (_status != null && _status.ConsumeGuardianAngel())
            {
                Hp = MaxHp * 0.3f;
                if (_data != null) _data.currentHp = Hp;
                return;
            }

            IsDead = true;
            if (attacker != null && attacker != this && !attacker.IsDead)
            {
                attacker.KillCount++;
                KillFeedRpc(attacker.Nickname, Nickname); // [4-D] 킬피드
            }
            else
            {
                KillFeedRpc(default, Nickname); // 자해/무주공 DoT = [자멸]
            }
            // [4-F] 낙인 대상 사망 → 폭발(주변 체이닝). 부활 제안 전에 정확한 누적값으로 실행.
            if (_status != null && _status.IsDeathMarked) _status.ExplodeDeathMark(true);
            TryOfferRevive(); // [3-D] 조건 충족 시 소유 클라에 부활 제안
        }
    }

    // [4-D] 연출 브로드캐스트 (StateAuthority → 전 피어, 시각 전용)
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void ShowDamageRpc(Vector2 pos, float amount) => NetFx.AddDamagePopup(pos, amount);

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void KillFeedRpc(NetworkString<_32> killer, NetworkString<_32> victim)
        => NetFx.AddKillFeed(killer.ToString(), victim.ToString());

    // ════════════════════════════════════════════════════════════
    //  [3-D] 부활권 — Supabase 티켓 차감 위임 (기존 PlayerNetworkSync 흐름 포팅)
    //  호스트 → 소유 클라: 제안(ReviveOffered) → 수락 시 차감 위임(DeductTicketRpc)
    //  소유 클라: 인증 세션으로 UseReviveTicket() → 결과 보고(ReportTicketRpc)
    //  호스트: 성공 → 부활(킬 유지) / 실패·포기·타임아웃 → 사망 확정
    // ════════════════════════════════════════════════════════════

    /// <summary>리롤 윈도우(매치 시작 준비 시간) 진행 중인지 — 전투 잠금/재제출 허용 판정.</summary>
    private bool IsRerollWindowOpen()
    {
        if (_match == null) _match = FindFirstObjectByType<NetMatch>();
        return _match != null && _match.RerollOpen;
    }

    // ════════════════════════════════════════════════════════════
    //  [3-E] 리롤 — 피자 차감(클라 인증 위임) 후 캐릭터 재굴림·재제출
    // ════════════════════════════════════════════════════════════

    private bool _rerollUsedLocal;  // 매치당 1회 (MaxRerollsPerMatch)
    private bool _rerollBusy;       // 피자 차감 중 더블클릭 방지
    private bool _rerollWindowSeen; // 새 윈도우 감지용

    /// <summary>HUD 리롤 버튼 표시용 — 이번 매치에 리롤을 이미 썼거나 처리 중인지.</summary>
    public bool RerollUsedLocal => _rerollUsedLocal || _rerollBusy;

    /// <summary>소유 클라 전용 — 피자 차감 후 새 캐릭터를 굴려 재제출한다 (NetHUD 버튼에서 호출).</summary>
    public async void RequestRerollLocal()
    {
        if (!HasInputAuthority || IsDead || _rerollBusy || _rerollUsedLocal) return;
        if (!IsRerollWindowOpen()) return;

        _rerollBusy = true;
        bool paid;
        if (SupabaseManager.Instance != null)
        {
            paid = false;
            try { paid = await SupabaseManager.Instance.SpendPizzaForReroll(); }
            catch (System.Exception e) { Debug.LogError($"[NetPlayer] 리롤 피자 차감 오류: {e.Message}"); }
            if (!paid) Debug.LogWarning("[NetPlayer] 리롤 불가 — 피자 부족 또는 차감 실패");
        }
        else
        {
            paid = true; // PoC 단독 실행 — 경제 없음, 무료 리롤 허용
            Debug.LogWarning("[NetPlayer] PoC 단독 — 피자 차감 생략(무료 리롤)");
        }

        if (Object == null || !Object.IsValid) { _rerollBusy = false; return; } // await 중 Despawn 방어
        if (!paid) { _rerollBusy = false; return; }

        string nick = GameManager.Instance != null ? GameManager.Instance.currentPlayerNickname : null;
        if (string.IsNullOrEmpty(nick)) nick = $"P{Object.InputAuthority.PlayerId}";

        var cd = StatCalculator.GenerateRandomCharacter(nick);
        if (GameManager.Instance != null) GameManager.Instance.myCharacterData = cd;
        CaptureLocalSkills(cd); // [4-C] 리롤 후 버튼 라벨 갱신

        Debug.Log($"[NetPlayer] 🍕 리롤 → 재제출: job={cd.job}, hp={cd.maxHp:0}");
        SubmitCharacterRpc(NetCharData.From(cd), nick);

        _rerollUsedLocal = true;
        _rerollBusy      = false;
    }

    /// <summary>StateAuthority 전용 — 부활 제안 조건 검사 (기존 CanOfferRevive 포팅).</summary>
    private void TryOfferRevive()
    {
        if (!HasStateAuthority || ReviveUsed) return;

        if (_match == null) _match = FindFirstObjectByType<NetMatch>();
        if (_match == null || !_match.HasStarted || _match.Phase != 0) return;

        float limit = GameBalanceConfig.Get()?.ReviveTimeLimit ?? 60f;
        if (_match.Elapsed > limit) return;
        if (_match.ReviveUsedCount >= NetMatch.MaxReviveCount) return;

        // 부활 후 매치가 유의미하려면 본인 외 생존자 1명 이상.
        // (원본 AliveCount<3 조건은 2인 매치에서 부활 자체가 불가능 → 테스트 가능하도록 완화.
        //  cutover 시 GameBalanceConfig로 정책 결정 예정.)
        int othersAlive = 0;
        foreach (var p in FindObjectsByType<NetPlayer>(FindObjectsSortMode.None))
            if (p != this && !p.IsDead) othersAlive++;
        if (othersAlive < 1) return;

        ReviveOffered  = true;
        ReviveDeadline = TickTimer.CreateFromSeconds(Runner, 6f); // UI 5초 + RTT 여유 (원본 동일)
    }

    /// <summary>소유 클라 → 호스트: 부활권 사용 수락.</summary>
    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    public void AcceptReviveRpc()
    {
        if (!IsDead || !ReviveOffered || ReviveProcessing) return;
        if (ReviveDeadline.Expired(Runner)) { ReviveOffered = false; return; }

        ReviveOffered    = false;
        ReviveProcessing = true;
        ReviveDeadline   = TickTimer.CreateFromSeconds(Runner, 10f); // 처리 타임아웃 (차감 보고 누락 대비)
        DeductTicketRpc(Object.InputAuthority); // 인증 세션을 가진 소유 클라에 차감 위임
    }

    /// <summary>소유 클라 → 호스트: 부활 포기 (기회 소모).</summary>
    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    public void GiveUpReviveRpc()
    {
        if (!IsDead || !ReviveOffered || ReviveProcessing) return;
        ReviveOffered = false;
        ReviveUsed    = true;
    }

    /// <summary>
    /// 소유 클라 → 호스트: 매치 항복(자진 사망). 부활 제안 없이 즉시 관전 처리되며,
    /// 매치는 다른 생존자를 위해 계속된다(NetMatch가 IsDead로 순위 집계).
    /// </summary>
    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    public void RequestSurrenderRpc()
    {
        if (IsDead) return;
        if (IsRerollWindowOpen()) return; // 준비 시간에는 항복 불가

        Hp     = 0f;
        IsDead = true;
        ReviveUsed = true; // 항복은 부활 제안 대상 아님
        if (_data != null) _data.currentHp = 0f;
        if (_status != null && _status.IsDeathMarked) _status.ExplodeDeathMark(true);
        KillFeedRpc(default, Nickname); // 공격자 없음 → "[자멸]" 표기
    }

    /// <summary>호스트 → 소유 클라에게만: Supabase 부활권 차감 요청 (위임).</summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void DeductTicketRpc([RpcTarget] PlayerRef player)
    {
        _ = DeductTicketAndReportAsync();
    }

    private async Task DeductTicketAndReportAsync()
    {
        // [Fix] 항상 한 프레임 양보 — DeductTicketRpc 핸들러 내부에서 ReportTicketRpc를 동기 재진입
        // 호출하면(단독 실행=무await 경로) 호스트에 전달되지 않아 ReviveProcessing이 영영 안 풀림.
        await System.Threading.Tasks.Task.Yield();

        bool success = false;
        if (SupabaseManager.Instance != null)
        {
            // DB 함수(use_revive_ticket)가 auth.uid() 기준 원자적 차감 — 위변조는 DB 레벨에서 차단 (원본 동일).
            try { success = await SupabaseManager.Instance.UseReviveTicket(); }
            catch (System.Exception e) { Debug.LogError($"[NetPlayer] 부활권 차감 오류: {e.Message}"); }
        }
        else
        {
            // 인증 불가 = 무료 부활 금지 (원본 Fix #5 동일).
            Debug.LogWarning("[NetPlayer] SupabaseManager 없음 → 부활권 차감 불가, 부활 거부");
        }

        if (Object == null || !Object.IsValid) return; // await 중 Despawn 방어
        ReportTicketRpc(success);
    }

    /// <summary>소유 클라 → 호스트: 차감 결과 보고 → 부활 실행 또는 사망 확정.</summary>
    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    private void ReportTicketRpc(NetworkBool success)
    {
        if (!ReviveProcessing || !IsDead) return;
        ReviveProcessing = false;
        ReviveUsed       = true; // 성공/실패 무관 기회 소모 (원본 동일)

        if (!success)
        {
            Debug.LogWarning($"[NetPlayer] 부활 거부 — 부활권 차감 실패 (client={Object.InputAuthority.PlayerId})");
            return;
        }

        if (_match == null) _match = FindFirstObjectByType<NetMatch>();
        if (_match != null) _match.ReviveUsedCount++;

        ReviveAt(NetSpawnPoints.Spawn(), resetKills: false);
        Debug.Log($"[NetPlayer] ✅ 부활 (client={Object.InputAuthority.PlayerId}, 매치 부활 {_match?.ReviveUsedCount}/{NetMatch.MaxReviveCount})");
    }

    public override void Render()
    {
        foreach (var changed in _changes.DetectChanges(this))
        {
            if (changed == nameof(Hp) || changed == nameof(IsDead) || changed == nameof(Job))
            {
                ApplyVisual();
                break;
            }
        }
    }

    private NetVisual _visual;

    private void ApplyVisual()
    {
        if (_sr == null) return;

        // [2-E] 직업 비주얼(NetVisual)이 적용된 경우 HP 틴트 양보 — 사망 회색만 유지.
        if (_visual == null) _visual = GetComponent<NetVisual>();
        if (_visual != null && _visual.HasJobVisual)
        {
            _sr.color = IsDead ? Color.gray : Color.white;
            return;
        }

        float ratio = MaxHp > 0f ? Hp / MaxHp : 1f;
        Color c = IsDead
            ? Color.gray
            : Color.Lerp(Color.red, new Color(0.3f, 0.8f, 1f), ratio);
        c.a = _sr.color.a; // [4-E] 은신 알파(NetVisual 관리) 보존
        _sr.color = c;
    }
}
