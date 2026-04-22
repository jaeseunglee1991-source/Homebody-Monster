using UnityEngine;
using System.Collections;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
[RequireComponent(typeof(StatusEffectSystem))]
public class PlayerController : MonoBehaviour
{
    [Header("Character Data")] public CharacterData myData;
    [Header("Input")] public VariableJoystick movementJoystick;
    [Header("Combat Settings")]
    public float attackRange = 1.8f;
    public float attackCooldown = 0.8f;
    public LayerMask enemyLayer;
    [Header("Visual")]
    public DamagePopup damagePopupPrefab;
    public SpriteRenderer spriteRenderer;
    public Animator animator;

    public bool IsLocalPlayer { get; set; } = true;
    public int  killCount     { get; private set; } = 0;
    public bool IsDead        { get; private set; } = false;
    public Rigidbody2D        Rb       { get; private set; }
    public StatusEffectSystem StatusFX { get; private set; }

    private Vector2 moveDir;
    private PlayerController targetEnemy;
    private float lastAttackTime = -999f;
    private bool  isChasing      = false;
    private bool  movementLocked = false;
    private bool  attackLocked   = false;
    private static Camera mainCam;

    private void Awake()
    {
        Rb = GetComponent<Rigidbody2D>(); StatusFX = GetComponent<StatusEffectSystem>();
        Rb.gravityScale = 0f; Rb.freezeRotation = true;
        if (mainCam == null) mainCam = Camera.main;
    }

    private void Start()
    {
        if (myData == null && GameManager.Instance?.myCharacterData != null) myData = GameManager.Instance.myCharacterData;
        if (myData != null) StartCoroutine(CombatSystem.RegenerationRoutine(myData, () => IsDead));
        InGameManager.Instance?.RegisterPlayer(this);
        if (IsLocalPlayer && InGameHUD.Instance != null && myData != null)
            InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);
    }

    private void Update()
    {
        if (IsDead || !IsLocalPlayer) return;
        HandleJoystickInput(); HandleTouchAttackInput(); UpdateAnimation();
    }

    private void FixedUpdate()
    {
        if (IsDead || !IsLocalPlayer || movementLocked) return;
        float spd = StatCalculator.GetEffectiveMoveSpeed(myData, StatusFX);
        if (isChasing && targetEnemy != null && !targetEnemy.IsDead)
        { Vector2 dir = ((Vector2)targetEnemy.transform.position - Rb.position).normalized; Rb.MovePosition(Rb.position + dir * spd * Time.fixedDeltaTime); }
        else { Rb.MovePosition(Rb.position + moveDir * spd * Time.fixedDeltaTime); }
    }

    private void HandleJoystickInput()
    {
        if (movementJoystick == null) return;
        moveDir.x = movementJoystick.Horizontal; moveDir.y = movementJoystick.Vertical;
        if (moveDir.sqrMagnitude > 0.01f) { isChasing = false; targetEnemy = null; }
    }

    private void HandleTouchAttackInput()
    {
        if (attackLocked || Input.touchCount == 0) return;
        foreach (Touch touch in Input.touches)
        {
            if (touch.phase != TouchPhase.Began) continue;
            Vector2 worldPos = mainCam.ScreenToWorldPoint(touch.position);
            var col = Physics2D.OverlapPoint(worldPos, enemyLayer);
            if (col != null) { var enemy = col.GetComponent<PlayerController>(); if (enemy != null && !enemy.IsDead && enemy != this) { SetAttackTarget(enemy); return; } }
        }
    }

    private void SetAttackTarget(PlayerController enemy)
    {
        targetEnemy = enemy;
        if (Vector2.Distance(transform.position, enemy.transform.position) <= attackRange) TryAttack(enemy);
        else { isChasing = true; StartCoroutine(ChaseAndAttack(enemy)); }
    }

    private IEnumerator ChaseAndAttack(PlayerController enemy)
    {
        while (enemy != null && !enemy.IsDead && isChasing)
        { if (Vector2.Distance(transform.position, enemy.transform.position) <= attackRange) { isChasing = false; TryAttack(enemy); yield break; } yield return null; }
        isChasing = false;
    }

    private void TryAttack(PlayerController enemy)
    {
        if (attackLocked || Time.time - lastAttackTime < attackCooldown || enemy == null || enemy.IsDead || myData == null) return;
        if (StatusFX.IsStealthy) StatusFX.RemoveEffect(StatusEffectType.Stealth);
        lastAttackTime = Time.time; myData.lastCombatTime = Time.time;
        DamageResult result = CombatSystem.CalculateDamage(myData, enemy.myData, StatusFX, enemy.StatusFX);
        if (!result.isEvaded && !result.isDivineGraceBlocked && result.finalDamage > 0f)
        {
            if (myData.stealthFirstAttack) { result = new DamageResult { finalDamage = result.finalDamage + 2f, isCritical = result.isCritical }; myData.stealthFirstAttack = false; }
            CombatSystem.PostDamageEffects(myData, enemy.myData, StatusFX, enemy.StatusFX, result.finalDamage);
        }
        enemy.TakeDamage(result, this);
        if (animator != null) animator.SetTrigger("Attack");
    }

    public void TakeDamage(DamageResult result, PlayerController attacker)
    {
        if (IsDead || myData == null) return;
        myData.lastCombatTime = Time.time;
        if (!result.isEvaded && !result.isDivineGraceBlocked) myData.currentHp -= result.finalDamage;
        ShowDamagePopupResult(result);
        if (IsLocalPlayer && InGameHUD.Instance != null) InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);
        CheckDeath(attacker);
    }

    public void TakeSkillDeath(PlayerController attacker) => CheckDeath(attacker);
    public void TakeDotDeath(PlayerController attacker)   => CheckDeath(attacker);

    public void TakeTrapDamage(float amount, PlayerController attacker)
    {
        if (IsDead || myData == null) return;
        myData.currentHp = Mathf.Max(myData.currentHp - amount, 0f);
        ShowDotPopup(amount, Color.yellow);
        if (IsLocalPlayer && InGameHUD.Instance != null) InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);
        CheckDeath(attacker);
    }

    private void CheckDeath(PlayerController killer)
    {
        if (IsDead || myData == null || myData.currentHp > 0f) return;
        if (CombatSystem.TryGuardianAngel(myData, StatusFX)) { if (IsLocalPlayer && InGameHUD.Instance != null) InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp); return; }
        if (CombatSystem.TryTenacity(myData, StatusFX))      { if (IsLocalPlayer && InGameHUD.Instance != null) InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp); return; }
        Die(killer);
    }

    public void Heal(float amount)
    {
        if (IsDead || myData == null) return;
        myData.currentHp = Mathf.Min(myData.currentHp + amount, myData.maxHp);
        if (IsLocalPlayer && InGameHUD.Instance != null) InGameHUD.Instance.UpdateHealthBar(myData.currentHp, myData.maxHp);
    }

    public void UseSkill(int slotIndex)
    {
        if (IsDead || myData == null || slotIndex >= myData.activeSkills.Count) return;
        ActiveSkillType skill = myData.activeSkills[slotIndex];
        Vector2 targetPos = Rb.position + GetFacingDirection() * 4f;
        if (targetEnemy != null && !targetEnemy.IsDead) targetPos = targetEnemy.Rb.position;
        SkillSystem.ActivateSkill(skill, this, targetPos);
    }

    public Vector2 GetFacingDirection()
    {
        if (moveDir.sqrMagnitude > 0.01f) return moveDir.normalized;
        if (targetEnemy != null && !targetEnemy.IsDead) return ((Vector2)targetEnemy.transform.position - Rb.position).normalized;
        if (spriteRenderer != null) return spriteRenderer.flipX ? Vector2.left : Vector2.right;
        return Vector2.right;
    }

    public void RecalculateMoveSpeed() { }
    public void SetMovementLocked(bool locked) { movementLocked = locked; if (locked) { moveDir = Vector2.zero; isChasing = false; } }
    public void SetAttackLocked(bool locked)   => attackLocked = locked;
    public void SetStealth(bool active)        { if (!active) myData.stealthFirstAttack = false; }

    private void Die(PlayerController killer)
    {
        if (IsDead) return;
        IsDead = true; isChasing = false; targetEnemy = null; movementLocked = true;
        if (killer != null) { killer.killCount++; Debug.Log($"[Combat] {killer.myData?.playerName} -> {myData?.playerName} 처치! (킬:{killer.killCount})"); }
        if (animator != null) animator.SetTrigger("Die");
        GetComponent<Collider2D>().enabled = false; Rb.simulated = false;
        InGameManager.Instance?.OnPlayerDied(this);
        StartCoroutine(DisableAfterDelay(2f));
    }

    private IEnumerator DisableAfterDelay(float delay) { yield return new WaitForSeconds(delay); gameObject.SetActive(false); }

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

    private void ShowDamagePopupResult(DamageResult result)
    {
        if (damagePopupPrefab == null) return;
        string text; Color color;
        if (result.isEvaded)              { text = "MISS";      color = Color.gray;    }
        else if (result.isDivineGraceBlocked) { text = "BLOCKED"; color = Color.yellow; }
        else if (result.isWorldCollapse)  { text = "COLLAPSE!"; color = Color.magenta; }
        else if (result.isLuckyStrike)    { text = $"{result.finalDamage:0.#}*"; color = new Color(1f, 0.8f, 0f); }
        else if (result.isCritical)       { text = $"{result.finalDamage:0.#}!"; color = Color.yellow; }
        else                              { text = $"{result.finalDamage:0.#}";  color = Color.white;  }
        var popup = Instantiate(damagePopupPrefab, transform.position + Vector3.up * 0.5f, Quaternion.identity);
        popup.Setup(text, color);
    }

    private void UpdateAnimation()
    {
        if (animator == null) return;
        animator.SetBool("IsMoving", moveDir.sqrMagnitude > 0.01f || isChasing);
        if (spriteRenderer != null)
        { if (moveDir.x > 0.05f) spriteRenderer.flipX = false; else if (moveDir.x < -0.05f) spriteRenderer.flipX = true; }
    }

    private void OnDrawGizmosSelected() { Gizmos.color = Color.red; Gizmos.DrawWireSphere(transform.position, attackRange); }
}