using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 인게임 HUD — 표시 전용(데이터 구동은 Fusion NetHudBridge가 담당).
///
/// [Pass C] NGO 결합부(InitPlayerUI(PlayerController)/스킬버튼 직접 처리/ShowReviveUI(PlayerNetworkSync)/
/// InGameManager 참조)는 제거됨. NetHudBridge가 NetPlayer/NetMatch 값을 읽어 아래 필드·메서드를 구동하고,
/// 스킬버튼·부활·항복 패널도 브리지가 직접 배선한다. 이 클래스는 캔버스 위젯 보관 + 표시 헬퍼만 제공.
/// </summary>
public class InGameHUD : MonoBehaviour
{
    public static InGameHUD Instance { get; private set; }

    [Header("체력 UI")]
    public Slider healthSlider;
    public TextMeshProUGUI healthText;

    [Header("생존자 UI")]
    public TextMeshProUGUI survivorCountText;

    [Header("스킬 버튼 (최대 4개) — NetHudBridge가 배선")]
    public Button[] skillButtons;
    public Image[] skillCooldownFills;
    public TextMeshProUGUI[] skillNameTexts;

    [Header("타이머")]
    public TextMeshProUGUI timerText;

    [Header("게임 종료 배너")]
    public GameObject endBannerPanel;
    public TextMeshProUGUI endBannerText;

    [Header("킬 피드")]
    public TextMeshProUGUI killFeedText;

    private const int KillFeedMaxLines = 5;
    private const float KillFeedDisplaySecs = 4f;
    private readonly Queue<(string msg, float expireAt)> _killFeedQueue = new Queue<(string, float)>();
    private Coroutine _killFeedRoutine;

    [Header("조작 UI 루트 (스펙테이터/사망 시 숨김 — NetHudBridge가 토글)")]
    public GameObject controlsRoot;

    [Header("포기(항복) 확인 UI — NetHudBridge가 배선")]
    public GameObject surrenderConfirmPanel;
    public Button surrenderConfirmButton;
    public Button surrenderCancelButton;

    [Header("부활 UI — NetHudBridge가 배선")]
    public GameObject revivePanel;
    public TextMeshProUGUI reviveTimerText;
    public TextMeshProUGUI reviveInfoText;
    public Button reviveButton;
    public Button giveUpButton;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        if (endBannerPanel        != null) endBannerPanel.SetActive(false);
        if (revivePanel           != null) revivePanel.SetActive(false);
        if (surrenderConfirmPanel != null) surrenderConfirmPanel.SetActive(false);
        if (killFeedText          != null) killFeedText.text = "";

        if (timerText == null)
        {
            var tmpList = GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var t in tmpList) if (t.gameObject.name == "TimerText") { timerText = t; break; }
        }
        if (timerText != null) timerText.text = "00:00";

        if (survivorCountText == null)
        {
            var tmpList = GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var t in tmpList) if (t.gameObject.name == "SurvivorCountText") { survivorCountText = t; break; }
        }
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    /// <summary>조작 UI(스킬버튼 등) 표시 토글. NetHudBridge가 사망/관전 시 false로 호출.</summary>
    public void SetControlsVisible(bool visible)
    {
        if (controlsRoot != null) controlsRoot.SetActive(visible);
    }

    // ════════════════════════════════════════════════════════════
    //  표시 헬퍼 (NetHudBridge가 호출)
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

    public void UpdateTimer(float seconds)
    {
        if (timerText == null) return;
        timerText.text = $"{(int)(seconds / 60f):00}:{(int)(seconds % 60f):00}";
    }

    public void ShowGameEndBanner(string message, bool playResultBGM = false)
    {
        if (endBannerPanel != null) endBannerPanel.SetActive(true);
        if (endBannerText  != null) endBannerText.text = message;
        if (playResultBGM) AudioManager.Instance?.PlayResultBGM();
    }

    public void SetGameStarted(int totalPlayers)
    {
        UpdateSurvivorCount(totalPlayers, totalPlayers);
        AudioManager.Instance?.PlayInGameBGM();
    }

    // ════════════════════════════════════════════════════════════
    //  킬 피드
    // ════════════════════════════════════════════════════════════

    /// <summary>킬 이벤트 표시. 로컬 플레이어가 공격자면 닉네임을 노란색으로 강조.</summary>
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
        if (_killFeedQueue.Count == 0) { killFeedText.text = ""; return; }

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
            if (changed) RefreshKillFeedText();
        }
        _killFeedRoutine = null;
    }
}
