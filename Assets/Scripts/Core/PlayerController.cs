using UnityEngine;
using System.Collections;

/// <summary>
/// 플레이어 캐릭터를 제어합니다.
/// - 조이스틱 이동
/// - 적 터치 시 자동 공격 (범위 내 즉시 / 범위 밖 추격 후 공격)
/// - 사망 처리 및 킬 카운트
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class PlayerController : MonoBehaviour
{
    // ── 외부 참조 ──────────────────────────────────────────────
    [Header("Character Data")]
    public CharacterData myData;

    [Header("Input (Inspector에서 할당)")]
    public VariableJoystick movementJoystick;

    [Header("Combat Settings")]
    [Tooltip("기본 공격 사거리 (유닛)")]
    public float attackRange = 1.8f;
    [Tooltip("공격 쿨다운 (초)")]
    public float attackCooldown = 0.8f;
    [Tooltip("적 레이어 마스크")]
    public LayerMask enemyLayer;
    [Tooltip("데미지 팝업 프리팹")]
    public DamagePopup damagePopupPrefab;

    [Header("Visual")]
    public SpriteRenderer spriteRenderer;
    public Animator animator;

    // ── 공개 프로퍼티 ──────────────────────────────────────────
    /// <summary>이 PlayerController가 로컬 플레이어인지 여부 (멀티플레이어 분기용)</summary>
    public bool IsLocalPlayer { get; set; } = true; // Photon 연동 시 photonView.IsMine으로 교체

    /// <summary>이 플레이어가 잡은 적 수</summary>
    public int killCount { get; private set; } = 0;

    public bool IsDead { get; private set; } = false;

    // ── 내부 상태 ──────────────────────────────────────────────
    private Rigidbody2D rb;
    private Vector2 moveDir;
    private PlayerController targetEnemy;     // 현재 추격/공격 대상
    private float lastAttackTime = -999f;
    private bool isChasing = false;           // 적을 향해 이동 중 여부
    private static Camera mainCam;

    // ── Unity 생명주기 ─────────────────────────────────────────
    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        rb.gravityScale = 0f;
        rb.freezeRotation = true;
        if (mainCam == null) mainCam = Camera.main;
    }

    private void Start()
    {
        // 씬 진입 시 GameManager에서 캐릭터 데이터 받아오기
        if (myData == null && GameManager.Instance?.myCharacterData != null)
            myData = GameManager.Instance.myCharacterData;

        // InGameManager에 자신을 등록
        InGameManager.Instance?.RegisterPlayer(this);

        // 초기 인터페이스 업데이트
        if (IsLocalPlayer && InGameHUD.Instance != null && myData != null)
            InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);
    }

    private void Update()
    {
        if (IsDead) return;

        // 로컬 플레이어만 입력 처리
        if (!IsLocalPlayer) return;

        HandleJoystickInput();
        HandleTouchAttackInput();
        UpdateAnimation();
    }

    private void FixedUpdate()
    {
        if (IsDead || !IsLocalPlayer) return;

        if (isChasing && targetEnemy != null && !targetEnemy.IsDead)
        {
            // 적 추격 이동
            Vector2 dir = ((Vector2)targetEnemy.transform.position - rb.position).normalized;
            rb.MovePosition(rb.position + dir * myData.moveSpeed * Time.fixedDeltaTime);
        }
        else
        {
            // 조이스틱 이동
            rb.MovePosition(rb.position + moveDir * myData.moveSpeed * Time.fixedDeltaTime);
        }
    }

    // ── 조이스틱 입력 ─────────────────────────────────────────
    private void HandleJoystickInput()
    {
        if (movementJoystick == null) return;

        moveDir.x = movementJoystick.Horizontal;
        moveDir.y = movementJoystick.Vertical;

        // 조이스틱 조작 시 추격 취소
        if (moveDir.sqrMagnitude > 0.01f)
        {
            isChasing = false;
            targetEnemy = null;
        }
    }

    // ── 터치 공격 입력 ─────────────────────────────────────────
    private void HandleTouchAttackInput()
    {
        if (Input.touchCount == 0) return;

        foreach (Touch touch in Input.touches)
        {
            if (touch.phase != TouchPhase.Began) continue;

            // 터치 위치를 월드 좌표로 변환
            Vector2 worldPos = mainCam.ScreenToWorldPoint(touch.position);

            // 터치 지점에 적이 있는지 확인
            Collider2D hit = Physics2D.OverlapPoint(worldPos, enemyLayer);
            if (hit != null)
            {
                PlayerController enemy = hit.GetComponent<PlayerController>();
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
        float dist = Vector2.Distance(transform.position, enemy.transform.position);

        if (dist <= attackRange)
        {
            TryAttack(enemy);
        }
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
            float dist = Vector2.Distance(transform.position, enemy.transform.position);
            if (dist <= attackRange)
            {
                isChasing = false;
                TryAttack(enemy);
                yield break;
            }
            yield return null; 
        }
        isChasing = false;
    }

    // ── 공격 실행 ──────────────────────────────────────────────
    private void TryAttack(PlayerController enemy)
    {
        if (Time.time - lastAttackTime < attackCooldown) return;
        if (enemy == null || enemy.IsDead) return;

        lastAttackTime = Time.time;

        // 전투 시스템에 데미지 계산 위임
        DamageResult result = CombatSystem.CalculateDamage(myData, enemy.myData);

        // 실제 데미지 적용
        enemy.TakeDamage(result, this);

        // 공격 애니메이션
        if (animator != null) animator.SetTrigger("Attack");

        Debug.Log($"[Combat] {myData.playerName} → {enemy.myData.playerName} | 데미지: {result.finalDamage} | 회피: {result.isEvaded}");
    }

    // ── 피격 처리 ──────────────────────────────────────────────
    public void TakeDamage(DamageResult result, PlayerController attacker)
    {
        if (IsDead || myData == null) return;

        // Lifesteal 처리: 공격자 회복
        if (!result.isEvaded && attacker != null && attacker.myData.skills.Contains(SkillType.Lifesteal))
        {
            float healAmount = result.finalDamage * 0.2f;
            attacker.Heal(healAmount);
        }

        // HP 감소
        if (!result.isEvaded)
            myData.currentHp -= result.finalDamage;

        // 데미지 팝업 생성
        ShowDamagePopup(result);

        // 체력 UI 업데이트
        if (IsLocalPlayer && InGameHUD.Instance != null)
            InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);

        // 사망 판정
        if (myData.currentHp <= 0f)
            Die(attacker);
    }

    public void Heal(float amount)
    {
        if (IsDead || myData == null) return;
        myData.currentHp = Mathf.Min(myData.currentHp + amount, myData.maxHp);
        if (IsLocalPlayer && InGameHUD.Instance != null)
            InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);
    }

    // ── 사망 처리 ──────────────────────────────────────────────
    private void Die(PlayerController killer)
    {
        if (IsDead) return;
        IsDead = true;
        isChasing = false;
        targetEnemy = null;

        if (killer != null)
        {
            killer.killCount++;
            Debug.Log($"[Combat] {killer.myData?.playerName}이(가) {myData?.playerName}을(를) 처치! (킬 수: {killer.killCount})");
        }

        if (animator != null) animator.SetTrigger("Die");

        GetComponent<Collider2D>().enabled = false;
        rb.simulated = false;

        InGameManager.Instance?.OnPlayerDied(this);
        StartCoroutine(DisableAfterDelay(2f));

        Debug.Log($"[PlayerController] {myData?.playerName} 사망");
    }

    private IEnumerator DisableAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        gameObject.SetActive(false);
    }

    public void UseSkill(int slotIndex)
    {
        if (IsDead || myData == null) return;
        if (slotIndex >= myData.skills.Count) return;

        SkillType skill = myData.skills[slotIndex];
        SkillSystem.ActivateSkill(skill, this);
    }

    // ── 시각 / 유틸 ──────────────────────────────────────────
    private void UpdateAnimation()
    {
        if (animator == null) return;

        bool moving = moveDir.sqrMagnitude > 0.01f || isChasing;
        animator.SetBool("IsMoving", moving);

        if (spriteRenderer != null)
        {
            if (moveDir.x > 0.05f) spriteRenderer.flipX = false;
            else if (moveDir.x < -0.05f) spriteRenderer.flipX = true;
        }
    }

    private void ShowDamagePopup(DamageResult result)
    {
        if (damagePopupPrefab == null) return;

        string text;
        Color color;

        if (result.isEvaded) { text = "MISS"; color = Color.gray; }
        else if (result.isWorldCollapse) { text = "COLLAPSE!"; color = Color.magenta; }
        else if (result.isCritical) { text = $"{result.finalDamage:0.#}!"; color = Color.yellow; }
        else { text = $"{result.finalDamage:0.#}"; color = Color.white; }

        DamagePopup popup = Instantiate(damagePopupPrefab, transform.position + Vector3.up * 0.5f, Quaternion.identity);
        popup.Setup(text, color);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);
    }
}
