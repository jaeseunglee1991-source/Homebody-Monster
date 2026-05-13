using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// ════════════════════════════════════════════════════════════════
//  NetworkCharacterData
//  ※ NGO 직렬화는 필드 순서 의존. 한 번 출시된 후에는 중간 삽입 금지.
//    AttackCooldown은 MoveSpeed 뒤·Active0 앞에 추가됨 (직업별 평타 쿨다운).
// ════════════════════════════════════════════════════════════════
public struct NetworkCharacterData : INetworkSerializable
{
    public int   Job, Affinity, Grade;
    public float MaxHp, BaseAtk, MoveSpeed, AttackCooldown;
    public int   Active0, Active1, Active2, Active3;
    public int   Passive0, Passive1, Passive2, Passive3;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref Job);            s.SerializeValue(ref Affinity);
        s.SerializeValue(ref Grade);          s.SerializeValue(ref MaxHp);
        s.SerializeValue(ref BaseAtk);        s.SerializeValue(ref MoveSpeed);
        s.SerializeValue(ref AttackCooldown);
        s.SerializeValue(ref Active0);        s.SerializeValue(ref Active1);
        s.SerializeValue(ref Active2);        s.SerializeValue(ref Active3);
        s.SerializeValue(ref Passive0);       s.SerializeValue(ref Passive1);
        s.SerializeValue(ref Passive2);       s.SerializeValue(ref Passive3);
    }

    public CharacterData ToCharacterData()
    {
        var d = new CharacterData
        {
            job = (JobType)Job, affinity = (AffinityType)Affinity, grade = (GradeTier)Grade,
            maxHp = MaxHp, currentHp = MaxHp, baseAtk = BaseAtk, moveSpeed = MoveSpeed,
            attackCooldown = AttackCooldown,
            activeSkills = new List<ActiveSkillType>(), passiveSkills = new List<PassiveSkillType>(),
        };
        int[] actives  = { Active0, Active1, Active2, Active3 };
        int[] passives = { Passive0, Passive1, Passive2, Passive3 };
        foreach (int a in actives)  if (a >= 0) d.activeSkills.Add((ActiveSkillType)a);
        foreach (int p in passives) if (p >= 0) d.passiveSkills.Add((PassiveSkillType)p);
        return d;
    }
}

// ════════════════════════════════════════════════════════════════
//  PlayerNetworkSync — 개선 버전
//
//  [원본 대비 변경 요약]
//  1. WaitUntil 폴링 제거 → async Task + CancellationToken으로 교체
//  2. _hasUsedRevive 조기 설정 버그 수정
//     → Supabase 결과 확정 후에만 true로 설정
//     → 중복 진입 방지는 _isProcessingRevive로 분리
//  3. async void 사용 금지 → async Task 사용 (Unity 크래시 방지)
//  4. Despawn 이후 await 재진입 방지 → CancellationToken 공유
//  5. OnNetworkDespawn에서 CTS 정리 보장
//  6. Supabase 인증 클라이언트 위임 (서버는 auth.uid() 없음)
//  7. Thorns 반사 사망 누락 버그 수정 (2순위 사망 체크)
// ════════════════════════════════════════════════════════════════
[RequireComponent(typeof(PlayerController))]
[RequireComponent(typeof(NetworkObject))]
public class PlayerNetworkSync : NetworkBehaviour
{
    public readonly NetworkVariable<float> NetworkHp = new(
        100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public readonly NetworkVariable<float> NetworkMaxHp = new(
        100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public readonly NetworkVariable<int> NetworkKillCount = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public readonly NetworkVariable<bool> NetworkIsDead = new(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // [Fix #1] Owner 쓰기 권한 제거 → Server 권한으로 변경하여 클라이언트 위변조 차단.
    // 닉네임은 SubmitCharacterDataServerRpc의 파라미터로 직접 전달되므로
    // NetworkVariable 동기화 타이밍 버그도 함께 해결됩니다.
    public readonly NetworkVariable<FixedString64Bytes> NetworkNickname = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // [FIX] 이동 방향 동기화 — 다른 클라이언트 캐릭터 flipX 갱신용.
    // ClientNetworkTransform이 위치를 동기화하지만 moveDir은 동기화하지 않아
    // 다른 플레이어가 왼쪽으로 이동해도 스프라이트가 뒤집히지 않는 버그 수정.
    // [외형 동기화] 직업 인덱스 — JobVisualRegistry 룩업용.
    // -1 = 미설정 (SubmitCharacterDataServerRpc 도달 전).
    // 서버 권한 — 클라이언트 위변조 차단 + 서버 검증 통과한 직업만 브로드캐스트.
    // OnValueChanged 는 Despawn 후엔 발동 안 되므로 PlayerController 가 OnNetworkSpawn 에서
    // 현재 값을 즉시 한번 읽고 + 구독한다 (late join / 재접속 대응).
    public readonly NetworkVariable<int> NetworkJob = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // [머리위 UI] 직업/상성/등급을 모든 클라이언트에 동기화하기 위한 NetworkVariable.
    // 각 캐릭터 위에 직업·상성·등급을 표시하기 위해 사용 (PlayerWorldUI 가 구독).
    public readonly NetworkVariable<int> NetworkAffinity = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public readonly NetworkVariable<int> NetworkGrade = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<Vector2> NetworkMoveDir = new(
        Vector2.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // ── 서버 전용 상태 ──────────────────────────────────────────
    private CharacterData      _serverData;
    // [버그 수정] BanAndKick용 Supabase userId 캐시.
    // SubmitCharacterDataServerRpc에서 클라이언트가 전달한 currentPlayerId를 저장.
    private string             _serverUserId;
    private float              _lastAttackTime = -999f;
    private PlayerNetworkSync  _pendingKiller  = null;
    // [X-F] Thorns 반사 재진입 차단 플래그 (반사 데미지가 또 반사되는 것 방지)
    private bool _thornsReflecting = false;
    // 스킬별 마지막 사용 시각 — 악의적 클라이언트의 쿨다운 무시 RPC 반복 전송 방지
    private readonly Dictionary<ActiveSkillType, float> _skillLastUsed =
        new Dictionary<ActiveSkillType, float>();

    // ── 부활 상태 필드 ─────────────────────────────────────────
    // _hasUsedRevive   : 부활 기회를 최종 소모했는지 (성공/포기/타임아웃 모두 포함)
    //                    true가 된 이후에는 어떤 경로로도 부활 불가
    // _isProcessingRevive : Supabase 통신이 진행 중인지
    //                       중복 ServerRpc 방어용, 통신 중에만 true
    // _reviveCts       : 타임아웃 Task와 Supabase Task가 공유하는 CancellationToken
    //                    둘 중 하나가 먼저 끝나면 나머지를 취소
    private bool _hasUsedRevive      = false;
    private bool _isProcessingRevive = false;
    private CancellationTokenSource _reviveCts = null;
    private Coroutine _regenCoroutine = null;
    // BUG-02: SubmitCharacterDataServerRpc isFirstSpawn 판정용 플래그.
    // NetworkHp.Value == 100f 비교 방식은 maxHp가 우연히 100이 나오는 캐릭터(예: baseHp=40 * 1.5 * 1.666)에서
    // 게임 중 리롤 시 HP가 만피로 강제 리셋되는 버그가 있어 명시적 플래그로 대체.
    private bool _hasSubmittedCharacterData = false;

    public CharacterData ServerData  => _serverData;
    // [버그 수정] ServerValidator.BanAndKickAsync가 정확한 userId를 참조할 수 있도록 공개
    public string        ServerUserId => _serverUserId;

    private PlayerController _controller;

    private void Awake() { _controller = GetComponent<PlayerController>(); }

    // [FEATURE] 서버 측 위치 검증 (ServerValidator 안티치트)
    private void FixedUpdate()
    {
        if (!IsServer || _controller == null || _controller.Rb == null) return;
        if (NetworkIsDead.Value) return;
        ServerValidator.Instance?.RecordAndValidatePosition(this, _controller.Rb.position);
        PingAdaptiveCombat.Instance?.RecordSnapshot(OwnerClientId, _controller.Rb.position);
    }

    // ════════════════════════════════════════════════════════════
    //  NGO 생명주기
    // ════════════════════════════════════════════════════════════

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        NetworkHp.OnValueChanged        += HandleHpChanged;
        NetworkIsDead.OnValueChanged    += HandleDeadChanged;
        NetworkKillCount.OnValueChanged += HandleKillCountChanged;
        // [FIX] NetworkNickname OnValueChanged 미구독 버그.
        // 닉네임이 서버에서 설정되어도 캐릭터 위 닉네임 UI가 갱신되지 않음.
        // 스폰 직후 초기값도 표시되지 않는 문제 포함.
        NetworkNickname.OnValueChanged  += HandleNicknameChanged;

        if (IsOwner)
        {
            // [Fix #1] NetworkNickname.Value를 먼저 쓰고 ServerRpc를 보내면
            // NetworkVariable 동기화가 RPC보다 늦게 도달하여 playerName이 빈 문자열이 됩니다.
            // 닉네임을 RPC 파라미터로 직접 전달하여 타이밍 버그를 해결합니다.
            string nicknameValue = GameManager.Instance?.currentPlayerNickname
                ?? GameManager.Instance?.currentPlayerId
                ?? $"Player_{OwnerClientId}";
            // [버그 수정] Supabase userId를 서버로 전달.
            // 기존: ServerValidator.BanAndKickAsync에서 GameManager.currentPlayerId를 사용했는데
            //       데디케이티드 서버의 GameManager는 자신의 계정 ID(또는 빈값)를 갖고 있어
            //       치트 플레이어의 ID가 아닌 엉뚱한 값이 ban_logs에 기록됨.
            // 수정: 인증된 클라이언트가 자신의 currentPlayerId를 RPC로 전달,
            //       서버는 _serverUserId에 캐시해 BanAndKickAsync에서 정확히 사용.
            string userIdValue = GameManager.Instance?.currentPlayerId ?? "";
            SubmitCharacterDataServerRpc(
                BuildNetworkData(),
                new FixedString64Bytes(nicknameValue),
                new FixedString64Bytes(userIdValue));
        }

        InGameManager.Instance?.RegisterPlayer(_controller);

        // 스폰 시점에 이미 NetworkNickname이 설정되어 있으면 즉시 UI 반영
        // (OnValueChanged는 값이 바뀔 때만 호출되므로 초기값은 수동 적용 필요)
        if (!NetworkNickname.Value.IsEmpty)
            HandleNicknameChanged(default, NetworkNickname.Value);

        // [버그 수정 #C] HUD 초기화 누락 방지 워치독.
        // HandleHpChanged 콜백은 NetworkHp 값이 실제로 변할 때만 발화하므로
        // (myData 도달 전 NetworkHp 동기화 → 이후 HP 변동 없음) 시 HUD 영구 미초기화.
        // 매 프레임 폴링하여 myData/HUD 준비되면 1회 초기화 후 종료.
        if (IsOwner) StartCoroutine(EnsureHudInitializedRoutine());
    }

    private System.Collections.IEnumerator EnsureHudInitializedRoutine()
    {
        float deadline = Time.time + 10f;
        while (Time.time < deadline)
        {
            if (_hudInitialized) yield break;
            if (InGameHUD.Instance != null
                && _controller != null
                && _controller.myData != null
                && _controller.myData.activeSkills != null
                && _controller.myData.activeSkills.Count > 0)
            {
                InGameHUD.Instance.InitPlayerUI(_controller);
                InGameHUD.Instance.UpdateHealthBar(NetworkHp.Value, NetworkMaxHp.Value);
                _hudInitialized = true;
                yield break;
            }
            yield return null;
        }
    }

    public override void OnNetworkDespawn()
    {
        NetworkHp.OnValueChanged        -= HandleHpChanged;
        NetworkIsDead.OnValueChanged    -= HandleDeadChanged;
        NetworkKillCount.OnValueChanged -= HandleKillCountChanged;
        NetworkNickname.OnValueChanged  -= HandleNicknameChanged;

        // Despawn 시 진행 중인 타이머/Supabase Task 모두 취소
        CancelAndDisposeCts();

        // [FEATURE] 서버 측 안티치트 기록 정리
        if (IsServer)
        {
            ServerValidator.Instance?.RemovePlayer(OwnerClientId);
            PingAdaptiveCombat.Instance?.RemovePlayer(OwnerClientId); // Fix-13
        }

        base.OnNetworkDespawn();
    }

    // ════════════════════════════════════════════════════════════
    //  NetworkVariable 콜백
    // ════════════════════════════════════════════════════════════

    private bool _hudInitialized = false;

    private void HandleHpChanged(float prev, float curr)
    {
        if (_controller.myData != null) _controller.myData.currentHp = curr;
        if (!IsOwner) return;
        if (!_hudInitialized && InGameHUD.Instance != null && _controller.myData != null
            && _controller.myData.activeSkills != null && _controller.myData.activeSkills.Count > 0)
        {
            // [버그 수정] activeSkills가 비어있는 시점에 InitPlayerUI 호출 시 스킬 버튼 미초기화.
            // myData가 채워졌더라도 activeSkills.Count > 0 검증 후 호출.
            InGameHUD.Instance.InitPlayerUI(_controller);
            _hudInitialized = true;
        }
        InGameHUD.Instance?.UpdateHealthBar(curr, NetworkMaxHp.Value);
    }

    private void HandleDeadChanged(bool prev, bool curr) { }

    private void HandleKillCountChanged(int prev, int curr) { _controller.SetKillCount(curr); }

    // BUG-11: TMP 텍스트 캐시 — 매번 GetComponentsInChildren을 호출하지 않도록 1회만 탐색.
    private TMPro.TextMeshPro _nicknameTextCache;

    private void HandleNicknameChanged(FixedString64Bytes prev, FixedString64Bytes curr)
    {
        if (_controller == null) return;
        string nickname = curr.ToString();
        if (_controller.myData != null)
            _controller.myData.playerName = nickname;

        if (_nicknameTextCache == null)
        {
            // 1회 탐색 후 캐싱
            foreach (var t in _controller.GetComponentsInChildren<TMPro.TextMeshPro>(true))
            {
                if (t.gameObject.name == "NicknameText")
                {
                    _nicknameTextCache = t;
                    break;
                }
            }
        }
        if (_nicknameTextCache != null)
            _nicknameTextCache.text = nickname;
    }

    // ════════════════════════════════════════════════════════════
    //  ServerRpc
    // ════════════════════════════════════════════════════════════

    [ServerRpc]
    private void SubmitCharacterDataServerRpc(NetworkCharacterData netData, FixedString64Bytes nickname, FixedString64Bytes userId = default)
    {
        // StatCalculator 이론 최댓값 기준 범위 검증 (개조 클라이언트 maxHp=9999 등 차단)
        // HP 최댓값: 50 * (1+9*0.111) * 1.4 ≈ 140, ATK 최댓값: 5.0 * 2.0 * 1.5 ≈ 15
        if (!IsValidCharacterData(netData))
        {
            Debug.LogWarning($"[Server] 클라이언트 {OwnerClientId}: 유효하지 않은 CharacterData 거부 " +
                             $"(HP={netData.MaxHp:0.#}, ATK={netData.BaseAtk:0.#})");
            // [FIX] return만 하면 _serverData=null, NetworkHp=0인 채로 alivePlayers에 등록되어
            // 게임 종료 판정을 영구적으로 막는 버그 발생. 비정상 클라이언트를 즉시 킥.
            Debug.LogError($"[Server] 클라이언트 {OwnerClientId} 비정상 데이터 → 연결 강제 종료.");
            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.DisconnectClient(OwnerClientId);
            return;
        }
        _serverData = netData.ToCharacterData();
        // H-5: 리롤로 같은 ActiveSkillType이 재배정됐을 때 이전 매치/세션의 쿨다운 타임스탬프가
        // _skillLastUsed에 잔존하여 새 캐릭터의 동일 스킬을 즉시 사용 못 하던 버그.
        _skillLastUsed.Clear();
        // [Fix #1] 닉네임을 RPC 파라미터로 직접 수신.
        // NetworkVariable 동기화보다 RPC가 먼저 도달하는 타이밍 버그를 해결하며,
        // 서버에서 NetworkNickname을 설정하므로 클라이언트 위변조도 차단합니다.
        _serverData.playerName = nickname.ToString();
        // [버그 수정] 클라이언트가 전달한 Supabase userId를 서버 캐시에 저장.
        // BanAndKickAsync에서 GameManager.currentPlayerId(서버 자신의 ID) 대신 이 값을 사용.
        if (!userId.IsEmpty) _serverUserId = userId.ToString();
        // StatusEffectSystem(myData.shieldHp 수정)과 CombatSystem(_serverData.shieldHp 읽기)이
        // 같은 객체를 참조하도록 단일화 → IronSkin 실드, deathMark, tenacity 등 런타임 필드 일관성 보장
        _controller.SetMyData(_serverData);
        NetworkNickname.Value  = nickname;
        // NEW-02: 리롤 재제출 시 전투 중인 플레이어의 HP가 만피로 강제 리셋되는 버그 수정.
        // 최초 스폰 또는 게임 시작 전에는 만피 초기화,
        // 게임 진행 중 리롤은 기존 HP 비율을 유지하면서 MaxHp만 갱신.
        // BUG-02: NetworkHp == 100f 비교는 캐릭터 maxHp가 우연히 100인 경우 false-positive로
        // 진행 중인 플레이어의 HP를 만피로 리셋하는 버그가 있어 명시적 플래그로 변경.
        bool isFirstSpawn = !_hasSubmittedCharacterData;
        _hasSubmittedCharacterData = true;
        bool gameActive   = InGameManager.Instance != null && InGameManager.Instance.isGameActive;
        if (isFirstSpawn || !gameActive)
        {
            NetworkHp.Value    = _serverData.maxHp;
            NetworkMaxHp.Value = _serverData.maxHp;
        }
        else
        {
            float hpRatio = NetworkMaxHp.Value > 0f ? NetworkHp.Value / NetworkMaxHp.Value : 1f;
            NetworkMaxHp.Value = _serverData.maxHp;
            NetworkHp.Value    = Mathf.Round(_serverData.maxHp * hpRatio * 10f) / 10f;
        }
        // [외형 동기화] 검증된 직업을 모든 클라이언트에 브로드캐스트.
        // PlayerController.OnJobValueChanged 가 수신하여 UpdateVisualByJob 호출.
        NetworkJob.Value       = (int)_serverData.job;
        // [머리위 UI] 상성·등급도 함께 브로드캐스트하여 PlayerWorldUI가 모든 클라이언트에서 표시.
        NetworkAffinity.Value  = (int)_serverData.affinity;
        NetworkGrade.Value     = (int)_serverData.grade;

        // [FIX] Regeneration 패시브 NetworkHp 미갱신 버그 수정.
        // CombatSystem.RegenerationRoutine은 data.currentHp만 수정하고 NetworkHp.Value를 갱신하지 않아
        // 다른 클라이언트에 HP 회복이 전파되지 않고 서버 전투 판정과도 불일치 발생.
        // HealServer()를 사용하는 전용 코루틴으로 대체하여 NetworkHp.Value 동기화 보장.
        if (_regenCoroutine != null) StopCoroutine(_regenCoroutine);
        _regenCoroutine = StartCoroutine(RegenerationNetworkRoutine());
    }

    private System.Collections.IEnumerator RegenerationNetworkRoutine()
    {
        // BUG-23: 기존엔 interval/cooldown/rate/min을 모두 하드코딩하여 GameBalanceConfig 변경이
        // 실제 네트워크 게임 재생 패시브에 반영되지 않음 (출시 후 밸런스 패치 불가).
        // Config가 없으면 기존 기본값 유지.
        while (true)
        {
            var cfg = GameBalanceConfig.Get();
            float interval  = cfg != null ? cfg.RegenerationTickInterval : 2f;
            yield return new WaitForSeconds(interval);

            // [버그 수정] yield break → continue 로 변경.
            // 기존: 사망 시 yield break → 코루틴 영구 종료.
            //        부활(ReportReviveTicketResultServerRpc)로 NetworkIsDead.Value가
            //        false로 복원되어도 코루틴이 이미 끝났으므로 재생 패시브 영구 비활성화.
            // 수정: 사망 중에는 tick을 건너뛰고(continue), 부활하면 자동으로 재개.
            //        OnNetworkDespawn 시 코루틴은 Unity가 자동 정리하므로 무한루프 문제 없음.
            if (NetworkIsDead.Value) continue;
            if (_serverData == null || !_serverData.HasPassive(PassiveSkillType.Regeneration)) continue;

            float cooldown  = cfg != null ? cfg.RegenerationCooldown : 4f;
            float regenRate = cfg != null ? cfg.RegenerationHpRate   : 0.05f;
            float regenMin  = cfg != null ? cfg.RegenerationMin      : 1.5f;
            if (Time.time - _serverData.lastCombatTime >= cooldown && NetworkHp.Value < NetworkMaxHp.Value)
            {
                float amount = Mathf.Max(regenMin, NetworkMaxHp.Value * regenRate);
                // HealServer: NetworkHp.Value + _serverData.currentHp 동시 갱신 → 클라이언트 전파
                _controller.HealServer(amount);
            }
        }
    }

    private static bool IsValidCharacterData(NetworkCharacterData d)
    {
        if (d.Job   < 0 || d.Job   > 9)   return false;
        if (d.Grade < 0 || d.Grade > 9)   return false;

        // [FIX] Affinity 범위 검증 누락 버그.
        // Job/Grade는 0~9 검증하지만 Affinity(AffinityType: Spicy=0 ~ Pineapple=6, 총 7개)는
        // 검증하지 않았음. 조작된 클라이언트가 Affinity=999 등 범위 밖 값을 전송하면
        // CombatSystem.CheckAffinityAdvantage / IsSpecialAffinity에서 정의되지 않은 enum 값으로
        // 상성 판정이 오동작하고, MintChoco/Pineapple 3배 데미지 상성을 회피하는 치트가 가능.
        // Enum.GetValues().Length로 계산하여 enum 추가 시 자동 반영.
        int affinityMax = System.Enum.GetValues(typeof(AffinityType)).Length - 1;
        if (d.Affinity < 0 || d.Affinity > affinityMax) return false;

        // [FIX] 스킬 슬롯 범위 검증 추가.
        // 범위 밖 ActiveSkillType/PassiveSkillType 값은 SkillSystem.switch default로 빠지지만,
        // 명시적 검증으로 조작된 패킷을 조기 차단. -1은 빈 슬롯(유효).
        int activeMax  = System.Enum.GetValues(typeof(ActiveSkillType)).Length  - 1;
        int passiveMax = System.Enum.GetValues(typeof(PassiveSkillType)).Length - 1;
        int[] actives  = { d.Active0,  d.Active1,  d.Active2,  d.Active3  };
        int[] passives = { d.Passive0, d.Passive1, d.Passive2, d.Passive3 };
        foreach (int a in actives)  if (a != -1 && (a < 0 || a > activeMax))  return false;
        foreach (int p in passives) if (p != -1 && (p < 0 || p > passiveMax)) return false;

        if (d.MaxHp    < 5f   || d.MaxHp    > 160f) return false;
        if (d.BaseAtk  < 0.5f || d.BaseAtk  > 20f)  return false;
        if (d.MoveSpeed < 1f  || d.MoveSpeed > 6f)   return false;
        // 직업별 최저 0.65f(Tanker) ~ 최고 1.20f(Mage) 범위. 여유로 0.5~1.5f 허용.
        if (d.AttackCooldown < 0.5f || d.AttackCooldown > 1.5f) return false;
        return true;
    }

    [ServerRpc]
    public void RequestAttackServerRpc(ulong targetNetworkObjectId)
    {
        if (NetworkIsDead.Value || _serverData == null) return;
        // 클라/서버 일치를 위해 서버가 검증·캐시한 _serverData.attackCooldown 사용.
        if (Time.time - _lastAttackTime < _serverData.attackCooldown) return;

        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                targetNetworkObjectId, out var targetNetObj)) return;

        var targetSync = targetNetObj.GetComponent<PlayerNetworkSync>();
        if (targetSync == null || targetSync.NetworkIsDead.Value || targetSync._serverData == null) return;

        float dist = Vector2.Distance(transform.position, targetNetObj.transform.position);
        if (dist > _controller.attackRange * 1.5f) return;

        _lastAttackTime = Time.time;
        _serverData.lastCombatTime = Time.time;

        var attackerFx = _controller.StatusFX;
        var targetFx   = targetSync._controller.StatusFX;

        if (attackerFx != null && attackerFx.IsStealthy)
            attackerFx.RemoveEffect(StatusEffectType.Stealth);

        DamageResult result = CombatSystem.CalculateDamage(_serverData, targetSync._serverData, attackerFx, targetFx);

        // 원래 공격 데미지는 Thorns 반사와 무관하게 타겟에게 항상 적용 (먼저 처리하여 Thorns 사망 판정이 올바른 HP 순서를 보도록)
        float newHp = Mathf.Max(0f, targetSync.NetworkHp.Value - result.finalDamage);
        targetSync.NetworkHp.Value       = newHp;
        targetSync._serverData.currentHp = newHp;

        if (!result.isEvaded && !result.isDivineGraceBlocked && result.finalDamage > 0f)
        {
            // PostDamageEffects: 흡혈(공격자 HP 회복), lastCombatTime 등 처리 (Thorns 분리됨)
            CombatSystem.PostDamageEffects(_serverData, targetSync._serverData, attackerFx, targetFx, result.finalDamage);
            NetworkHp.Value = Mathf.Clamp(_serverData.currentHp, 0f, NetworkMaxHp.Value);
        }

        targetSync.NotifyHitClientRpc(result);

        // 1순위: 타겟 사망 체크 (일반 공격 흐름)
        // [Fix 신규-C] NetworkIsDead를 먼저 true로 설정하여 같은 프레임 이중 ProcessDeath 차단
        if (newHp <= 0f && !targetSync.NetworkIsDead.Value)
        {
            targetSync.NetworkIsDead.Value = true;
            ProcessDeath(targetSync, attackerFx, targetFx);
        }

        // [버그 수정 X-F] Thorns 반사를 정상 데미지 파이프라인(ApplyDamageServer)으로 처리.
        // 무한 반사 방지를 위해 _thornsReflecting 플래그로 재진입 차단.
        // 메인 브랜치의 Thorns 재설계(ApplyDamageServer 경로)가 본 worktree의 ProcessDeath 자해 패치를
        // 대체하므로, 충돌 해결 시 메인 버전을 채택. ApplyDamageServer가 사망/킬 크레딧을 일관되게 처리한다.
        if (!result.isEvaded && !result.isDivineGraceBlocked && result.finalDamage > 0f
            && !_thornsReflecting)
        {
            float reflect = CombatSystem.CalculateThornsReflect(targetSync._serverData, result.finalDamage);
            if (reflect > 0f)
            {
                _thornsReflecting = true;
                try { ApplyDamageServer(reflect, targetSync); }
                finally { _thornsReflecting = false; }
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  사망 처리
    // ════════════════════════════════════════════════════════════

    private void ProcessDeath(PlayerNetworkSync target, StatusEffectSystem attackerFx, StatusEffectSystem targetFx)
    {
        // NetworkIsDead.Value는 호출 측(RequestAttackServerRpc / ApplyDamageServer)에서 이미 true로 설정됩니다.
        // Guardian Angel / Tenacity 발동 시에는 false로 복구하여 생존 처리합니다.
        if (CombatSystem.TryGuardianAngel(target._serverData, targetFx))
        {
            target.NetworkIsDead.Value = false;
            target.NetworkHp.Value     = target._serverData.currentHp;
            return;
        }
        if (CombatSystem.TryTenacity(target._serverData, targetFx))
        {
            target.NetworkIsDead.Value = false;
            target.NetworkHp.Value     = target._serverData.currentHp;
            return;
        }

        // NetworkIsDead.Value는 이미 true — 중복 설정하지 않음
        target.DeclareDeathClientRpc(NetworkObject.NetworkObjectId);

        // ── [DeathMark] 낙인 폭발 훅 ─────────────────────────────
        // 대상이 낙인 상태로 사망했을 때, SkillSystem 코루틴 종료보다 먼저
        // 폭발을 실행해야 정확한 사망 시점의 accumulated 값을 사용할 수 있음.
        // StatusEffectSystem.OnEffectExpired 경로는 사망 후 Update에서 호출되므로
        // 여기서 명시적으로 선처리.
        if (target._serverData != null &&
            target._serverData.deathMarkActive &&
            target._serverData.deathMarkCasterId != ulong.MaxValue)
        {
            // 낙인 상태 플래그 즉시 비활성화 — OnEffectExpired와 이중 폭발 방지
            ulong casterId     = target._serverData.deathMarkCasterId;
            float accumulated  = target._serverData.deathMarkAccumulated;
            target._serverData.deathMarkActive       = false;
            target._serverData.deathMarkCasterId     = ulong.MaxValue;
            target._serverData.deathMarkAccumulated  = 0f;

            target.TriggerDeathMarkExplosion(casterId, accumulated, isKillExplosion: true);
        }

        bool canRevive = CanOfferRevive(target);

        if (canRevive)
        {
            target._pendingKiller = this;
            target.OfferReviveClientRpc();

            // 기존 CTS가 남아있으면 정리 후 새로 생성
            target.CancelAndDisposeCts();
            target._reviveCts = new CancellationTokenSource();

            // 타임아웃 Task 시작 — Unity 메인스레드에서 안전하게 실행
            _ = target.ReviveTimeoutAsync(target._reviveCts.Token);
        }
        else
        {
            target._hasUsedRevive = true;
            FinalizeDeath(target);
        }
    }

    private static bool CanOfferRevive(PlayerNetworkSync target)
    {
        var mgr = InGameManager.Instance;
        if (mgr == null) return false;
        if (target._hasUsedRevive) return false;
        // BUG-24: ReviveTimeLimit를 GameBalanceConfig에서 읽어 InGameHUD UI 표시와 동기화.
        float reviveLimit = GameBalanceConfig.Get()?.ReviveTimeLimit ?? 60f;
        if (mgr.ElapsedGameTime > reviveLimit) return false;
        // [FIX] AliveCount 타이밍 버그 + 조건식 오류 수정.
        // CanOfferRevive는 ProcessDeath에서 FinalizeDeath(→ OnPlayerDied → alivePlayers.Remove)
        // 보다 먼저 호출되므로 사망자가 아직 AliveCount에 포함된 상태.
        // 부활 후 실제 생존자 수 = AliveCount - 1.
        // 기존 <= 2: "부활 후 2명"인 경우도 거부 → 3명 매치에서 부활 불가 버그.
        // 수정 < 3 : "부활 후 2명 미만"일 때만 거부 (2명이면 게임 계속 가능).
        if (mgr.AliveCount < 3) return false;
        if (mgr.MatchReviveUsedCount >= InGameManager.MaxMatchReviveCount) return false;
        return true;
    }

    // 부활권 불가 사유 텍스트 (테스트/디버깅용)

    private static string GetReviveDeniedReason(PlayerNetworkSync target)
    {
        var mgr = InGameManager.Instance;
        if (mgr == null) return "InGameManager 없음";
        if (target._hasUsedRevive)                                         return "이미 부활권 사용함";
        float reviveLimitMsg = GameBalanceConfig.Get()?.ReviveTimeLimit ?? 60f;
        if (mgr.ElapsedGameTime > reviveLimitMsg)                          return $"시간 초과 ({mgr.ElapsedGameTime:0}초 / 제한 {reviveLimitMsg:0}초)";
        if (mgr.AliveCount < 3)                                            return $"생존자 {mgr.AliveCount - 1}명 (부활 후 최소 2명 필요)";
        if (mgr.MatchReviveUsedCount >= InGameManager.MaxMatchReviveCount) return $"매치 부활 횟수 소진 ({mgr.MatchReviveUsedCount}/{InGameManager.MaxMatchReviveCount})";
        return "알 수 없음";
    }

    private void FinalizeDeath(PlayerNetworkSync target)
    {
        var killer = target._pendingKiller ?? this;

        // [FIX] 자해 사망 시 자기 자신의 킬 카운트가 증가하는 버그.
        // source = null(자해, RuthlessStrike 등)로 ApplyDamageServer가 호출되면
        // effectiveAttacker = this = target 자신이 되고,
        // _pendingKiller = null이므로 killer = target 자신.
        // 결과: 자기 자신의 킬 카운트가 1 증가함.
        // 수정: killer가 target 자신과 다를 때만 킬 카운트를 올림.
        // BUG-04: 자해(Thorns 반사 등) 사망 시 killer==target인 채로 BroadcastKillFeed가
        // 호출되어 킬피드에 동일 닉네임 "X가 X를 처치"로 표시되는 문제.
        // 정상 킬은 기존대로, 자해 사망은 "[자멸]" 표기로 분리 송출.
        string victimName = target.NetworkNickname.Value.ToString();
        if (killer != target)
        {
            // BUG-03: killer가 Despawn(연결 끊김)된 상태에서 NetworkVariable 접근 시 NGO 예외 발생.
            // Thorns/DoT 등 지연 데미지로 가해자가 떠난 뒤 사망이 처리될 때 보호.
            if (killer != null && killer.IsSpawned)
            {
                killer.NetworkKillCount.Value++;
                string killerName = killer.NetworkNickname.Value.ToString();
                BroadcastKillFeedClientRpc(killerName, victimName);
            }
            else
            {
                BroadcastKillFeedClientRpc("[연결끊김]", victimName);
            }
        }
        else
        {
            BroadcastKillFeedClientRpc("[자멸]", victimName);
        }

        target._pendingKiller = null;

        InGameManager.Instance?.OnPlayerDied(target._controller);
    }

    [ClientRpc]
    private void BroadcastKillFeedClientRpc(string attackerName, string victimName)
    {
        InGameHUD.Instance?.ShowKillFeed(attackerName, victimName);
    }

    // H-8: 서버 측 MatchReviveUsedCount 변경을 모든 클라이언트에 동기화.
    [ClientRpc]
    private void SyncMatchReviveCountClientRpc(int usedCount)
    {
        if (InGameManager.Instance != null)
            InGameManager.Instance.SetMatchReviveUsedCount(usedCount);
    }

    // ════════════════════════════════════════════════════════════
    //  부활 타임아웃 — async Task (코루틴 WaitForSeconds 대체)
    //
    //  기존 ReviveTimeoutRoutine()의 문제:
    //   - WaitForSeconds는 취소 불가 → 플레이어 응답 후에도 5초를 기다림
    //   - 그 5초 사이 RequestReviveServerRpc와 겹치면 FinalizeDeath 이중 호출 가능
    //
    //  개선:
    //   - Task.Delay(token) : CTS.Cancel() 즉시 대기 종료 (0ms 지연)
    //   - _hasUsedRevive 재확인 : 플레이어가 이미 응답했으면 아무것도 안 함
    // ════════════════════════════════════════════════════════════
    private async Task ReviveTimeoutAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(6000, token); // 6.0초 대기: UI 5초 + RTT 여유 1초 (취소 가능)
        }
        catch (TaskCanceledException)
        {
            return;
        }

        // 5.5초가 지나도록 플레이어가 아무것도 안 한 경우
        // C-2: 기존 `!_hasUsedRevive && !_isProcessingRevive` 체크는 atomic 하지 않아
        // ReportReviveTicketResultServerRpc가 거의 동시에 도달하면 양쪽 모두 통과 → FinalizeDeath 2회 호출.
        // _isProcessingRevive 상태(Supabase 응답 대기)면 무조건 양보하여 한 경로만 사망 확정 처리.
        if (_hasUsedRevive || _isProcessingRevive) return;
        // BUG-01: Despawn(연결 끊김 등) 이후 ClientRpc / NetworkVariable 접근 시 NGO 예외 방지.
        if (!IsSpawned) return;
        _hasUsedRevive = true;
        // [버그 수정 X-B] 순서 변경 — ReviveDeniedClientRpc 먼저(UI 닫고), 그 후 FinalizeDeath.
        ReviveDeniedClientRpc();
        FinalizeDeath(this);
    }

    // ════════════════════════════════════════════════════════════
    //  매치 초기화
    // ════════════════════════════════════════════════════════════

    public void ResetReviveStateForNewMatch()
    {
        if (!IsServer) return;
        _hasUsedRevive      = false;
        _isProcessingRevive = false;
        _pendingKiller      = null;
        CancelAndDisposeCts();
        _skillLastUsed.Clear(); // 스킬 쿨다운 초기화
    }

    // ════════════════════════════════════════════════════════════
    //  부활 RPC
    // ════════════════════════════════════════════════════════════

    [ClientRpc]
    private void OfferReviveClientRpc()
    {
        if (IsOwner && InGameHUD.Instance != null)
            InGameHUD.Instance.ShowReviveUI(this);
    }

    [ServerRpc]
    public void RequestReviveServerRpc()
    {
        if (_hasUsedRevive || !NetworkIsDead.Value || _isProcessingRevive) return;

        if (!CanOfferRevive(this))
        {
            Debug.LogWarning($"[Server] {OwnerClientId} 부활 요청 거부 (조건 변경됨)");
            _hasUsedRevive = true;
            CancelAndDisposeCts();
            ReviveDeniedClientRpc();
            FinalizeDeath(this);
            return;
        }

        CancelAndDisposeCts();
        _isProcessingRevive = true;

        // ── 호환성 수정: Supabase 티켓 차감을 인증된 클라이언트에 위임 ──────
        // 문제: UseReviveTicket()은 Supabase auth.uid()로 유저를 식별하지만
        //       데디케이티드 서버는 Supabase에 로그인하지 않아
        //       Client.Auth.CurrentUser == null → 항상 false 반환.
        //       결과: 부활권이 있어도 서버에서 100% 부활 거부됨.
        //
        // 해결: 인증 세션을 가진 Owner 클라이언트에게 티켓 차감을 위임하고
        //       결과를 ReportReviveTicketResultServerRpc로 수신합니다.
        //       게임 조건은 이미 서버에서 검증 완료.
        //       Supabase DB 함수(use_revive_ticket)가 서버 사이드에서
        //       auth.uid()로 원자적 차감 처리하므로 위변조는 DB 레벨에서 차단됩니다.
        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        RequestTicketDeductionClientRpc(rpcParams);
    }

    // ════════════════════════════════════════════════════════════
    //  티켓 차감 위임 흐름
    //
    //  서버 → Owner 클라이언트 : RequestTicketDeductionClientRpc
    //  클라이언트               : UseReviveTicket() (인증된 세션으로 실행)
    //  클라이언트 → 서버        : ReportReviveTicketResultServerRpc(bool)
    //  서버                     : 부활 실행 또는 최종 사망 처리
    // ════════════════════════════════════════════════════════════

    [ClientRpc]
    private void RequestTicketDeductionClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        _ = DeductTicketAndReportAsync();
    }

    private async Task DeductTicketAndReportAsync()
    {
        bool success = false;

        if (SupabaseManager.Instance != null)
        {
            // [FIX] Supabase 응답 대기에 타임아웃 없음 버그.
            // RequestReviveServerRpc에서 ReviveTimeoutAsync를 취소한 뒤 이 Task를 시작하지만
            // 서버 측 타임아웃이 전혀 없어 Supabase 응답이 느리거나 클라이언트가 응답 못 하면
            // _isProcessingRevive = true 상태로 플레이어가 영구 사망 대기에 빠짐.
            // → 10초 타임아웃으로 제한하고 초과 시 부활 거부 처리.
            using var cts = new System.Threading.CancellationTokenSource(
                System.TimeSpan.FromSeconds(10));
            try
            {
                var ticketTask    = SupabaseManager.Instance.UseReviveTicket();
                var completedTask = await System.Threading.Tasks.Task.WhenAny(
                    ticketTask,
                    System.Threading.Tasks.Task.Delay(10000, cts.Token));
                if (completedTask == ticketTask)
                    success = ticketTask.IsCompletedSuccessfully && ticketTask.Result;
                else
                    Debug.LogWarning("[PlayerNetworkSync] 티켓 차감 타임아웃 (10초) → 부활 거부");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PlayerNetworkSync] 티켓 차감 오류: {e.Message}");
            }

            // [FIX #revive-double] 로컬 캐시 이중 차감 제거.
            // UseReviveTicket() 내부(SupabaseManager.cs)에서 성공 시 이미 -1 처리하므로
            // 여기서 한 번 더 차감하면 부활 1회에 티켓이 2장 소모되는 버그 발생.
        }
        else
        {
            // [Fix #5] SupabaseManager 없음 = 인증 불가 → 부활 불허
            // 이전 코드는 success = true 로 두어 부활권 없이 무료 부활이 가능했음.
            success = false;
            Debug.LogError("[PlayerNetworkSync] SupabaseManager 인스턴스가 없어 티켓 차감 불가 → 부활 거부");
        }

        // await 이후 Despawn 방어
        if (!IsSpawned) return;

        ReportReviveTicketResultServerRpc(success);
    }

    [ServerRpc]
    public void ReportReviveTicketResultServerRpc(bool success)
    {
        // 중복 호출 방지: _isProcessingRevive 상태일 때만 유효
        if (!_isProcessingRevive || !NetworkIsDead.Value) return;

        _isProcessingRevive = false;
        _hasUsedRevive = true;

        if (!success)
        {
            Debug.LogWarning($"[Server] {OwnerClientId} 부활 거부 — 보유 부활권 없음 (Supabase)");
            ReviveDeniedClientRpc();
            FinalizeDeath(this);
            return;
        }

        // ── 부활 실행 ─────────────────────────────────────────────
        NetworkIsDead.Value      = false;
        NetworkHp.Value          = NetworkMaxHp.Value;
        _serverData.currentHp    = _serverData.maxHp;
        _serverData.tenacityUsed = false; // 부활 시 즉사 방지 1회 기회 복구

        InGameManager.Instance?.OnReviveTicketUsed();
        // H-8: InGameManager는 NetworkBehaviour가 아니므로 MatchReviveUsedCount가 서버에만 갱신되어
        // 클라이언트 InGameHUD가 항상 "매치 잔여: 3/3" 으로 잘못 표시되던 버그.
        // PlayerNetworkSync ClientRpc로 모든 클라이언트에 브로드캐스트하여 InGameManager에 반영.
        int newUsedCount = InGameManager.Instance?.MatchReviveUsedCount ?? 0;
        SyncMatchReviveCountClientRpc(newUsedCount);
        InGameManager.Instance?.OnPlayerRevived(_controller);

        // [버그 수정] 부활 위치를 서버에서 결정하여 모든 클라이언트가 동일 위치를 보도록 전달.
        // 이전에는 클라이언트가 각자 GetNextSpawnPoint()를 호출하여 desync 가능.
        Vector2 spawnPos = NetworkSpawnManager.Instance != null
            ? NetworkSpawnManager.Instance.GetNextSpawnPoint()
            : Vector2.zero;
        ExecuteReviveClientRpc(spawnPos);
    }

    [ServerRpc]
    public void RequestGiveUpServerRpc()
    {
        // _isProcessingRevive 추가: 이미 Supabase 처리 중이면 포기 무시
        if (!NetworkIsDead.Value || _hasUsedRevive || _isProcessingRevive) return;

        CancelAndDisposeCts();
        _hasUsedRevive = true;
        FinalizeDeath(this);
    }

    [ClientRpc]
    private void ExecuteReviveClientRpc(Vector2 spawnPos)
    {
        _controller.ReviveNetwork(spawnPos);
    }

    [ClientRpc]
    private void ReviveDeniedClientRpc()
    {
        if (IsOwner) InGameHUD.Instance?.HideReviveUI();
    }

    [ClientRpc]
    private void NotifyHitClientRpc(DamageResult result)
    {
        _controller.ShowDamagePopupNetwork(result);
    }

    [ClientRpc]
    private void DeclareDeathClientRpc(ulong killerNetworkObjectId)
    {
        _controller.PlayDeathAnimation();
    }

    /// <summary>
    /// BUG-03: NGO SceneManager.LoadScene 폴백 경로용. 서버가 NGO 씬 전환에 실패했을 때
    /// 각 클라이언트가 로컬 씬 전환을 수행하도록 강제한다.
    /// </summary>
    [ClientRpc]
    public void ForceLoadResultSceneClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        Debug.LogWarning("[PlayerNetworkSync] NGO 씬 전환 폴백 — 로컬에서 ResultScene 로드.");
        GameManager.Instance?.LoadScene(GameManager.SceneResult);
    }

    /// <summary>
    /// 서버 → 각 Owner 클라이언트: 본인의 매치 결과를 전달합니다.
    /// InGameManager.FinishGame()에서 개별 OwnerClientId를 대상으로 호출됩니다.
    ///
    /// [설계 근거]
    ///  • 데디케이티드 서버는 Supabase auth.uid()가 없어 SaveMatchResult 호출 불가
    ///    → UseReviveTicket 위임 패턴(ReportReviveTicketResultServerRpc)과 동일한 구조
    ///  • GameManager(DontDestroyOnLoad) 저장 + Supabase 저장을 Owner 측에서 수행
    ///  • ClientRpcParams로 각 플레이어에게만 전송하므로 불필요한 브로드캐스트 없음
    /// </summary>
    [ClientRpc]
    public void NotifyMatchResultClientRpc(bool isWinner, int rank, int kills, float survivedTime,
        ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        if (GameManager.Instance == null) return;

        GameManager.Instance.lastMatchResult = new MatchResult
        {
            isWinner = isWinner, rank = rank, killCount = kills, survivedTime = survivedTime
        };

        // Supabase 저장은 인증 세션을 가진 Owner(클라이언트)에서 수행.
        // Task를 GameManager에 보관 — ResultController가 완료를 기다린 후 전적을 표시.
        if (SupabaseManager.Instance != null)
            GameManager.Instance.MatchResultSaveTask = SaveMatchResultAsync(isWinner, rank, kills, survivedTime);

        // [FEATURE] 리더보드 전적 기록
        LeaderboardManager.Instance?.SubmitMatchResult(isWinner, rank, kills, survivedTime);
    }

    private async Task SaveMatchResultAsync(bool win, int rank, int kills, float time)
    {
        try { await SupabaseManager.Instance.SaveMatchResult(win, rank, kills, time); }
        catch (System.Exception e) { Debug.LogError($"[PlayerNetworkSync] 결과 저장 실패: {e.Message}"); }
    }

    // ════════════════════════════════════════════════════════════
    //  게임 시작 동기화 RPC (InGameManager → 각 클라이언트 오너)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 서버가 이 플레이어의 Owner 클라이언트에게 게임 시작 시간을 전달합니다.
    /// NetworkSpawnManager.Instance 참조 실패 문제를 우회하기 위해
    /// 안정적으로 스폰된 PlayerNetworkSync를 채널로 사용합니다.
    /// </summary>
    [ClientRpc]
    public void NotifyGameStartedOwnerClientRpc(float serverStartTime,
        ClientRpcParams rpcParams = default)
    {
        // Owner가 아닌 클라이언트에서는 무시 (다른 플레이어의 RPC도 수신될 수 있음)
        if (!IsOwner) return;
        Debug.Log($"[PlayerNetworkSync] 게임 시작 신호 수신 (serverStartTime={serverStartTime})");
        InGameManager.Instance?.ClientReceiveGameStart(serverStartTime);
    }

    /// <summary>
    /// 서버가 이 플레이어의 Owner 클라이언트 HUD에 카운트다운 메시지를 표시합니다.
    /// </summary>
    [ClientRpc]
    public void ShowCountdownOwnerClientRpc(string message,
        ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        InGameHUD.Instance?.ShowGameEndBanner(message);
    }

    /// <summary>
    /// 서버가 이 플레이어의 Owner 클라이언트 HUD 배너를 숨깁니다.
    /// </summary>
    [ClientRpc]
    public void HideCountdownOwnerClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        if (InGameHUD.Instance?.endBannerPanel != null)
            InGameHUD.Instance.endBannerPanel.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 / 상태이상 (서버에서 호출)
    // ════════════════════════════════════════════════════════════

    public void ApplyDamageServer(float damage, PlayerNetworkSync source)
    {
        ApplyDamageServer(damage, source, new DamageResult { finalDamage = damage });
    }

    public void ApplyDamageServer(float damage, PlayerNetworkSync source, DamageResult result)
    {
        if (!IsServer || NetworkIsDead.Value || _serverData == null) return;
        if (damage <= 0f) return;

        // [FIX] 면역(IsImmune) 상태에서 DoT 데미지가 통과하는 버그.
        // CombatSystem.CalculateDamageWithOverride(평타/스킬 경로)는 IsImmune을 체크하지만
        // ApplyDamageServer(DoT/자해 경로)는 체크 없이 HP를 차감함.
        // IceShield, TenacityShield 발동 중에도 독/출혈/화상 DoT가 계속 깎이는 버그 수정.
        if (_controller.StatusFX != null && _controller.StatusFX.IsImmune)
        {
            result = new DamageResult { finalDamage = 0f, isEvaded = true };
            NotifyHitClientRpc(result);
            return;
        }

        // [FIX] DoT 데미지(ApplyDamageServer 경로)가 실드(ShieldHp)를 무시하는 버그.
        // CombatSystem.CalculateDamageWithOverride(평타/스킬 경로)는 AbsorbWithShield를 거치지만
        // ApplyDamageServer는 직접 HP를 차감해 실드가 있어도 관통함.
        // 독/출혈/화상이 IronSkin 실드를 완전 무시하는 결과 초래.
        // → 실드가 있으면 먼저 흡수하고 남은 데미지만 HP에서 차감.
        if (_controller.StatusFX != null && _serverData.shieldHp > 0f)
        {
            damage = _controller.StatusFX.AbsorbWithShield(damage);
            // HIGH-02: 새 DamageResult 생성으로 isCritical / isLuckyStrike / isWorldCollapse 등의 플래그가
            // 소실되어 클라이언트 팝업이 일반 데미지로 잘못 표시되던 버그. 기존 result를 유지하면서 필요한 필드만 갱신.
            result.finalDamage = damage;
            result.isShielded  = true;
            if (damage <= 0f)
            {
                NotifyHitClientRpc(result);
                return;
            }
        }

        float newHp = Mathf.Max(0f, NetworkHp.Value - damage);
        NetworkHp.Value       = newHp;
        _serverData.currentHp = newHp;

        if (_serverData.deathMarkActive) _serverData.deathMarkAccumulated += damage;

        NotifyHitClientRpc(result);

        // [Fix #11] NetworkIsDead를 먼저 true로 설정하여 같은 프레임 이중 ProcessDeath 차단
        if (newHp <= 0f && !NetworkIsDead.Value)
        {
            NetworkIsDead.Value = true;
            var effectiveAttacker = source ?? this;
            effectiveAttacker.ProcessDeath(this, effectiveAttacker._controller.StatusFX, _controller.StatusFX);
        }
    }

    /// <summary>
    /// 서버 전용. 시전자(caster) 사망 시 낙인을 강제 해제합니다.
    /// StatusEffectSystem.RemoveEffect → OnEffectExpired를 통해 데이터 초기화까지 수행합니다.
    /// SkillSystem.DeathMark 코루틴의 분기 C(시전자 사망)에서만 호출됩니다.
    /// </summary>
    public void ForceRemoveDeathMarkServer()
    {
        if (!IsServer) return;
        if (_serverData != null)
        {
            _serverData.deathMarkActive      = false;
            _serverData.deathMarkCasterId    = ulong.MaxValue;
            _serverData.deathMarkAccumulated = 0f;
        }
        _controller.StatusFX?.RemoveEffect(StatusEffectType.DeathMarkTarget);

        // 모든 클라이언트에 낙인 해제 동기화 (클라이언트 StatusFX 상태 일치)
        // [NEW-10] forceRemove 파라미터로 강제 해제 신호를 명시 (이전엔 duration=0 ambiguity)
        SyncStatusEffectClientRpc((int)StatusEffectType.DeathMarkTarget, 0f, 0f, ulong.MaxValue, true);
    }

    public void ApplyStatusEffectServer(StatusEffectType type, float duration, float value = 0f, PlayerNetworkSync source = null)
    {
        if (!IsServer || NetworkIsDead.Value) return;

        // [FIX] Swiftness 패시브 슬로우 저항 미적용 버그.
        // StatCalculator.ModifySlowDuration()이 정의되어 있고 Swiftness 보유 시
        // 슬로우 지속시간을 70%로 단축하도록 설계되어 있으나,
        // ApplyStatusEffectServer에서 이 함수를 통과시키지 않아
        // Swiftness 패시브 보유자도 일반 플레이어와 동일한 슬로우 지속시간을 받음.
        float adjustedDuration = duration;
        if (type == StatusEffectType.Slow && _serverData != null)
            adjustedDuration = StatCalculator.ModifySlowDuration(_serverData, duration);

        _controller.StatusFX.ApplyEffectServer(type, adjustedDuration, value, source?._controller);
        // source NetworkObjectId 전달 → 클라이언트에서 피격 방향·넉백 방향 연출에 활용
        ulong srcId = (source?.NetworkObject != null) ? source.NetworkObject.NetworkObjectId : ulong.MaxValue;
        SyncStatusEffectClientRpc((int)type, adjustedDuration, value, srcId);
    }

    [ClientRpc]
    private void SyncStatusEffectClientRpc(int type, float duration, float value, ulong sourceNetObjId, bool forceRemove = false)
    {
        if (IsServer) return;

        // [NEW-10] 강제 해제는 forceRemove 파라미터로 판단 (duration=0 ambiguity 해소)
        if (forceRemove)
        {
            _controller.StatusFX.RemoveEffect((StatusEffectType)type);
            return;
        }
        if (duration <= 0f) return;

        PlayerController sourceCtrl = null;
        if (sourceNetObjId != ulong.MaxValue &&
            NetworkManager.Singleton?.SpawnManager.SpawnedObjects
                .TryGetValue(sourceNetObjId, out var srcNetObj) == true)
            sourceCtrl = srcNetObj.GetComponent<PlayerController>();
        _controller.StatusFX.ApplyEffectNetwork((StatusEffectType)type, duration, value, sourceCtrl);
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 RPC
    // ════════════════════════════════════════════════════════════

    [ServerRpc]
    public void RequestUseSkillServerRpc(int slotIndex, Vector2 targetPos, Vector2 facingDir = default)
    {
        if (NetworkIsDead.Value || _serverData == null) return;

        var statusFx = _controller.StatusFX;
        if (statusFx != null && statusFx.IsSilenced) return;

        if (slotIndex < 0 || slotIndex >= _serverData.activeSkills.Count) return;

        ActiveSkillType skill = _serverData.activeSkills[slotIndex];

        // 서버 측 쿨다운 검증 — 클라이언트가 쿨다운을 무시하고 RPC를 반복 전송해도 서버에서 차단
        float cd = SkillSystem.GetCooldown(skill);
        if (_skillLastUsed.TryGetValue(skill, out float lastUsed) && Time.time - lastUsed < cd) return;
        _skillLastUsed[skill] = Time.time;

        SkillSystem.ActivateSkillServer(skill, _controller, targetPos, facingDir);
        BroadcastSkillVisualsClientRpc((int)skill, targetPos);
    }

    [ClientRpc]
    private void BroadcastSkillVisualsClientRpc(int skillType, Vector2 targetPos)
    {
        _controller.PlaySkillVisuals((ActiveSkillType)skillType, targetPos);
    }

    /// <summary>caster 클라이언트(소유자)에게만 Lucky! 팝업 표시</summary>
    public void ShowLuckyPopupOwner()
    {
        if (!IsServer) return;
        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { OwnerClientId } }
        };
        ShowLuckyPopupClientRpc(rpcParams);
    }

    [ClientRpc]
    private void ShowLuckyPopupClientRpc(ClientRpcParams rpcParams = default)
    {
        _controller.ShowSkillPopup("Lucky!");
    }

    /// <summary>SnackTime — 모든 클라이언트에 caster의 디버프 즉시 해제 전파</summary>
    public void BroadcastRemoveAllDebuffs()
    {
        if (!IsServer) return;
        RemoveAllDebuffsClientRpc();
    }

    [ClientRpc]
    private void RemoveAllDebuffsClientRpc()
    {
        _controller.StatusFX?.RemoveAllDebuffs();
    }

    // ════════════════════════════════════════════════════════════
    //  Trap(덫놓기) 시각화 RPC
    //
    //  [이전 구현의 문제]
    //  서버만 OverlapCircle로 덫 판정을 수행하고 클라이언트에 시각 동기화가 없었음.
    //  피격 플레이어 입장에서는 아무것도 없는 곳에서 갑자기 피해와 슬로우가 발생함.
    //
    //  [해결]
    //  SpawnTrapVisualClientRpc  : 덫 배치 시 모든 클라이언트에 시각 오브젝트 생성
    //  NotifyTrapTriggeredClientRpc : 발동 시 피격 이펙트 재생
    //  RemoveTrapVisualClientRpc : 만료/소진 시 시각 오브젝트 제거
    //
    //  [Inspector 연결 필요]
    //  PlayerController 프리팹에 trapVisualPrefab을 연결하거나,
    //  없으면 DamagePopupPool 팝업으로 폴백합니다.
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 서버 → 모든 클라이언트: 덫 위치에 시각 오브젝트를 생성합니다.
    /// SkillSystem의 Trap 케이스에서 루틴 시작 직전 호출됩니다.
    /// </summary>
    [ClientRpc]
    public void SpawnTrapVisualClientRpc(Vector2 trapPos)
    {
        var trapPrefab = _controller.trapVisualPrefab;
        if (trapPrefab != null)
        {
            var go = Object.Instantiate(trapPrefab, trapPos, Quaternion.identity);
            // 15초 후 자동 삭제 (서버 루틴 최대 시간과 동일)
            Object.Destroy(go, 15f);
            _controller.RegisterTrapVisual(trapPos, go);
        }
        else
        {
            // 프리팹 미설정 시 폴백: 위치에 텍스트 팝업 표시
            DamagePopupPool.Instance?.Spawn(trapPos, "🪴", new Color(0.8f, 0.6f, 0.1f));
        }
    }

    /// <summary>
    /// 서버 → 모든 클라이언트: 덫이 발동됐음을 알리고 피격 이펙트를 재생합니다.
    /// </summary>
    [ClientRpc]
    public void NotifyTrapTriggeredClientRpc(Vector2 trapPos)
    {
        DamagePopupPool.Instance?.Spawn(trapPos + Vector2.up * 0.3f, "TRAP!",
            new Color(0.9f, 0.5f, 0f)); // 주황색
    }

    /// <summary>
    /// 서버 → 모든 클라이언트: 덫이 만료/소진됐을 때 시각 오브젝트를 제거합니다.
    /// </summary>
    [ClientRpc]
    public void RemoveTrapVisualClientRpc(Vector2 trapPos)
    {
        _controller.UnregisterTrapVisual(trapPos);
    }

    // ════════════════════════════════════════════════════════════
    //  위치 강제 설정 RPC — ClientNetworkTransform 환경 전용
    //
    //  ClientNetworkTransform은 Owner 클라이언트가 위치 권한을 가지므로
    //  서버에서 Rb.position을 직접 수정해도 클라이언트 값으로 롤백됩니다.
    //  ShadowRaid(순간이동), ChargeStrike/Bulldozer(돌진), 넉백 등
    //  이동을 수반하는 모든 스킬은 반드시 Owner 클라이언트에게 RPC로 지시해야 합니다.
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Owner 클라이언트의 위치를 서버 인증 좌표로 강제 보정합니다.
    /// 용도 1 — 스킬: ShadowRaid(순간이동), ChargeStrike/Bulldozer(돌진), 넉백
    /// 용도 2 — ServerValidator: 속도핵/텔레포트 감지 시 원래 위치로 롤백
    /// ClientNetworkTransform 환경에서는 서버 직접 수정이 클라이언트에 롤백되므로
    /// 반드시 이 RPC를 통해 Owner에게 위치를 지시해야 합니다.
    /// </summary>
    [ClientRpc]
    public void ForcePositionClientRpc(Vector2 pos, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        if (_controller?.Rb != null)
            _controller.Rb.position = pos;
    }

    [ClientRpc]
    public void ForceMoveClientRpc(Vector2 from, Vector2 to, float duration, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        StartCoroutine(ForceMoveCoroutine(from, to, duration));
    }

    private System.Collections.IEnumerator ForceMoveCoroutine(Vector2 from, Vector2 to, float duration)
    {
        float el = 0f;
        while (el < duration)
        {
            el += Time.deltaTime;
            _controller.Rb.MovePosition(Vector2.Lerp(from, to, Mathf.Clamp01(el / duration)));
            yield return null;
        }
        _controller.Rb.position = to;
    }

    [ClientRpc]
    public void ForceKnockbackClientRpc(Vector2 dir, float force, float duration, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        StartCoroutine(KnockbackCoroutine(dir, force, duration));
    }

    private System.Collections.IEnumerator KnockbackCoroutine(Vector2 dir, float force, float duration)
    {
        float el = 0f;
        while (el < duration)
        {
            el += Time.deltaTime;
            _controller.Rb.MovePosition(_controller.Rb.position +
                dir * force * (1f - el / duration) * Time.deltaTime);
            yield return null;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  DeathMark 전용 — 폭발·체이닝·킬피드
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 서버 전용. StatusEffectSystem(낙인 만료) 또는 ProcessDeath(낙인 대상 사망) 에서 호출.
    ///
    /// isKillExplosion = true  : 낙인 대상 사망으로 폭발 → 주변 체이닝 + 킬 크레딧 + 쿨다운 보상
    /// isKillExplosion = false : 낙인 시간 만료 소멸     → 절반 피해 + 체이닝 없음
    ///
    /// [설계 원칙]
    ///  • 폭발 데미지는 ApplyDamageServer를 통해 ProcessDeath까지 완전 위임
    ///  • 체이닝 킬 크레딧은 원래 caster에게 귀속 (FinalizeDeath의 _pendingKiller 통해)
    ///  • 쿨다운 감소는 서버의 _skillLastUsed 조작으로 구현 (클라이언트 위변조 불가)
    /// </summary>
    public void TriggerDeathMarkExplosion(ulong casterNetObjId, float accumulated, bool isKillExplosion)
    {
        if (!IsServer) return;

        // ── 시전자 조회 ─────────────────────────────────────────
        PlayerNetworkSync casterSync = null;
        if (casterNetObjId != ulong.MaxValue &&
            NetworkManager.Singleton?.SpawnManager.SpawnedObjects
                .TryGetValue(casterNetObjId, out var casterObj) == true)
            casterSync = casterObj?.GetComponent<PlayerNetworkSync>();

        // 시전자가 이미 사망·Despawn됐으면 폭발 취소
        if (casterSync == null || !casterSync.IsSpawned || casterSync.NetworkIsDead.Value) return;

        PlayerController caster = casterSync._controller;
        if (caster == null) return;

        // ── 폭발 데미지 계산 ────────────────────────────────────
        float explosionDamage;
        if (isKillExplosion)
            // 사망 폭발: baseAtk × 2.0 + 쌓인 피해 × 0.5
            explosionDamage = casterSync._serverData.baseAtk * 2.0f + accumulated * 0.5f;
        else
            // 시간 만료 소멸: 쌓인 피해의 35% (소형 패널티 폭발)
            explosionDamage = accumulated * 0.35f;

        explosionDamage = Mathf.Round(explosionDamage * 10f) / 10f;
        if (explosionDamage <= 0f) return;

        // ── 낙인 대상 주변 2.5유닛 체이닝 탐색 ─────────────────
        if (isKillExplosion)
        {
            foreach (var col in Physics2D.OverlapCircleAll(
                _controller.transform.position, 2.5f, caster.enemyLayer))
            {
                var pc = col.GetComponent<PlayerController>();
                if (pc == null || pc == _controller || pc == caster ||
                    pc.IsDead || pc.networkSync == null ||
                    pc.networkSync.NetworkIsDead.Value) continue;

                float chainDmg = Mathf.Round(explosionDamage * 0.6f * 10f) / 10f;
                if (chainDmg <= 0f) continue;
                // [버그 수정] 체이닝 사망 시 킬 크레딧이 원래 caster에게 귀속되도록
                // _pendingKiller를 ApplyDamageServer 호출 직전에 설정.
                pc.networkSync._pendingKiller = casterSync;
                pc.networkSync.ApplyDamageServer(chainDmg, casterSync);
            }
        }

        // ── 킬피드 & 쿨다운 감소 보상 (사망 폭발 전용) ──────────
        if (isKillExplosion)
        {
            string casterName = casterSync.NetworkNickname.Value.ToString();
            string victimName = NetworkNickname.Value.ToString();
            DeathMarkKillFeedClientRpc(casterName, victimName);

            // 쿨다운 감소: _skillLastUsed[DeathMark]를 8초 앞당김
            // → 22초 쿨다운 중 최대 8초 단축 (연속 낙인 처형 장려)
            if (casterSync._skillLastUsed.ContainsKey(ActiveSkillType.DeathMark))
                casterSync._skillLastUsed[ActiveSkillType.DeathMark] -= 8f;
        }
    }

    /// <summary>서버 → 전체 클라이언트: 낙인처형 전용 킬피드 + 시전자 HUD 보상 팝업</summary>
    [ClientRpc]
    private void DeathMarkKillFeedClientRpc(string casterName, string victimName)
    {
        // 모든 클라이언트: 전용 킬피드 ("☠암살자명 → 피해자명[낙인처형]")
        InGameHUD.Instance?.ShowKillFeed($"☠{casterName}", $"{victimName}[낙인처형]");

        // 시전자 클라이언트에게만 쿨다운 감소 팝업 표시
        string myNick = GameManager.Instance?.currentPlayerNickname;
        if (!string.IsNullOrEmpty(myNick) && myNick == casterName)
        {
            Vector3 popupPos = Camera.main != null
                ? Camera.main.ViewportToWorldPoint(new Vector3(0.5f, 0.72f, 10f))
                : Vector3.up * 3f;
            DamagePopupPool.Instance?.Spawn(popupPos, "낙인처형! CD-8초",
                new Color(0.6f, 0f, 1f)); // 보라색
        }
    }

    // ════════════════════════════════════════════════════════════
    //  내부 유틸
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// CancellationTokenSource를 안전하게 취소하고 메모리를 해제합니다.
    /// Cancel 후 Dispose를 반드시 함께 호출해야 메모리 누수가 없습니다.
    /// </summary>
    private void CancelAndDisposeCts()
    {
        if (_reviveCts == null) return;
        if (!_reviveCts.IsCancellationRequested)
            _reviveCts.Cancel();
        _reviveCts.Dispose();
        _reviveCts = null;
    }

    // ════════════════════════════════════════════════════════════
    //  직렬화 유틸
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 리롤 후 새 캐릭터 데이터를 서버에 재제출합니다.
    /// CharacterRerollSystem에서 Owner 클라이언트 측으로 호출됩니다.
    /// </summary>
    public void ResubmitCharacterData()
    {
        if (!IsOwner) return;
        var netData  = BuildNetworkData();
        var nickname = new Unity.Collections.FixedString64Bytes(
            GameManager.Instance?.currentPlayerNickname ?? "");
        var userId   = new Unity.Collections.FixedString64Bytes(
            GameManager.Instance?.currentPlayerId ?? "");
        SubmitCharacterDataServerRpc(netData, nickname, userId);
    }

    private NetworkCharacterData BuildNetworkData()
    {
        // OnNetworkSpawn 시점엔 myData가 프리팹 기본값일 수 있으므로 GameManager를 우선 (진짜 데이터)
        var d = GameManager.Instance?.myCharacterData ?? _controller.myData;
        if (d == null) { Debug.LogWarning("[PlayerNetworkSync] CharacterData 없음, 기본값 전송"); return default; }
        return new NetworkCharacterData
        {
            Job = (int)d.job, Affinity = (int)d.affinity, Grade = (int)d.grade,
            MaxHp = d.maxHp, BaseAtk = d.baseAtk, MoveSpeed = d.moveSpeed,
            AttackCooldown = d.attackCooldown,
            Active0 = GetActive(d, 0), Active1 = GetActive(d, 1),
            Active2 = GetActive(d, 2), Active3 = GetActive(d, 3),
            Passive0 = GetPassive(d, 0), Passive1 = GetPassive(d, 1),
            Passive2 = GetPassive(d, 2), Passive3 = GetPassive(d, 3),
        };
    }

    private static int GetActive(CharacterData d, int i)  => i < d.activeSkills.Count  ? (int)d.activeSkills[i]  : -1;
    private static int GetPassive(CharacterData d, int i) => i < d.passiveSkills.Count ? (int)d.passiveSkills[i] : -1;

    [ServerRpc]
    public void UpdateMoveDirServerRpc(Vector2 dir)
    {
        if (NetworkIsDead.Value) return;
        NetworkMoveDir.Value = dir;
    }
}
