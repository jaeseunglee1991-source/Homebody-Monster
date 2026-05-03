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
///  - 킬 피드(Kill Feed) 구현: 최대 5줄, 항목별 4초 후 자동 제거
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

    [Header("킬 피드")]
    /// <summary>
    /// 킬 로그를 표시할 TextMeshProUGUI.
    /// Inspector에서 화면 우측 상단 영역에 배치하세요.
    /// Overflow: Overflow, Alignment: Right, Rich Text: ON 권장.
    /// </summary>
    public TextMeshProUGUI killFeedText;

    // ── 킬 피드 내부 상태 ─────────────────────────────────────
    private const int   KillFeedMaxLines    = 5;
    private const float KillFeedDisplaySecs = 4f;

    private readonly Queue<(string msg, float expireAt)> _killFeedQueue
        = new Queue<(string, float)>();

    private Coroutine _killFeedRoutine;

    [Header("조작 UI 루트 (스펙테이터 모드에서 숨김)")]
    /// <summary>
    /// 스킬 버튼, 공격 버튼 등 인게임 조작 UI를 묶은 루트 GameObject.
    /// Inspector에서 연결하세요. SpectatorManager가 사망 시 비활성화합니다.
    /// </summary>
    public GameObject controlsRoot;

    [Header("부활 UI")]
    public GameObject      revivePanel;
    public TextMeshProUGUI reviveTimerText;
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
        if (killFeedText   != null) killFeedText.text = "";
    }

    public void InitPlayerUI(PlayerController player)
    {
        localPlayer = player;
        SetupSkillButtons(player);
    }

    /// <summary>
    /// 인게임 조작 UI(스킬 버튼 등)의 표시 여부를 전환합니다.
    /// SpectatorManager에서 사망 후 관전 진입 시 false로 호출합니다.
    /// </summary>
    public void SetControlsVisible(bool visible)
    {
        if (controlsRoot != null)
            controlsRoot.SetActive(visible);
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

        // [FIX] UseSkill 반환값(bool)으로 성공 여부 확인 후 쿨다운 시작.
        // IsSilenced/IsDead로 RPC가 차단돼도 쿨다운 UI가 무조건 시작되는 버그 수정.
        if (localPlayer.UseSkill(slot))
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
        AudioManager.Instance?.PlayResultBGM();
    }

    public void SetGameStarted(int totalPlayers)
    {
        UpdateSurvivorCount(totalPlayers, totalPlayers);
        AudioManager.Instance?.PlayInGameBGM();
    }

    // ════════════════════════════════════════════════════════════
    //  킬 피드
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 킬 이벤트 발생 시 호출합니다.
    /// PlayerNetworkSync(또는 InGameManager)의 킬 처리 완료 콜백에서 호출하세요.
    /// 로컬 플레이어가 공격자일 경우 닉네임을 노란색으로 강조합니다.
    /// </summary>
    public void ShowKillFeed(string attackerName, string victimName)
    {
        if (killFeedText == null) return;

        string localNickname = GameManager.Instance?.currentPlayerNickname ?? "";

        string attackerFormatted = attackerName == localNickname
            ? $"<color=#f1c40f>{attackerName}</color>"
            : attackerName;

        string entry = $"⚔️ {attackerFormatted} → {victimName}";

        _killFeedQueue.Enqueue((entry, Time.time + KillFeedDisplaySecs));
        AudioManager.Instance?.PlayKillFeed();

        while (_killFeedQueue.Count > KillFeedMaxLines)
            _killFeedQueue.Dequeue();

        RefreshKillFeedText();

        if (_killFeedRoutine == null)
            _killFeedRoutine = StartCoroutine(KillFeedExpiryRoutine());
    }

    private void RefreshKillFeedText()
    {
        if (killFeedText == null) return;

        if (_killFeedQueue.Count == 0)
        {
            killFeedText.text = "";
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var (msg, _) in _killFeedQueue)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(msg);
        }
        killFeedText.text = sb.ToString();
    }

    private IEnumerator KillFeedExpiryRoutine()
    {
        var halfSec = new WaitForSeconds(0.5f);

        while (_killFeedQueue.Count > 0)
        {
            yield return halfSec;

            bool changed = false;
            while (_killFeedQueue.Count > 0 && Time.time >= _killFeedQueue.Peek().expireAt)
            {
                _killFeedQueue.Dequeue();
                changed = true;
            }

            if (changed)
                RefreshKillFeedText();
        }

        _killFeedRoutine = null;
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
