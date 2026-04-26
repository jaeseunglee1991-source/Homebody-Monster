using UnityEngine;
using UnityEngine.EventSystems;
using System.Collections;
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
    public DamagePopup damagePopupPrefab;
    public SpriteRenderer spriteRenderer;
    public Animator animator;

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
            // 시네머신 카메라 자동 연결 (3.x 버전 대응 강화)
            var vcam = FindFirstObjectByType<Unity.Cinemachine.CinemachineCamera>();
            if (vcam != null)
            {
                vcam.Follow = transform;
                Debug.Log($"[PlayerController] 🎥 시네머신 카메라 연결 성공: {vcam.name}");
            }
            else
            {
                Debug.LogWarning("[PlayerController] ⚠️ 씬에서 CinemachineCamera를 찾을 수 없습니다.");
            }

            // 동적 스폰 시 씬에 배치된 조이스틱을 자동으로 연결
            if (movementJoystick == null)
                movementJoystick = FindFirstObjectByType<VariableJoystick>();
        }
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
        if (IsDead || !IsOwner) return;
        HandleJoystickInput();
        HandleTouchAttackInput();
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
    }

    private void HandleTouchAttackInput()
    {
        if (attackLocked || Input.touchCount == 0) return;
        foreach (Touch touch in Input.touches)
        {
            if (touch.phase != TouchPhase.Began) continue;
            // UI(버튼, 조이스틱 등) 위를 터치했을 때 평타 오발 방지
            if (EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject(touch.fingerId)) continue;
            Vector2 worldPos = mainCam.ScreenToWorldPoint(touch.position);
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
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 — 서버 RPC 요청
    // ════════════════════════════════════════════════════════════

    public void UseSkill(int slotIndex)
    {
        if (IsDead || myData == null || slotIndex >= myData.activeSkills.Count) return;
        if (StatusFX.IsSilenced) return; // 클라이언트 1차 검증

        Vector2 targetPos = Rb.position + GetFacingDirection() * 4f;
        if (targetEnemy != null && !targetEnemy.IsDead)
            targetPos = targetEnemy.Rb.position;

        networkSync.RequestUseSkillServerRpc(slotIndex, targetPos);
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
        // TODO: skill 타입별 파티클 Instantiate
        // 예: if (skill == ActiveSkillType.Fireball) Instantiate(fireballVfxPrefab, transform.position, Quaternion.identity);
    }

    public void ShowDotPopup(float dmg, Color color)
    {
        if (damagePopupPrefab == null) return;
        var popup = Instantiate(damagePopupPrefab, transform.position + Vector3.up * 0.5f, Quaternion.identity);
        popup.Setup(dmg > 0f ? $"{dmg:0.#}" : "BLOCK", color);
    }

    public void ShowSkillDamagePopup(float dmg, Color color)
    {
        if (damagePopupPrefab == null) return;
        var popup = Instantiate(damagePopupPrefab, transform.position + Vector3.up * 0.7f, Quaternion.identity);
        popup.Setup($"{dmg:0.#}", color);
    }

    public void ShowSkillPopup(string text)
    {
        if (damagePopupPrefab == null) return;
        var popup = Instantiate(damagePopupPrefab, transform.position + Vector3.up * 1f, Quaternion.identity);
        popup.Setup(text, Color.yellow);
    }

    public void ShowDamagePopupNetwork(DamageResult result)
    {
        if (damagePopupPrefab == null) return;
        string text; Color color;
        if      (result.isEvaded)            { text = "MISS";             color = Color.gray;                  }
        else if (result.isDivineGraceBlocked){ text = "BLOCKED";          color = Color.yellow;                }
        else if (result.isWorldCollapse)     { text = "COLLAPSE!";        color = Color.magenta;               }
        else if (result.isLuckyStrike)       { text = $"{result.finalDamage:0.#}*"; color = new Color(1f,0.8f,0f); }
        else if (result.isCritical)          { text = $"{result.finalDamage:0.#}!"; color = Color.yellow;      }
        else                                 { text = $"{result.finalDamage:0.#}";  color = Color.white;       }
        var popup = Instantiate(damagePopupPrefab, transform.position + Vector3.up * 0.5f, Quaternion.identity);
        popup.Setup(text, color);
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
        GetComponent<Collider2D>().enabled = false;
        if (Rb != null) Rb.simulated = false;

        if (spriteRenderer != null)
        {
            spriteRenderer.color        = new Color(0.6f, 0.6f, 0.6f, 1f);
            spriteRenderer.sortingOrder = -10;
        }
    }

    public void ReviveNetwork()
    {
        if (!IsDead) return;
        IsDead         = false;
        movementLocked = false;
        isChasing      = false;
        targetEnemy    = null;

        if (animator != null)
        {
            animator.Rebind();
            animator.Update(0f);
        }

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
        animator.SetBool("IsMoving", moveDir.sqrMagnitude > 0.01f || isChasing);
        if (spriteRenderer == null) return;
        if      (moveDir.x > 0.05f)  spriteRenderer.flipX = false;
        else if (moveDir.x < -0.05f) spriteRenderer.flipX = true;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);
    }
}
