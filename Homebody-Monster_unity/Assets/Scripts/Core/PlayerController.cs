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
    public float attackCooldown = 0.8f;
    public LayerMask enemyLayer;
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

    private Vector2          moveDir;
    private PlayerController targetEnemy;
    private float            lastAttackTime = -999f;
    private bool             isChasing      = false;
    private bool             movementLocked = false;
    private bool             attackLocked   = false;
    private static Camera    mainCam;

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
            if (movementJoystick == null)
                movementJoystick = FindFirstObjectByType<VariableJoystick>();
        }
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        if (IsOwner)
            UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.Disable();
    }

    private void Start()
    {
        // 프리팹의 기본값을 무시하고 GameManager의 실제 캐릭터 데이터로 덮어쓰기
        if (GameManager.Instance?.myCharacterData != null)
            myData = GameManager.Instance.myCharacterData;

        // 등록은 PlayerNetworkSync.OnNetworkSpawn에서 수행하므로 중복 제거
        // (원본에서는 Start에서도 호출했으나 NGO 구조에서는 OnNetworkSpawn이 기준)

        if (IsOwner && InGameHUD.Instance != null && myData != null)
        {
            InGameHUD.Instance.InitPlayerUI(this);
            InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);
        }
    }

    private void Update()
    {
        if (IsDead) return;

        if (IsOwner)
        {
            HandleJoystickInput();
            HandleTouchAttackInput();
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
        if (IsDead || !IsOwner) return;

        if (movementLocked)
        {
            Rb.linearVelocity = Vector2.zero;
            return;
        }

        float spd = StatCalculator.GetEffectiveMoveSpeed(myData, StatusFX);
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

    private void HandleJoystickInput()
    {
        if (movementJoystick == null) return;
        moveDir.x = movementJoystick.Horizontal;
        moveDir.y = movementJoystick.Vertical;
        if (moveDir.sqrMagnitude > 0.01f) { isChasing = false; targetEnemy = null; }

        // [FIX] 이동 방향을 NetworkVariable에 동기화하여 다른 클라이언트에서 flipX 갱신
        if (networkSync != null && networkSync.IsOwner &&
            Vector2.Distance(networkSync.NetworkMoveDir.Value, moveDir) > 0.05f)
            networkSync.NetworkMoveDir.Value = moveDir;
    }

    private void HandleTouchAttackInput()
    {
        var activeTouches = UnityEngine.InputSystem.EnhancedTouch.Touch.activeTouches;
        if (attackLocked || activeTouches.Count == 0) return;

        foreach (var touch in activeTouches)
        {
            if (touch.phase != UnityEngine.InputSystem.TouchPhase.Began) continue;
            // UI(버튼, 조이스틱 등) 위를 터치했을 때 평타 오발 방지
            if (IsTouchOverUI(touch.screenPosition)) continue;

            Vector2 worldPos = mainCam.ScreenToWorldPoint(touch.screenPosition);
            var col = Physics2D.OverlapPoint(worldPos, enemyLayer);
            if (col != null)
            {
                var enemy = col.GetComponent<PlayerController>();
                if (enemy != null && !enemy.IsDead && enemy != this)
                {
                    SetAttackTarget(enemy);
                    return;
                }
            }
        }
    }

    // PointerEventData 기반 UI 히트 테스트 — New Input System과 호환
    private static readonly List<UnityEngine.EventSystems.RaycastResult> _uiRaycastResults
        = new List<UnityEngine.EventSystems.RaycastResult>();

    private static bool IsTouchOverUI(Vector2 screenPos)
    {
        if (EventSystem.current == null) return false;
        var pe = new PointerEventData(EventSystem.current) { position = screenPos };
        _uiRaycastResults.Clear();
        EventSystem.current.RaycastAll(pe, _uiRaycastResults);
        return _uiRaycastResults.Count > 0;
    }

    private void SetAttackTarget(PlayerController enemy)
    {
        targetEnemy = enemy;
        if (Vector2.Distance(transform.position, enemy.transform.position) <= attackRange)
            TryAttack(enemy);
        else
        {
            isChasing = true;
            StartCoroutine(ChaseAndAttack(enemy));
        }
    }

    private IEnumerator ChaseAndAttack(PlayerController enemy)
    {
        while (enemy != null && !enemy.IsDead && isChasing)
        {
            if (Vector2.Distance(transform.position, enemy.transform.position) <= attackRange)
            {
                isChasing = false;
                TryAttack(enemy);
                yield break;
            }
            yield return null;
        }
        isChasing = false;
    }

    // ════════════════════════════════════════════════════════════
    //  평타 — 서버 RPC 요청
    // ════════════════════════════════════════════════════════════

    private void TryAttack(PlayerController enemy)
    {
        if (attackLocked || Time.time - lastAttackTime < attackCooldown
            || enemy == null || enemy.IsDead) return;

        lastAttackTime = Time.time;

        var targetNetObj = enemy.GetComponent<NetworkObject>();
        if (targetNetObj != null)
            networkSync.RequestAttackServerRpc(targetNetObj.NetworkObjectId);

        if (animator != null) animator.SetTrigger("Attack");
        AudioManager.Instance?.PlayAttackHit();
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 — 서버 RPC 요청
    // ════════════════════════════════════════════════════════════

    // [FIX] void → bool 반환으로 변경.
    // InGameHUD.OnSkillClicked에서 성공 여부와 무관하게 쿨다운 UI를 항상 시작하는 버그 수정.
    // IsSilenced(IceShield), IsDead 등으로 RPC가 차단된 경우 false를 반환해 쿨다운을 막음.
    public bool UseSkill(int slotIndex)
    {
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
        if (animator != null) animator.SetTrigger("Attack");
        AudioManager.Instance?.PlaySkillSound(skill);
        // TODO: skill 타입별 파티클 Instantiate
        // 예: if (skill == ActiveSkillType.Fireball) Instantiate(fireballVfxPrefab, transform.position, Quaternion.identity);
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

        if (animator != null) animator.SetTrigger("Die");
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

    public void ReviveNetwork()
    {
        if (!IsDead) return;
        IsDead         = false;
        movementLocked = false;
        attackLocked   = false;
        isChasing      = false;
        targetEnemy    = null;

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
        if (animator == null) return;

        // 비Owner: networkSync를 통해 서버에서 동기화된 이동 방향을 사용
        Vector2 displayDir = IsOwner
            ? moveDir
            : (networkSync != null ? networkSync.NetworkMoveDir.Value : Vector2.zero);

        animator.SetBool("IsMoving", displayDir.sqrMagnitude > 0.01f || isChasing);
        if (spriteRenderer == null) return;
        if      (displayDir.x > 0.05f)  spriteRenderer.flipX = false;
        else if (displayDir.x < -0.05f) spriteRenderer.flipX = true;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);
    }
}
