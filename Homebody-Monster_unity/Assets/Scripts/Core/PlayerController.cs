using UnityEngine;
using UnityEngine.EventSystems;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
[RequireComponent(typeof(StatusEffectSystem))]
[RequireComponent(typeof(PlayerNetworkSync))]
public class PlayerController : NetworkBehaviour
{
    [Header("Character Data")]
    [field: SerializeField] public CharacterData myData { get; private set; }
    [Header("Input")] public VariableJoystick movementJoystick;
    [Header("Combat Settings")]
    public float attackRange = 1.8f;
    public LayerMask enemyLayer;

    private float AttackCooldown => myData?.attackCooldown ?? 0.8f;
    [Header("Visual")]
    public SpriteRenderer spriteRenderer;
    public Animator animator;

    [Header("Trap Settings")]
    [Tooltip("덫 위치를 나타낼 시각 오브젝트 프리팹. 미설정 시 텍스트 팝업으로 폴백합니다.")]
    public GameObject trapVisualPrefab;

    // 배치된 덫 시각 오브젝트 추적 (위치 → GameObject)
    // RemoveTrapVisualClientRpc에서 정확한 오브젝트를 찾아 제거하기 위해 사용
    private readonly Dictionary<Vector2, GameObject> _trapVisuals
        = new Dictionary<Vector2, GameObject>();

    public void RegisterTrapVisual(Vector2 pos, GameObject go)
    {
        if (go != null) _trapVisuals[pos] = go;
    }

    public void UnregisterTrapVisual(Vector2 pos)
    {
        if (_trapVisuals.TryGetValue(pos, out var go))
        {
            if (go != null) Object.Destroy(go);
            _trapVisuals.Remove(pos);
        }
    }

    // NGO IsOwner로 대체 — IsLocalPlayer 외부 set은 제거 (NetworkBehaviour에서 관리)
    public new bool IsLocalPlayer => IsOwner;
    public int  killCount     { get; private set; } = 0;
    public bool IsDead        { get; private set; } = false;

    public Rigidbody2D        Rb          { get; private set; }
    public StatusEffectSystem StatusFX    { get; private set; }
    public PlayerNetworkSync  networkSync { get; private set; }

    public readonly NetworkVariable<bool> NetworkIsChasing = new(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private Vector2          moveDir;
    private PlayerController targetEnemy;
    private float            lastAttackTime = -999f;
    private bool             isChasing      = false;
    private Coroutine        _autoAttackCoroutine;
    private bool             _lastSyncedChasing = false;
    private bool             movementLocked = true;
    // [FIX] 초기 상태를 잠금(true)으로 변경. InGameManager에서 게임 시작 시 명시적으로 풀어주기 전까지 이동 불가.
    private bool             attackLocked   = true;
    private static Camera    mainCam;

    // [디버그 / 단독 테스트] 서버가 직접 스폰한 더미/봇 식별 플래그.
    // 호스트 환경에서는 OwnerClientId == ServerClientId == 0 이므로 봇도 IsOwner 가 true 가 되어
    // HandleJoystickInput / HandleTouchAttackInput / FixedUpdate 가 동작 → 본인 + 더미가 동시에 이동.
    // 이 플래그가 true 면 owner 입력 처리를 모두 우회하여 더미는 가만히 있도록 함.
    // 정상 매칭 흐름에서는 절대 set 되지 않으므로 부작용 없음.
    private bool             _isBot         = false;
    public bool IsBot => _isBot;
    public void SetAsBot(bool isBot) => _isBot = isBot;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        Rb          = GetComponent<Rigidbody2D>();
        StatusFX    = GetComponent<StatusEffectSystem>();
        networkSync = GetComponent<PlayerNetworkSync>();
        Rb.gravityScale   = 0f;
        Rb.freezeRotation = true;
        if (mainCam == null) mainCam = Camera.main;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsOwner)
        {
            // CameraFollowLocalPlayer 스크립트가 CinemachineCamera에서 로컬 플레이어를 자동 추적합니다.
            // 이곳에서 중복 설정하면 Cinemachine 3.x에서 두 번 할당이 발생하므로 제거.

            // New Input System 터치 지원 활성화
            UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.Enable();

            // 동적 스폰 시 씬에 배치된 조이스틱을 자동으로 연결
            // Unity 6: FindObjectsInactive.Include 로 비활성 Canvas 의 조이스틱도 탐색
            if (movementJoystick == null)
                movementJoystick = FindFirstObjectByType<VariableJoystick>(FindObjectsInactive.Include);

            // [외형 동기화] Owner 는 본인 직업을 이미 알고 있으므로
            // SubmitCharacterDataServerRpc 라운드트립 대기 없이 즉시 적용 → 깜빡임 제거.
            if (GameManager.Instance?.myCharacterData != null)
                UpdateVisualByJob((int)GameManager.Instance.myCharacterData.job);
        }

        // [외형 동기화] 모든 클라이언트(Owner 포함)에서 NetworkJob 구독.
        // Late join 또는 재접속 시 OnValueChanged 가 발동되지 않을 수 있으므로
        // 현재 값이 유효(-1 아님)하면 즉시 한 번 적용한다.
        if (networkSync != null)
        {
            networkSync.NetworkJob.OnValueChanged += OnNetworkJobChanged;
            int currentJob = networkSync.NetworkJob.Value;
            if (currentJob >= 0) UpdateVisualByJob(currentJob);
        }
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        if (networkSync != null)
            networkSync.NetworkJob.OnValueChanged -= OnNetworkJobChanged;

        // 연결 끊김/씬 전환 시 자동 평타 코루틴 누수 방지.
        // PlayDeathAnimation 경로와 별개로 Despawn에서도 명시적으로 정리.
        if (_autoAttackCoroutine != null)
        {
            StopCoroutine(_autoAttackCoroutine);
            _autoAttackCoroutine = null;
        }
        targetEnemy = null;
        isChasing   = false;

        if (IsOwner)
            UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.Disable();
    }

    // ════════════════════════════════════════════════════════════
    //  외형 교체 (직업별)
    //  Owner: SubmitCharacterDataServerRpc 직전 즉시 호출
    //  Non-Owner: NetworkJob.OnValueChanged 콜백으로 호출
    // ════════════════════════════════════════════════════════════

    private void OnNetworkJobChanged(int prev, int curr)
    {
        if (curr < 0) return;             // 미설정 상태 무시
        UpdateVisualByJob(curr);
    }

    public void UpdateVisualByJob(int jobIndex)
    {
        // 데디케이티드 서버(헤드리스) 는 시각 처리 불필요.
        // IsServer && !IsClient 일 때만 스킵 — listen-server(Host)는 클라이언트 역할도 하므로 진행.
        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsServer
            && !NetworkManager.Singleton.IsClient)
            return;

        // jobIndex 범위 검증 (enum 값이 0~9, -1 은 미설정)
        if (jobIndex < 0 || jobIndex > (int)JobType.Chef) return;

        // Despawn 직후 콜백 도달 가능 → 컴포넌트 null 가드
        if (animator == null && spriteRenderer == null) return;

        var registry = JobVisualRegistry.Instance;
        if (registry == null) return; // 워닝은 Instance getter 에서 1회 출력

        // 등록되지 않은 직업이면 기본 외형 유지 (정상 동작)
        if (!registry.TryGetVisual((JobType)jobIndex, out var visual)) return;

        if (animator != null && visual.animatorController != null)
        {
            animator.runtimeAnimatorController = visual.animatorController;
            // Controller 교체 시 현재 재생 상태가 무효화되므로 Idle 첫 프레임으로 리셋
            animator.Rebind();
            animator.Update(0f);
        }
        if (spriteRenderer != null && visual.defaultSprite != null)
            spriteRenderer.sprite = visual.defaultSprite;
    }

    private void Start()
    {
        // [버그 수정] 모든 PlayerController 에서 무조건 GameManager.myCharacterData 로 덮어쓰면
        // 다음 두 시나리오에서 데이터 오염 발생:
        //  ① 호스트 환경에서 서버 소유 더미가 IsOwner=true 가 되어 본인 controller 와 더미 controller
        //     양쪽 다 myData = GameManager.myCharacterData (같은 객체!) 를 가리킴.
        //     → 더미 피격 시 HandleHpChanged 가 dummy._controller.myData.currentHp = dummy_hp 로
        //        호스트의 currentHp 를 9999 등으로 덮어버림.
        //  ② 멀티플레이 환경에서 원격 클라이언트의 PlayerController 역시 로컬 GameManager 데이터로
        //     덮어써져 원격 캐릭터 정보가 로컬 캐릭터 정보로 오염됨.
        //
        // 진짜로 myData = GameManager.myCharacterData 가 필요한 경우는 단 하나:
        //  로컬 클라이언트가 직접 조작하는 본인 캐릭터 (= IsOwner && _isBot == false).
        //  그 외의 경우엔 PlayerNetworkSync.SubmitCharacterDataServerRpc / DebugInitializeAsBot 가
        //  SetMyData(_serverData) 로 이미 정확한 데이터를 주입했으므로 그대로 둔다.
        if (IsOwner && !_isBot && GameManager.Instance?.myCharacterData != null)
            myData = GameManager.Instance.myCharacterData;

        // 등록은 PlayerNetworkSync.OnNetworkSpawn에서 수행하므로 중복 제거
        // (원본에서는 Start에서도 호출했으나 NGO 구조에서는 OnNetworkSpawn이 기준)

        // [버그 수정] 호스트 환경에서 서버 소유 더미도 IsOwner=true 이므로 본인 + 더미가
        // 둘 다 InitPlayerUI / UpdateHealthBar 를 호출하여 마지막에 Start 가 실행된 캐릭터가
        // InGameHUD.localPlayer 와 healthBar 를 차지함. 결과적으로 더미의 myData (activeSkills 비어있음)
        // 가 localPlayer 로 등록되어 스킬 버튼이 실제로는 더미.UseSkill 을 호출하고 skillCount=0 으로 거부됨.
        // → 봇은 HUD 와 무관하므로 InitPlayerUI / UpdateHealthBar 호출을 명시적으로 스킵.
        if (IsOwner && !_isBot && InGameHUD.Instance != null && myData != null)
        {
            InGameHUD.Instance.InitPlayerUI(this);
            InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);
        }
    }

    private void Update()
    {
        // [디버그] 봇은 owner 권한이 있어도 입력 처리를 절대 받지 않는다.
        if (_isBot) { UpdateAnimation(); return; }

        if (IsOwner) HandleBackKeyInput();

        if (IsDead) return;

        if (IsOwner)
        {
            HandleJoystickInput();
            HandleTouchAttackInput();
        }

        if (IsOwner && isChasing != _lastSyncedChasing)
        {
            _lastSyncedChasing = isChasing;
            SyncChasingToServer(isChasing);
        }

        // [FIX] UpdateAnimation을 IsOwner 조건 밖으로 이동.
        // 기존: !IsOwner이면 UpdateAnimation()을 호출하지 않아
        //        다른 플레이어 캐릭터의 spriteRenderer.flipX가 전혀 갱신되지 않음.
        //        → 다른 플레이어가 왼쪽으로 이동해도 항상 오른쪽을 바라보는 버그.
        // 수정: 모든 플레이어에 대해 UpdateAnimation()을 호출.
        //        flipX는 moveDir(Owner) 또는 networkSync.NetworkMoveDir(비Owner)을 기준으로 갱신.
        UpdateAnimation();
    }

    private void FixedUpdate()
    {
        // [디버그] 봇은 owner 권한이 있어도 이동/추격을 처리하지 않는다.
        if (_isBot) return;

        if (IsDead || !IsOwner) return;

        if (movementLocked)
        {
            #if UNITY_6000_0_OR_NEWER
                Rb.linearVelocity = Vector2.zero;
            #else
                Rb.velocity = Vector2.zero;
            #endif
            return;
        }

        if (myData == null) return;

        float spd = StatCalculator.GetEffectiveMoveSpeed(myData, StatusFX);
        // [버그 수정] 이동 처리에도 IsStealthy 체크 추가 (ChaseAndAttack과 동일 패턴).
        // ChaseAndAttack 코루틴이 yield return null 직후 체크하기 전에
        // FixedUpdate가 먼저 실행되어 은신 적 방향으로 1프레임 이동하는 것을 방지.
        bool targetBecameStealthy = targetEnemy != null
            && targetEnemy.StatusFX != null
            && targetEnemy.StatusFX.IsStealthy;
        if (targetBecameStealthy)
        {
            isChasing   = false;
            targetEnemy = null;
        }

        if (isChasing && targetEnemy != null && !targetEnemy.IsDead)
        {
            Vector2 dir = ((Vector2)targetEnemy.transform.position - Rb.position).normalized;
            Rb.MovePosition(Rb.position + dir * spd * Time.fixedDeltaTime);
        }
        else
        {
            Rb.MovePosition(Rb.position + moveDir * spd * Time.fixedDeltaTime);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  입력 처리 (Owner 전용)
    // ════════════════════════════════════════════════════════════

    private float _joystickLookupCooldown = 0f;

    private void HandleJoystickInput()
    {
        // [버그 수정] 인게임 씬 로드 직후엔 조이스틱 Canvas 가 비활성/미생성 상태일 수 있어
        // 1회 탐색 후 영구 포기하면 조이스틱이 끝까지 null 로 남는 문제 발생.
        // → 1초 간격으로 재시도 + Unity 6 의 FindObjectsInactive.Include 로 비활성도 포함.
        if (movementJoystick == null)
        {
            _joystickLookupCooldown -= Time.deltaTime;
            if (_joystickLookupCooldown <= 0f)
            {
                movementJoystick = FindFirstObjectByType<VariableJoystick>(FindObjectsInactive.Include);
                _joystickLookupCooldown = 1f; // 못 찾으면 1초 후 재시도
            }
            if (movementJoystick == null) return;
        }
        
        moveDir.x = movementJoystick.Horizontal;
        moveDir.y = movementJoystick.Vertical;
        if (moveDir.sqrMagnitude > 0.01f) { isChasing = false; targetEnemy = null; }

        // [FIX] 이동 방향을 ServerRpc를 통해 동기화하여 권한 에러 해결
        if (networkSync != null && networkSync.IsOwner &&
            Vector2.Distance(networkSync.NetworkMoveDir.Value, moveDir) > 0.05f)
            networkSync.UpdateMoveDirServerRpc(moveDir);
    }

    // ════════════════════════════════════════════════════════════
    //  Back 키 / ESC — 매치 중도 포기 확인 팝업 토글
    //  (실시간 멀티이므로 게임 자체는 일시정지하지 않고 오버레이만 표시)
    // ════════════════════════════════════════════════════════════
    private void HandleBackKeyInput()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        // Android Back 버튼은 New Input System에서 escapeKey 로 매핑됨.
        if (!kb.escapeKey.wasPressedThisFrame) return;

        var hud = InGameHUD.Instance;
        if (hud == null) return;

        // 이미 표시 중이면 ESC 로 닫기 (토글 UX).
        if (hud.IsSurrenderConfirmShown)
        {
            hud.HideSurrenderConfirm();
            return;
        }

        // 사망/관전 상태 또는 게임이 활성화되지 않은 경우엔 무시.
        // (InGameHUD.ShowSurrenderConfirm 내부에서 한 번 더 가드)
        hud.ShowSurrenderConfirm();
    }

    private void HandleTouchAttackInput()
    {
        if (attackLocked) return;
        // MEDIUM-02: 씬 전환 직후 mainCam이 null일 수 있음 (정적 캐시가 이전 씬 카메라를 가리키다 파괴됨).
        if (mainCam == null) mainCam = Camera.main;
        if (mainCam == null) return;

#if UNITY_EDITOR || UNITY_STANDALONE
        // [디버그 / 에디터 테스트] 마우스 좌클릭을 터치로 시뮬레이트.
        // 모바일 빌드에서는 Touch 만 사용하지만, Unity 에디터에서는 마우스 클릭이 터치 이벤트로
        // 변환되지 않아 StandaloneTestBootstrap 으로 테스트 시 평타가 불가능했음.
        // EnhancedTouchSupport 와 무관하게 Mouse.current 가 동작하므로 안전.
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            Vector2 mouseScreen = mouse.position.ReadValue();
            var uiHit = ClassifyUIHit(mouseScreen);
            if (uiHit != UIHitKind.Blocking)
            {
                Vector2 mouseWorld = mainCam.ScreenToWorldPoint(mouseScreen);
                // 조이스틱 영역 위 클릭은 적이 있을 때만 평타로 잡고, 빈 공간이면 조이스틱에 양보.
                ProcessAttackClickAt(mouseWorld, clearOnMiss: uiHit != UIHitKind.JoystickOnly);
            }
        }
#endif

        // M-2: OnNetworkDespawn에서 EnhancedTouchSupport.Disable() 후 같은 프레임 Update에서
        // Touch.activeTouches 접근 시 InvalidOperationException 발생하던 버그.
        if (!UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.enabled) return;
        var activeTouches = UnityEngine.InputSystem.EnhancedTouch.Touch.activeTouches;
        if (activeTouches.Count == 0) return;

        foreach (var touch in activeTouches)
        {
            if (touch.phase != UnityEngine.InputSystem.TouchPhase.Began) continue;

            // [평타 우선] 조이스틱 raycast 영역(부모 RectTransform)이 화면 절반을 덮어
            // 그 안의 적을 탭으로 공격할 수 없던 문제를 해결.
            // - Blocking(버튼·HUD 등): 기존대로 평타 차단.
            // - JoystickOnly: 적이 있으면 평타 발동, 없으면 ClearTarget 하지 않고 조이스틱에 양보.
            // - None: 기존 동작.
            var uiHit = ClassifyUIHit(touch.screenPosition);
            if (uiHit == UIHitKind.Blocking) continue;

            Vector2 worldPos = mainCam.ScreenToWorldPoint(touch.screenPosition);
            ProcessAttackClickAt(worldPos, clearOnMiss: uiHit != UIHitKind.JoystickOnly);
            return; // 한 프레임에 한 터치만 처리
        }
    }

    /// <summary>
    /// 월드 좌표 클릭/터치를 평타 대상 지정 또는 타겟 해제로 변환.
    /// 반환: 클릭 지점에서 유효한 적을 찾아 SetAttackTarget 한 경우 true.
    /// clearOnMiss: false 이면 적이 없어도 기존 타겟/자동평타를 유지 (조이스틱 영역 탭에서 사용).
    /// </summary>
    private bool ProcessAttackClickAt(Vector2 worldPos, bool clearOnMiss = true)
    {
        // [버그 수정] PlayerCharacter 프리팹의 Enemy Layer 가 "Nothing" 으로 설정되어 있으면
        // OverlapPoint 가 아무것도 검출하지 못해 평타가 영원히 안 됨.
        // enemyLayer 가 비어있으면 AllLayers 로 폴백 → 클릭 지점의 모든 콜라이더 검사 후
        // PlayerController 컴포넌트 보유 여부로 적 판정 (안전).
        LayerMask effectiveMask = enemyLayer.value != 0 ? enemyLayer : (LayerMask)Physics2D.AllLayers;

        // 클릭 지점에 여러 콜라이더가 겹쳐있을 수 있어 OverlapPointAll 로 모두 검사.
        var hits = Physics2D.OverlapPointAll(worldPos, effectiveMask);
        foreach (var col in hits)
        {
            if (col == null) continue;
            var enemy = col.GetComponent<PlayerController>();
            if (enemy != null && !enemy.IsDead && enemy != this)
            {
                SetAttackTarget(enemy);
                return true;
            }
        }
        if (!clearOnMiss) return false;

        // 적이 없으면 자동 평타/추격 해제 (수동 취소)
        if (_autoAttackCoroutine != null)
        {
            StopCoroutine(_autoAttackCoroutine);
            _autoAttackCoroutine = null;
        }
        ClearTarget();
        return false;
    }

    // PointerEventData 기반 UI 히트 테스트 — New Input System과 호환
    private static readonly List<UnityEngine.EventSystems.RaycastResult> _uiRaycastResults
        = new List<UnityEngine.EventSystems.RaycastResult>();

    /// <summary>
    /// 터치/클릭 지점이 어떤 UI 위에 있는지 분류.
    /// - JoystickOnly: 조이스틱 raycast 영역만 걸린 경우(부모 RectTransform 포함). 평타 우선 허용.
    /// - Blocking: 버튼·HUD 등 비-조이스틱 UI가 하나라도 걸린 경우. 평타 차단.
    /// - None: UI 히트 없음.
    /// </summary>
    private enum UIHitKind { None, JoystickOnly, Blocking }

    private static UIHitKind ClassifyUIHit(Vector2 screenPos)
    {
        if (EventSystem.current == null) return UIHitKind.None;
        var pe = new PointerEventData(EventSystem.current) { position = screenPos };
        _uiRaycastResults.Clear();
        EventSystem.current.RaycastAll(pe, _uiRaycastResults);
        if (_uiRaycastResults.Count == 0) return UIHitKind.None;

        bool sawJoystick = false;
        foreach (var result in _uiRaycastResults)
        {
            if (result.gameObject == null) continue;
            // 조이스틱(부모 RectTransform 포함)은 평타 발동을 막지 않는다.
            if (result.gameObject.GetComponentInParent<Joystick>() != null)
            {
                sawJoystick = true;
                continue;
            }
            // 그 외 UI(버튼·스킬 슬롯·HUD 등)는 1개라도 있으면 평타 차단.
            return UIHitKind.Blocking;
        }
        return sawJoystick ? UIHitKind.JoystickOnly : UIHitKind.None;
    }

    // ════════════════════════════════════════════════════════════
    //  자동 평타 — 타겟 고정 후 쿨다운마다 자동 공격
    //  (적 터치 → 타겟 사망/은신/Despawn/null 시 자동 해제)
    // ════════════════════════════════════════════════════════════

    private void SetAttackTarget(PlayerController enemy)
    {
        // 동일 타겟 재클릭은 무시하되, 코루틴이 비정상 종료되어 null인 경우는 재시작 허용.
        if (targetEnemy == enemy && _autoAttackCoroutine != null) return;

        if (_autoAttackCoroutine != null)
        {
            StopCoroutine(_autoAttackCoroutine);
            _autoAttackCoroutine = null;
        }

        targetEnemy = enemy;
        isChasing   = false;
        _autoAttackCoroutine = StartCoroutine(AutoAttackRoutine(enemy));
    }

    /// <summary>이 거리를 넘어가면 자동 추격을 포기하고 타겟 해제. 사용자가 조이스틱 입력 없이도 끊을 수 있도록.</summary>
    private const float AutoChaseMaxDistance = 15f;

    private IEnumerator AutoAttackRoutine(PlayerController enemy)
    {
        while (true)
        {
            // 1) 타겟 유효성 검사
            // 조이스틱 입력/FixedUpdate 은신감지 등 외부 경로에서 targetEnemy를
            // null로 바꾼 경우, 로컬 파라미터 enemy는 여전히 살아있으므로
            // 필드 일치 여부로 외부 클리어를 감지해 종료한다.
            if (enemy == null || targetEnemy != enemy)
            {
                ClearTarget();
                yield break;
            }
            // Despawn된 타겟의 NetworkVariable 접근 시 NGO 예외 방지를 위해 IsSpawned 선행 체크.
            if (enemy.IsDead
                || (enemy.networkSync != null
                    && (!enemy.networkSync.IsSpawned || enemy.networkSync.NetworkIsDead.Value)))
            {
                ClearTarget();
                yield break;
            }
            if (enemy.StatusFX != null && enemy.StatusFX.IsStealthy)
            {
                ClearTarget();
                yield break;
            }

            // 2) 거리 계산
            float dist = Vector2.Distance(transform.position, enemy.transform.position);

            // [신규] 추격 거리 제한 — 너무 멀어지면 자동 포기.
            // 이전엔 일단 타겟이 잡히면 무한히 chase 가 활성화되어 사용자가 조이스틱 입력 없이
            // 빈 곳을 클릭해야만 끊을 수 있었음. 에디터 테스트 / 모바일 조작 실수 모두 대응.
            if (dist > AutoChaseMaxDistance)
            {
                ClearTarget();
                yield break;
            }

            // [신규] 카메라 시야 밖으로 타겟이 사라지면 자동 포기.
            // 거리 제한과 별개 — 거리상 가깝더라도 카메라가 다른 곳을 비추면 추격 중단.
            // 모바일 UX: 화면 밖 적은 시각 정보 없이 추격하면 길 잃은 듯한 느낌. 자연스러운 자동 해제.
            // IsLocalPlayer 만 적용 — 다른 클라이언트의 AutoAttackRoutine 은 자체 카메라 기준이 다름.
            if (IsLocalPlayer && IsOwner)
            {
                if (mainCam == null) mainCam = Camera.main;
                if (mainCam != null)
                {
                    Vector3 vp = mainCam.WorldToViewportPoint(enemy.transform.position);
                    // 약간의 여유(-0.05~1.05) — 화면 가장자리 깜빡임 방지.
                    bool offScreen = vp.z < 0f
                                     || vp.x < -0.05f || vp.x > 1.05f
                                     || vp.y < -0.05f || vp.y > 1.05f;
                    if (offScreen)
                    {
                        ClearTarget();
                        yield break;
                    }
                }
            }

            if (dist <= attackRange)
            {
                // 3) 사거리 내: 쿨다운 체크 후 공격
                isChasing = false;
                if (!attackLocked && Time.time - lastAttackTime >= AttackCooldown)
                {
                    lastAttackTime = Time.time;
                    var targetNetObj = enemy.GetComponent<NetworkObject>();
                    if (targetNetObj != null && networkSync != null)
                        networkSync.RequestAttackServerRpc(targetNetObj.NetworkObjectId);
                    if (animator != null) animator.SetTrigger("Attack");
                    AudioManager.Instance?.PlayAttackHit();
                }
            }
            else
            {
                // 4) 사거리 밖: 추격 (FixedUpdate가 이동 처리)
                isChasing = true;
            }

            yield return null;
        }
    }

    private void ClearTarget()
    {
        targetEnemy          = null;
        isChasing            = false;
        _autoAttackCoroutine = null;

        // [FIX] 공격 취소 시 Animator의 Attack/Skill 트리거를 즉시 소거.
        // 트리거는 set 된 후 다음 transition 평가 시 1회 소비되는데,
        // 코루틴만 멈추고 트리거를 남겨두면 마지막 set 트리거가 다음 프레임에 발화하여
        // "빈 공간 클릭으로 공격을 멈췄는데 한 번 더 공격 모션이 나옴" 증상이 발생.
        // ResetTrigger 는 미정의 파라미터에 호출해도 경고만 1회 출력되고 게임에 영향 없음.
        if (animator != null && animator.runtimeAnimatorController != null)
        {
            animator.ResetTrigger("Attack");
            animator.ResetTrigger("Skill");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 — 서버 RPC 요청
    // ════════════════════════════════════════════════════════════

    // [FIX] void → bool 반환으로 변경.
    // InGameHUD.OnSkillClicked에서 성공 여부와 무관하게 쿨다운 UI를 항상 시작하는 버그 수정.
    // IsSilenced(IceShield), IsDead 등으로 RPC가 차단된 경우 false를 반환해 쿨다운을 막음.
    public bool UseSkill(int slotIndex)
    {
#if UNITY_EDITOR
        // 스킬 사용 흐름 진단 — 어느 PlayerController 인스턴스에서 호출됐는지 instanceID 까지 출력.
        // skillCount=0 이면 더미가 localPlayer 로 잘못 등록된 것 → InGameHUD.InitPlayerUI 진입점 점검.
        int skillCount = myData?.activeSkills?.Count ?? -1;
        Debug.Log($"[PlayerController] UseSkill on {name} (instanceID={GetInstanceID()}, " +
                  $"_isBot={_isBot}, IsOwner={IsOwner}): slot={slotIndex}, IsDead={IsDead}, " +
                  $"myData={(myData != null ? "OK" : "NULL")}, skillCount={skillCount}, " +
                  $"silenced={StatusFX?.IsSilenced ?? false}");
#endif
        if (IsDead || myData == null || slotIndex >= myData.activeSkills.Count) return false;
        if (StatusFX.IsSilenced) return false; // 클라이언트 1차 검증

        Vector2 targetPos = Rb.position + GetFacingDirection() * 4f;
        if (targetEnemy != null && !targetEnemy.IsDead)
            targetPos = targetEnemy.Rb.position;

        // [버그 수정] 시전 시점의 바라보는 방향을 RPC 파라미터로 함께 전달.
        // GetFacingDirection()은 moveDir·spriteRenderer.flipX 를 읽는데,
        // 이 값들은 클라이언트 Update()에서만 갱신되므로 서버에서는 항상 Vector2.right.
        // ChargeStrike·Shockwave·Bulldozer·Shuriken·Sweep(Cone) 등
        // targetPos 없이 방향만으로 판정하는 스킬들이 항상 오른쪽으로 발동되는 버그.
        // → 클라이언트에서 정확한 방향을 캡처해 서버로 전달하여 해결.
        Vector2 facingDir = GetFacingDirection();

        networkSync.RequestUseSkillServerRpc(slotIndex, targetPos, facingDir);
        return true;
    }

    // ════════════════════════════════════════════════════════════
    //  방향 계산
    // ════════════════════════════════════════════════════════════

    public Vector2 GetFacingDirection()
    {
        if (moveDir.sqrMagnitude > 0.01f) return moveDir.normalized;
        if (targetEnemy != null && !targetEnemy.IsDead)
            return ((Vector2)targetEnemy.transform.position - Rb.position).normalized;
        if (spriteRenderer != null)
            return spriteRenderer.flipX ? Vector2.left : Vector2.right;
        return Vector2.right;
    }

    // ════════════════════════════════════════════════════════════
    //  상태 제어 (StatusEffectSystem / PlayerNetworkSync 에서 호출)
    // ════════════════════════════════════════════════════════════

    public void RecalculateMoveSpeed() { /* StatCalculator 연동 시 여기서 캐시 갱신 */ }

    // 서버에서 _serverData와 myData를 동일 객체로 연결하기 위해 사용
    public void SetMyData(CharacterData data) { myData = data; }

    public void SetMovementLocked(bool locked)
    {
        movementLocked = locked;
        if (locked) { moveDir = Vector2.zero; isChasing = false; }
    }

    public void SetAttackLocked(bool locked) => attackLocked = locked;

    public void SetStealth(bool active)
    {
        if (!active && myData != null) myData.stealthFirstAttack = false;
    }

    public void SetKillCount(int count) => killCount = count;

    // ════════════════════════════════════════════════════════════
    //  회복 (HealingLight, SnackTime 등 서버에서 호출)
    // ════════════════════════════════════════════════════════════

    /// <summary>서버 전용 회복 — networkSync.NetworkHp를 직접 수정합니다.</summary>
    public void HealServer(float amount)
    {
        if (networkSync == null || !networkSync.IsServer) return;
        float newHp = Mathf.Min(networkSync.NetworkHp.Value + amount, networkSync.NetworkMaxHp.Value);
        networkSync.NetworkHp.Value      = newHp;
        networkSync.ServerData.currentHp = newHp;
    }

    // ════════════════════════════════════════════════════════════
    //  시각 효과 — ClientRpc로 모든 클라이언트에서 호출됨
    // ════════════════════════════════════════════════════════════

    /// <summary>스킬 이펙트/애니메이션 재생 — BroadcastSkillVisualsClientRpc에서 호출</summary>
    public void PlaySkillVisuals(ActiveSkillType skill, Vector2 targetPos)
    {
        if (animator != null)
        {
            // [개선] 평타와 스킬을 동일한 "Attack" 트리거로 재생하면 직업별 고유 스킬 모션을
            // 추가하기 어렵다. "Skill" 트리거를 우선 시도하고, 컨트롤러에 "Skill" 파라미터가
            // 없거나 해당 직업이 스킬 전용 모션을 갖고 있지 않으면 자동으로 "Attack" 으로 폴백.
            // SetTrigger 자체는 미정의 파라미터에 호출해도 경고 1회만 발생하고 게임에 영향 없음.
            bool hasSkillParam = HasAnimatorParameter("Skill");
            if (hasSkillParam) animator.SetTrigger("Skill");
            else               animator.SetTrigger("Attack");

#if UNITY_EDITOR
            // 에디터에서 스킬 모션 디버깅 — 트리거 + 컨트롤러 + 현재 재생 상태까지 출력.
            // "스킬 모션 안 보임" 신고 시 어디서 끊겼는지 빠르게 식별.
            string ctrlName = animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.name
                : "null";
            string currentState = "?";
            if (animator.runtimeAnimatorController != null && animator.isInitialized)
            {
                var info = animator.GetCurrentAnimatorStateInfo(0);
                currentState = info.fullPathHash.ToString();
            }
            Debug.Log($"[PlayerController] PlaySkillVisuals: skill={skill}, " +
                      $"trigger={(hasSkillParam ? "Skill" : "Attack")}, " +
                      $"controller={ctrlName}, animatorEnabled={animator.enabled}, " +
                      $"stateHash={currentState}");
#endif
        }
        AudioManager.Instance?.PlaySkillSound(skill);
        // TODO: skill 타입별 파티클 Instantiate
    }

    /// <summary>현재 Animator Controller에 지정 파라미터가 존재하는지 안전하게 확인합니다.</summary>
    private bool HasAnimatorParameter(string name)
    {
        if (animator == null || animator.runtimeAnimatorController == null) return false;
        foreach (var p in animator.parameters)
            if (p.name == name) return true;
        return false;
    }

    /// <summary>
    /// 피격 모션 재생. NotifyHitClientRpc 에서 호출됨.
    /// 컨트롤러에 "Hurt" 트리거가 있을 때만 동작 — 미정의 직업은 무시.
    /// 사망(Die) 우선 / 회피·블록 시에는 호출하지 않음 (호출자 책임).
    /// </summary>
    public void PlayHurtAnimation()
    {
        if (IsDead || animator == null) return;
        if (!HasAnimatorParameter("Hurt")) return;
        animator.SetTrigger("Hurt");
    }

    public void ShowDotPopup(float dmg, Color color)
    {
        if (DamagePopupPool.Instance == null) return;
        DamagePopupPool.Instance.Spawn(transform.position + Vector3.up * 0.5f,
            dmg > 0f ? $"{dmg:0.#}" : "BLOCK", color);
    }

    public void ShowSkillDamagePopup(float dmg, Color color)
    {
        if (DamagePopupPool.Instance == null) return;
        DamagePopupPool.Instance.Spawn(transform.position + Vector3.up * 0.7f, $"{dmg:0.#}", color);
    }

    public void ShowSkillPopup(string text)
    {
        if (DamagePopupPool.Instance == null) return;
        DamagePopupPool.Instance.Spawn(transform.position + Vector3.up * 1f, text, Color.yellow);
    }

    public void ShowDamagePopupNetwork(DamageResult result)
    {
        if (DamagePopupPool.Instance == null) return;
        string text; Color color;
        if      (result.isEvaded)            { text = "MISS";                        color = Color.gray;             }
        else if (result.isDivineGraceBlocked){ text = "BLOCKED";                     color = Color.yellow;           }
        else if (result.isWorldCollapse)     { text = "COLLAPSE!";                   color = Color.magenta;          }
        else if (result.isLuckyStrike)       { text = $"{result.finalDamage:0.#}*";  color = new Color(1f,0.8f,0f); }
        else if (result.isCritical)          { text = $"{result.finalDamage:0.#}!";  color = Color.yellow; AudioManager.Instance?.PlayCritical(); }
        else                                 { text = $"{result.finalDamage:0.#}";   color = Color.white;           }
        DamagePopupPool.Instance.Spawn(transform.position + Vector3.up * 0.5f, text, color);
    }

    // ════════════════════════════════════════════════════════════
    //  사망 / 부활 — ClientRpc로 모든 클라이언트에서 호출됨
    // ════════════════════════════════════════════════════════════

    public void PlayDeathAnimation()
    {
        if (IsDead) return;
        IsDead         = true;
        isChasing      = false;
        targetEnemy    = null;
        movementLocked = true;

        // 사망 시 자동 평타 코루틴 정리 (PlayerVisibility/StatusFX null 접근 등 잔여 호출 방지).
        if (_autoAttackCoroutine != null)
        {
            StopCoroutine(_autoAttackCoroutine);
            _autoAttackCoroutine = null;
        }

        if (animator != null)
        {
            // [버그 수정] 치명타 1프레임에 NotifyHitClientRpc → DeclareDeathClientRpc 순서로 호출되어
            // Hurt/Die 트리거가 동시에 set 됨. AnyState 우선순위로 Die가 먼저 전이하더라도 남은 Hurt 트리거가
            // 다음 프레임에 발화하여 Archer_Hurt → (자동 ExitTime) → Idle 로 빠져나가 Die 애니메이션이 묻힘.
            // Die 직전에 Hurt 트리거를 명시적으로 소비하여 사망 애니메이션이 유지되도록 함.
            animator.ResetTrigger("Hurt");
            animator.SetTrigger("Die");
        }
        AudioManager.Instance?.PlayDeath();
        GetComponent<Collider2D>().enabled = false;
        if (Rb != null) Rb.simulated = false;

        if (spriteRenderer != null)
        {
            spriteRenderer.color        = new Color(0.6f, 0.6f, 0.6f, 1f);
            spriteRenderer.sortingOrder = -10;
        }

        // [FEATURE] 로컬 플레이어 사망 시 관전 모드 진입
        if (IsLocalPlayer)
            SpectatorManager.Instance?.EnterSpectator();
    }

    public void ReviveNetwork(Vector2 spawnPos = default)
    {
        if (!IsDead) return;
        IsDead         = false;
        movementLocked = false;
        attackLocked   = false;
        isChasing      = false;
        targetEnemy    = null;
        // [버그 수정 N-A] 부활 시 평타 쿨다운 리셋. 부활 직후 즉시 평타 가능하도록 함.
        lastAttackTime = -999f;

        // [버그 수정] 부활 시 스폰 위치 리셋 누락.
        // 서버 ExecuteReviveClientRpc가 전달한 spawnPos만 사용.
        // NetworkSpawnManager.Instance는 클라이언트에서 항상 null이므로 폴백 불가 — 0,0 고정 버그 방지.
        if (networkSync != null && Rb != null && spawnPos != default && spawnPos != Vector2.zero)
        {
            Rb.position = spawnPos;
        }

        if (animator != null)
        {
            animator.Rebind();
            animator.Update(0f);
        }
        AudioManager.Instance?.PlayRevive();
        GetComponent<Collider2D>().enabled = true;
        if (Rb != null) Rb.simulated = true;

        if (spriteRenderer != null)
        {
            spriteRenderer.color        = Color.white;
            spriteRenderer.sortingOrder = 0;
        }

        if (IsOwner && InGameHUD.Instance != null && myData != null)
            InGameHUD.Instance.UpdateHealthBar(myData.maxHp, myData.maxHp);
    }

    // ════════════════════════════════════════════════════════════
    //  애니메이션
    // ════════════════════════════════════════════════════════════

    private void UpdateAnimation()
    {
        // [버그 수정] 이전 코드는 `if (animator == null) return;` 으로 즉시 종료하여
        // animator 컴포넌트가 없는 캐릭터는 spriteRenderer.flipX 가 갱신되지 않아
        // 항상 오른쪽을 바라보는 버그가 있었음.
        // 코드 상단 line 217~222 주석에 "모든 플레이어에 대해 UpdateAnimation()을 호출.
        // flipX는 moveDir(Owner) 또는 networkSync.NetworkMoveDir(비Owner)을 기준으로 갱신."
        // 이라 명시되어 있어 의도와 불일치. animator 사용은 선택적으로 처리하고
        // flipX 갱신은 spriteRenderer 가 있으면 항상 실행되도록 변경.

        // 비Owner: networkSync를 통해 서버에서 동기화된 이동 방향을 사용
        Vector2 displayDir = IsOwner
            ? moveDir
            : (networkSync != null ? networkSync.NetworkMoveDir.Value : Vector2.zero);

        bool displayChasing = IsOwner ? isChasing : NetworkIsChasing.Value;

        // animator 는 선택적 — 없어도 flipX 는 계속 갱신되어야 함.
        if (animator != null)
            animator.SetBool("IsMoving", displayDir.sqrMagnitude > 0.01f || displayChasing);

        if (spriteRenderer == null) return;
        if      (displayDir.x > 0.05f)  spriteRenderer.flipX = false;
        else if (displayDir.x < -0.05f) spriteRenderer.flipX = true;
    }

    [ServerRpc]
    private void SetChasingServerRpc(bool chasing)
    {
        NetworkIsChasing.Value = chasing;
    }

    private void SyncChasingToServer(bool chasing)
    {
        if (IsOwner && IsSpawned) SetChasingServerRpc(chasing);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);
    }
}
