using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 인게임 HUD 전체 관리.
///
/// 변경 사항:
///  - reviveInfoText 필드 추가: 부활 UI에 제한 사유 표시
///    (60초 초과, 생존자 2명 이하, 매치 횟수 소진 등)
///  - ShowReviveUI()에서 현재 매치 상태에 맞는 안내 문구 표시
///  - ReviveDeniedClientRpc 수신 시 HideReviveUI() 호출 (PlayerNetworkSync에서 직접 처리)
///  - GetSkillShortName 전체 40종 포함 유지
/// </summary>
public class InGameHUD : MonoBehaviour
{
    public static InGameHUD Instance { get; private set; }

    [Header("체력 UI")]
    public Slider healthSlider;
    public TextMeshProUGUI   healthText;

    [Header("생존자 UI")]
    public TextMeshProUGUI survivorCountText;

    [Header("스킬 버튼 (최대 4개)")]
    public Button[] skillButtons;
    public Image[]  skillCooldownFills;
    public TextMeshProUGUI[]   skillNameTexts;

    [Header("타이머")]
    public TextMeshProUGUI timerText;

    [Header("게임 종료 배너")]
    public GameObject endBannerPanel;
    public TextMeshProUGUI       endBannerText;

    [Header("부활 UI")]
    public GameObject      revivePanel;
    public TextMeshProUGUI            reviveTimerText;
    /// <summary>
    /// 부활 가능 여부 및 제한 사유를 표시하는 텍스트.
    /// Inspector에서 RevivePanel 하위에 배치하세요.
    /// 예: "남은 부활 횟수: 2/3  |  60초 제한 내 사용 가능"
    /// </summary>
    public TextMeshProUGUI            reviveInfoText;
    public Button          reviveButton;
    public Button          giveUpButton;

    private PlayerController    localPlayer;
    private float[]             cooldownTimers;
    private float[]             cooldownMaxes;

    private PlayerNetworkSync   pendingReviveSync;
    private Coroutine           reviveCountdownRoutine;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        if (endBannerPanel != null) endBannerPanel.SetActive(false);
        if (revivePanel    != null) revivePanel.SetActive(false);
    }

    public void InitPlayerUI(PlayerController player)
    {
        localPlayer = player;
        SetupSkillButtons(player);
    }

    private void Update() => UpdateCooldownUI();

    // ════════════════════════════════════════════════════════════
    //  스킬 버튼 초기화
    // ════════════════════════════════════════════════════════════

    private void SetupSkillButtons(PlayerController player)
    {
        if (player?.myData == null) return;
        List<ActiveSkillType> skills = player.myData.activeSkills;
        int count = Mathf.Min(skills.Count, skillButtons?.Length ?? 0);
        cooldownTimers = new float[skillButtons.Length];
        cooldownMaxes  = new float[skillButtons.Length];

        for (int i = 0; i < skillButtons.Length; i++)
        {
            if (skillButtons[i] == null) continue;
            if (i < count)
            {
                ActiveSkillType skill = skills[i];
                int captured = i;
                skillButtons[i].gameObject.SetActive(true);
                skillButtons[i].onClick.RemoveAllListeners();
                skillButtons[i].onClick.AddListener(() => OnSkillClicked(captured));
                if (skillNameTexts != null && i < skillNameTexts.Length && skillNameTexts[i] != null)
                    skillNameTexts[i].text = GetSkillShortName(skill);
                float cd = SkillSystem.GetCooldown(skill);
                cooldownMaxes[i]  = cd;
                cooldownTimers[i] = 0f;
                if (skillCooldownFills != null && i < skillCooldownFills.Length && skillCooldownFills[i] != null)
                    skillCooldownFills[i].fillAmount = 0f;
            }
            else
            {
                skillButtons[i].gameObject.SetActive(false);
            }
        }
    }

    private void OnSkillClicked(int slot)
    {
        if (localPlayer == null || cooldownTimers == null ||
            slot >= cooldownTimers.Length || cooldownTimers[slot] > 0f) return;
        localPlayer.UseSkill(slot);
        cooldownTimers[slot] = cooldownMaxes[slot];
    }

    private void UpdateCooldownUI()
    {
        if (cooldownTimers == null) return;
        for (int i = 0; i < cooldownTimers.Length; i++)
        {
            if (cooldownTimers[i] > 0f)
                cooldownTimers[i] = Mathf.Max(0f, cooldownTimers[i] - Time.deltaTime);
            if (skillCooldownFills != null && i < skillCooldownFills.Length && skillCooldownFills[i] != null)
                skillCooldownFills[i].fillAmount = cooldownMaxes[i] > 0f ? cooldownTimers[i] / cooldownMaxes[i] : 0f;
            if (skillButtons != null && i < skillButtons.Length && skillButtons[i] != null)
                skillButtons[i].interactable = cooldownTimers[i] <= 0f;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  HUD 업데이트
    // ════════════════════════════════════════════════════════════

    public void UpdateHealthBar(float current, float max)
    {
        if (healthSlider != null) healthSlider.value = max > 0 ? current / max : 0f;
        if (healthText   != null) healthText.text    = $"{current:0.#} / {max:0.#}";
    }

    public void UpdateSurvivorCount(int alive, int total)
    {
        if (survivorCountText != null)
            survivorCountText.text = $"생존자: {alive} / {total}";
    }

    public void UpdateTimer(float elapsed)
    {
        if (timerText == null) return;
        timerText.text = $"{(int)(elapsed / 60f):00}:{(int)(elapsed % 60f):00}";
    }

    public void ShowGameEndBanner(string message)
    {
        if (endBannerPanel != null) endBannerPanel.SetActive(true);
        if (endBannerText  != null) endBannerText.text = message;
    }

    public void SetGameStarted(int totalPlayers)
    {
        UpdateSurvivorCount(totalPlayers, totalPlayers);
    }

    // ════════════════════════════════════════════════════════════
    //  부활 UI
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// PlayerNetworkSync.OfferReviveClientRpc()에서 호출.
    /// 5초 카운트다운 부활창을 표시하며, 현재 매치 부활 잔여 횟수와
    /// 제한 조건 안내 문구를 함께 표시합니다.
    /// </summary>
    public void ShowReviveUI(PlayerNetworkSync sync)
    {
        pendingReviveSync = sync;
        if (revivePanel != null) revivePanel.SetActive(true);

        // ── 부활 정보 텍스트 갱신 ──────────────────────────────
        UpdateReviveInfoText();

        if (reviveButton != null)
        {
            reviveButton.onClick.RemoveAllListeners();
            reviveButton.onClick.AddListener(OnReviveClicked);
        }

        if (giveUpButton != null)
        {
            giveUpButton.onClick.RemoveAllListeners();
            giveUpButton.onClick.AddListener(OnGiveUpClicked);
        }

        if (reviveCountdownRoutine != null) StopCoroutine(reviveCountdownRoutine);
        reviveCountdownRoutine = StartCoroutine(ReviveCountdown(5f));
    }

    /// <summary>
    /// 보유 부활권 수량, 매치 잔여 횟수, 제한 시간을 reviveInfoText에 표시합니다.
    /// Supabase profiles.revive_ticket_count는 GameManager.Instance.reviveTicketCount에
    /// 캐시된 값을 사용합니다. (로비 로그인 시 로드됨)
    /// </summary>
    private void UpdateReviveInfoText()
    {
        if (reviveInfoText == null) return;

        var mgr = InGameManager.Instance;
        if (mgr == null) { reviveInfoText.text = ""; return; }

        int remaining      = InGameManager.MaxMatchReviveCount - mgr.MatchReviveUsedCount;
        float timeLeft     = Mathf.Max(0f, 60f - mgr.ElapsedGameTime);
        int survivors      = mgr.AliveCount;

        // Supabase에서 캐시된 보유 티켓 수량
        int ownedTickets   = GameManager.Instance != null ? GameManager.Instance.reviveTicketCount : 0;

        // 제한 사유 우선 표시
        if (ownedTickets <= 0)
        {
            reviveInfoText.text = "보유한 즉시부활권이 없습니다.\n(로비에서 피자 또는 광고로 획득 가능)";
        }
        else if (timeLeft <= 0f)
        {
            reviveInfoText.text = "게임 시작 60초가 지나 부활권을 사용할 수 없습니다.";
        }
        else if (survivors <= 2)
        {
            reviveInfoText.text = $"생존자가 {survivors}명이라 부활권을 사용할 수 없습니다.\n(최소 3명 이상이어야 사용 가능)";
        }
        else if (remaining <= 0)
        {
            reviveInfoText.text = "이번 매치의 부활권이 모두 소진되었습니다. (0/3)";
        }
        else
        {
            reviveInfoText.text =
                $"보유 즉시부활권: {ownedTickets}장  |  " +
                $"매치 잔여: {remaining}/{InGameManager.MaxMatchReviveCount}  |  " +
                $"제한 시간: {timeLeft:0}초";
        }
    }

    private IEnumerator ReviveCountdown(float duration)
    {
        // [Fix #8] 매 프레임(yield return null)에서 0.5초 간격으로 변경.
        // UpdateReviveInfoText()의 불필요한 매 프레임 연산을 줄이고,
        // 카운트다운 숫자는 0.5초 단위로 갱신해도 사용자가 체감하지 못합니다.
        var halfSecWait = new WaitForSeconds(0.5f);
        float timer = duration;
        while (timer > 0f)
        {
            if (reviveTimerText != null)
                reviveTimerText.text = Mathf.CeilToInt(timer).ToString();

            // 카운트다운 중에도 정보 텍스트 갱신
            UpdateReviveInfoText();

            yield return halfSecWait;
            timer -= 0.5f;
        }
        // 5초 만료 → 서버 타임아웃 코루틴이 킬 처리, UI만 닫음
        HideReviveUI();
    }

    private void OnReviveClicked()
    {
        if (pendingReviveSync != null)
            pendingReviveSync.RequestReviveServerRpc();
        HideReviveUI();
    }

    private void OnGiveUpClicked()
    {
        if (pendingReviveSync != null)
            pendingReviveSync.RequestGiveUpServerRpc();
        HideReviveUI();
    }

    public void HideReviveUI()
    {
        if (reviveCountdownRoutine != null)
        {
            StopCoroutine(reviveCountdownRoutine);
            reviveCountdownRoutine = null;
        }
        if (revivePanel != null) revivePanel.SetActive(false);
        pendingReviveSync = null;
    }

    // ════════════════════════════════════════════════════════════
    //  스킬 이름 (40종 전체)
    // ════════════════════════════════════════════════════════════

    private static string GetSkillShortName(ActiveSkillType skill) => skill switch
    {
        ActiveSkillType.Sweep            => "휩쓸기",
        ActiveSkillType.ChargeStrike     => "돌진강타",
        ActiveSkillType.DefenseStance    => "방어태세",
        ActiveSkillType.EarthquakeStrike => "지진강타",
        ActiveSkillType.ShieldBash       => "방패강타",
        ActiveSkillType.Shockwave        => "지진파",
        ActiveSkillType.IronSkin         => "강철피부",
        ActiveSkillType.Bulldozer        => "불도저",
        ActiveSkillType.HolyStrike       => "신성타격",
        ActiveSkillType.JudgmentHammer   => "심판망치",
        ActiveSkillType.DivineGrace      => "신의가호",
        ActiveSkillType.PillarOfJudgment => "심판기둥",
        ActiveSkillType.RuthlessStrike   => "무자비",
        ActiveSkillType.BleedSlash       => "출혈베기",
        ActiveSkillType.UndyingRage      => "불굴분노",
        ActiveSkillType.BladeStorm       => "칼날폭풍",
        ActiveSkillType.Fireball         => "화염구",
        ActiveSkillType.IceShards        => "얼음파편",
        ActiveSkillType.IceShield        => "얼음방패",
        ActiveSkillType.Meteor           => "메테오",
        ActiveSkillType.PierceArrow      => "관통화살",
        ActiveSkillType.MultiShot        => "다중사격",
        ActiveSkillType.Trap             => "덫놓기",
        ActiveSkillType.ArrowRain        => "화살폭우",
        ActiveSkillType.Smite            => "징벌",
        ActiveSkillType.HolyExplosion    => "신성폭발",
        ActiveSkillType.HealingLight     => "치유빛",
        ActiveSkillType.GuardianAngel    => "수호천사",
        ActiveSkillType.PoisonDagger     => "독단검",
        ActiveSkillType.Ambush           => "기습",
        ActiveSkillType.SmokeBomb        => "연막탄",
        ActiveSkillType.ShadowRaid       => "그림자습격",
        ActiveSkillType.VitalStrike      => "급소찌르기",
        ActiveSkillType.Shuriken         => "표창",
        ActiveSkillType.StealthSkill     => "은신",
        ActiveSkillType.DeathMark        => "죽음표식",
        ActiveSkillType.FryingPan        => "프라이팬",
        ActiveSkillType.BurningOil       => "불타는기름",
        ActiveSkillType.SnackTime        => "간식타임",
        ActiveSkillType.FeastTime        => "만찬시간",
        _                                => skill.ToString()
    };
}
