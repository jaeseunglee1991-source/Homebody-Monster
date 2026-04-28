using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 결과 씬 UI를 관리합니다.
/// GameManager.lastMatchResult에서 실제 게임 결과를 가져와 표시합니다.
/// 피자 보상 수령, 광고 2배 보너스, 전적 표시, 결과 채팅 기능을 포함합니다.
/// </summary>
public class ResultController : MonoBehaviour
{
    [Header("UI References")]
    public CanvasGroup mainCanvasGroup;
    public TextMeshProUGUI resultTitleText;
    public TextMeshProUGUI resultDetailText;

    [Header("보상 UI")]
    public TextMeshProUGUI rewardText;          // "🍕 60 피자 획득!"
    public Button adBonusButton;                // "광고 시청으로 2배!" 버튼
    public TextMeshProUGUI adBonusButtonText;   // 버튼 내부 텍스트

    [Header("전적 UI")]
    public TextMeshProUGUI recordText;          // "전적: 12승 5패"

    [Header("Chat UI")]
    public TextMeshProUGUI chatLogText;
    public TMP_InputField chatInputField;
    public ScrollRect chatScrollRect;

    [Header("Buttons")]
    public Button exitButton;
    public Button sendButton;

    // ── 채팅 로그 메모리 관리 ─────────────────────────────────
    private readonly Queue<string> chatLines = new Queue<string>();
    private const int MaxChatLogLines = 50;

    // ── 보상 상태 ────────────────────────────────────────────
    private int _earnedPizza = 0;
    private bool _adBonusClaimed = false;
    private bool _rewardClaimed = false;

    private void Awake()
    {
        if (mainCanvasGroup != null)
            mainCanvasGroup.alpha = 0;

        // 광고 보너스 버튼 초기 비활성
        if (adBonusButton != null) adBonusButton.gameObject.SetActive(false);
    }

    private void Start()
    {
        // ── NGO 연결 정리 (인게임 → 결과 씬 전환 시) ──────────
        CleanupNetcodeConnection();

        // ── 채팅 이벤트 연결 + 로비 채팅 채널 접속 ──────────────
        if (AppNetworkManager.Instance != null)
        {
            AppNetworkManager.Instance.OnChatReceived += UpdateChatUI;
            // [Fix #4] 이벤트 구독 이후에 ConnectToLobby 호출해야 이미 수신된 메시지가 누락되지 않음
            AppNetworkManager.Instance.ConnectToLobby();
        }

        DisplayMatchResult();
        StartCoroutine(FadeInSequence());

        // 버튼 리스너 등록
        if (exitButton != null) exitButton.onClick.AddListener(OnClickExitToLobby);
        if (sendButton != null) sendButton.onClick.AddListener(OnClickSendChat);
        if (adBonusButton != null) adBonusButton.onClick.AddListener(OnClickAdBonus);

        // 채팅 입력 설정
        SetupChatInput();

        // 전적 로드 및 피자 보상 수령
        StartCoroutine(PostResultSequence());
    }

    private void Update()
    {
        // 엔터 키로 채팅 전송 지원
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (chatInputField != null && chatInputField.isFocused)
            {
                OnClickSendChat();
            }
            else if (chatInputField != null)
            {
                chatInputField.ActivateInputField();
            }
        }
    }

    private void OnDestroy()
    {
        if (AppNetworkManager.Instance != null)
        {
            AppNetworkManager.Instance.OnChatReceived -= UpdateChatUI;
            // [Fix #4] 씬 전환 시 채팅 채널 구독 해제 — 리소스 누수 및 이벤트 중복 방지
            AppNetworkManager.Instance.DisconnectLobbyChat();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  페이드 인 연출
    // ════════════════════════════════════════════════════════════

    private IEnumerator FadeInSequence()
    {
        if (mainCanvasGroup == null) yield break;

        float elapsed = 0f;
        float duration = 0.8f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            mainCanvasGroup.alpha = Mathf.Lerp(0, 1, elapsed / duration);
            yield return null;
        }
        mainCanvasGroup.alpha = 1;
    }

    // ════════════════════════════════════════════════════════════
    //  결과 표시
    // ════════════════════════════════════════════════════════════

    private void DisplayMatchResult()
    {
        if (GameManager.Instance == null) return;

        MatchResult result = GameManager.Instance.lastMatchResult;

        // 제목 텍스트
        if (resultTitleText != null)
        {
            if (result.isWinner)
                resultTitleText.text = "<color=#f1c40f><size=72>🏆 VICTORY!</size></color>\n<size=36>최후의 1인! 승리했습니다.</size>";
            else
                resultTitleText.text = $"<color=#e74c3c><size=72>DEFEATED</size></color>\n<size=36>{result.rank}위로 탈락했습니다.</size>";
        }

        // 상세 텍스트 (킬 수, 생존 시간)
        if (resultDetailText != null)
        {
            int min = (int)(result.survivedTime / 60f);
            int sec = (int)(result.survivedTime % 60f);
            resultDetailText.text = $"KILLS: <color=#3498db>{result.killCount}</color>   SURVIVAL: <color=#2ecc71>{min:00}:{sec:00}</color>";
        }
    }

    // ════════════════════════════════════════════════════════════
    //  결과 씬 진입 후 순차 처리 (전적 로드 → 보상 수령 → 광고 버튼)
    // ════════════════════════════════════════════════════════════

    private IEnumerator PostResultSequence()
    {
        // 1. Supabase 프로필에서 전적 불러오기
        yield return StartCoroutine(LoadAndDisplayRecord());

        // 2. 피자 보상 수령 (DB RPC: grant_match_rewards)
        yield return StartCoroutine(ClaimMatchRewards());
    }

    /// <summary>
    /// Supabase 프로필에서 승/패 전적을 로드하여 UI에 표시합니다.
    /// </summary>
    private IEnumerator LoadAndDisplayRecord()
    {
        if (recordText == null || SupabaseManager.Instance == null ||
            GameManager.Instance == null || string.IsNullOrEmpty(GameManager.Instance.currentPlayerId))
        {
            yield break;
        }

        var task = SupabaseManager.Instance.GetOrCreateProfile(GameManager.Instance.currentPlayerId);
        while (!task.IsCompleted) yield return null;

        if (task.Result != null)
        {
            var profile = task.Result;
            recordText.text = $"전적: <color=#f1c40f>{profile.WinCount}</color>승  <color=#e74c3c>{profile.LoseCount}</color>패";
        }
        else
        {
            recordText.text = "전적: 불러오기 실패";
        }
    }

    /// <summary>
    /// 피자 보상을 DB에서 수령하고 UI에 표시합니다.
    /// 수령 후 광고 2배 보너스 버튼을 활성화합니다.
    /// </summary>
    private IEnumerator ClaimMatchRewards()
    {
        if (_rewardClaimed || SupabaseManager.Instance == null || GameManager.Instance == null)
        {
            yield break;
        }

        MatchResult result = GameManager.Instance.lastMatchResult;

        var task = SupabaseManager.Instance.GrantMatchRewards(result.rank, result.killCount, false);
        while (!task.IsCompleted) yield return null;

        _earnedPizza = task.Result;
        _rewardClaimed = true;

        // 보상 표시
        if (rewardText != null)
        {
            if (_earnedPizza > 0)
                rewardText.text = $"<color=#f39c12>🍕 {_earnedPizza} 피자 획득!</color>";
            else
                rewardText.text = "<color=#95a5a6>보상 없음</color>";
        }

        // 광고 2배 버튼 활성화 (보상이 있을 때만)
        if (adBonusButton != null && _earnedPizza > 0)
        {
            adBonusButton.gameObject.SetActive(true);
            if (adBonusButtonText != null)
                adBonusButtonText.text = $"📺 광고 시청으로 {_earnedPizza}🍕 추가 획득!";
        }
    }

    // ════════════════════════════════════════════════════════════
    //  광고 2배 보너스
    // ════════════════════════════════════════════════════════════

    private void OnClickAdBonus()
    {
        if (_adBonusClaimed || _earnedPizza <= 0) return;

        // TODO: 실제 광고 SDK 연동 (AdMob 등)
        // 광고 시청 완료 콜백에서 아래를 호출
        StartCoroutine(ClaimAdBonusReward());
    }

    private IEnumerator ClaimAdBonusReward()
    {
        if (adBonusButton != null) adBonusButton.interactable = false;

        if (SupabaseManager.Instance == null || GameManager.Instance == null)
            yield break;

        MatchResult result = GameManager.Instance.lastMatchResult;

        // DB에 광고 2배 보상 요청
        var task = SupabaseManager.Instance.GrantMatchRewards(result.rank, result.killCount, true);
        while (!task.IsCompleted) yield return null;

        int bonusPizza = task.Result;
        _adBonusClaimed = true;

        if (rewardText != null)
            rewardText.text = $"<color=#f39c12>🍕 {_earnedPizza} + {bonusPizza} 피자 획득! (광고 보너스)</color>";

        if (adBonusButton != null)
            adBonusButton.gameObject.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  채팅
    // ════════════════════════════════════════════════════════════

    private void SetupChatInput()
    {
        if (chatInputField == null) return;

        chatInputField.characterLimit = 100;
        chatInputField.lineType = TMP_InputField.LineType.SingleLine;

        // 엔터키 전송 (onSubmit 이벤트 활용 — Update()의 Input.GetKeyDown과 상보적)
        chatInputField.onSubmit.AddListener((val) =>
        {
            OnClickSendChat();
            chatInputField.ActivateInputField();
        });
    }

    public void OnClickSendChat()
    {
        if (chatInputField != null && !string.IsNullOrEmpty(chatInputField.text))
        {
            AppNetworkManager.Instance?.SendChatMessage(chatInputField.text);
            chatInputField.text = "";
            chatInputField.ActivateInputField(); // 전송 후 다시 포커스
        }
    }

    private void UpdateChatUI(string message)
    {
        if (chatLogText == null) return;

        // 채팅 로그 메모리 관리 (최대 50줄)
        chatLines.Enqueue(message);
        while (chatLines.Count > MaxChatLogLines)
            chatLines.Dequeue();

        chatLogText.text = string.Join("\n", chatLines);

        // 스크롤을 맨 아래로 내림
        ScrollToBottom();
    }

    /// <summary>채팅 스크롤 영역을 맨 아래로 이동합니다.</summary>
    private void ScrollToBottom()
    {
        if (chatScrollRect == null || chatScrollRect.content == null) return;
        LayoutRebuilder.ForceRebuildLayoutImmediate(chatScrollRect.content);
        chatScrollRect.verticalNormalizedPosition = 0f;
    }

    // ════════════════════════════════════════════════════════════
    //  NGO 연결 정리
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 인게임에서 결과 씬으로 넘어올 때 NGO 연결을 정리합니다.
    /// NetworkManager.Shutdown()은 씬 전환 후 호출해야 안전합니다.
    /// </summary>
    private void CleanupNetcodeConnection()
    {
        if (Unity.Netcode.NetworkManager.Singleton != null &&
            Unity.Netcode.NetworkManager.Singleton.IsListening)
        {
            Debug.Log("[ResultController] NGO 연결 종료 (결과 씬 진입)");
            Unity.Netcode.NetworkManager.Singleton.Shutdown();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  로비로 복귀
    // ════════════════════════════════════════════════════════════

    public void OnClickExitToLobby()
    {
        // 중복 클릭 방지
        if (exitButton != null) exitButton.interactable = false;
        
        GameManager.Instance?.ResetForNewMatch();
    }
}

