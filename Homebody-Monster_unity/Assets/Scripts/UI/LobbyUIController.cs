using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 로비 씬 UI 관리자. (팝업형 매칭 UI 및 모바일 호환성 적용 버전)
///
/// 채팅 기능 (Supabase Realtime Broadcast 기반):
///  - 로비 전용 (인게임/결과 화면에서는 작동하지 않음)
///  - 세로형 안드로이드 모바일 최적화: 자동 스크롤, 100자 제한, 엔터키 전송
///  - 채팅 로그 메모리 관리: 최대 50줄 유지
///  - 씬 전환 시 Supabase Realtime 채널 자동 해제
/// </summary>
public class LobbyUIController : MonoBehaviour
{
    [Header("로비 기본 UI")]
    public GameObject lobbyPanel;         // 플레이어 목록 + 채팅 + 시작 버튼 영역
    public Text       playerListText;
    public Text       chatLogText;
    public InputField chatInputField;
    public Button     sendChatButton;     // 채팅 전송 버튼 (모바일 터치용)
    public Button     startMatchButton;
    public ScrollRect chatScrollRect;     // 채팅 로그 스크롤 영역 (Inspector 연결)

    [Header("매칭 팝업 (Dim 처리 포함)")]
    public GameObject dimBackground;      // 반투명 배경 (Raycast Target 체크 필수)
    public GameObject matchmakingPanel;   // 매칭 중일 때 보일 팝업창
    public Text       queueCountText;     // "3 / 8명"
    public Text       timerText;          // "00:42"
    public Text       statusText;         // "상대를 찾는 중..."
    public Slider     timerSlider;        // 60초 진행 바
    public Button     cancelMatchButton;

    private float maxWaitSeconds = 60f;
    private bool  isPopupActive = false;  // 현재 팝업이 켜져 있는지 추적

    // ── 채팅 로그 관리 ───────────────────────────────────────────
    private readonly List<string> chatLines = new List<string>();
    private const int MaxChatLogLines = 50; // 메모리 관리: 최대 표시 줄 수

    private void Start()
    {
        ShowLobbyPanel();

        // 1. 네트워크(채팅/접속자) 이벤트 연결
        if (AppNetworkManager.Instance != null)
        {
            AppNetworkManager.Instance.OnPlayerListUpdated += UpdatePlayerListUI;
            AppNetworkManager.Instance.OnChatReceived      += UpdateChatUI;
            AppNetworkManager.Instance.ConnectToLobby();
        }

        // 2. 매치메이킹 이벤트 연결
        if (MatchmakingManager.Instance != null)
        {
            maxWaitSeconds = MatchmakingManager.Instance.maxWaitSeconds;
            MatchmakingManager.Instance.OnQueueCountChanged    += HandleQueueCountChanged;
            MatchmakingManager.Instance.OnTimerUpdated         += HandleTimerUpdated;
            MatchmakingManager.Instance.OnStatusMessageChanged += HandleStatusChanged;
            MatchmakingManager.Instance.OnMatchFound           += HandleMatchFound;
            MatchmakingManager.Instance.OnMatchmakingFailed    += HandleMatchmakingFailed;
        }

        if (timerSlider != null)
            timerSlider.maxValue = maxWaitSeconds;

        // 3. 채팅 입력 필드 설정 (모바일 최적화)
        SetupChatInput();

        // 4. 로비 진입 시 시스템 메시지 표시
        UpdateChatUI("[시스템]: 로비에 입장했습니다. 즐거운 게임 되세요!");
    }

    private void Update()
    {
        // 📱 실전 호환성: 안드로이드 뒤로가기(Escape) 버튼으로 매칭 취소 지원
        if (isPopupActive && Input.GetKeyDown(KeyCode.Escape))
        {
            OnClickCancelMatch();
        }

        // ⌨️ 에디터/키보드 사용자를 위한 Enter키 전송 지원
        if (chatInputField != null && chatInputField.isFocused && Input.GetKeyDown(KeyCode.Return))
        {
            OnClickSendChat();
        }
    }

    private void OnDestroy()
    {
        // 🧹 씬 전환 시 참조 해제 (MissingReferenceException 에러 방지)
        if (AppNetworkManager.Instance != null)
        {
            AppNetworkManager.Instance.OnPlayerListUpdated -= UpdatePlayerListUI;
            AppNetworkManager.Instance.OnChatReceived      -= UpdateChatUI;

            // Supabase Realtime 채팅 채널 구독 해제 (로비 전용이므로 씬 이탈 시 정리)
            AppNetworkManager.Instance.DisconnectLobbyChat();
        }

        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnQueueCountChanged    -= HandleQueueCountChanged;
            MatchmakingManager.Instance.OnTimerUpdated         -= HandleTimerUpdated;
            MatchmakingManager.Instance.OnStatusMessageChanged -= HandleStatusChanged;
            MatchmakingManager.Instance.OnMatchFound           -= HandleMatchFound;
            MatchmakingManager.Instance.OnMatchmakingFailed    -= HandleMatchmakingFailed;
        }
    }

    // ── 채팅 입력 필드 설정 (모바일 UX) ───────────────────────

    private void SetupChatInput()
    {
        if (chatInputField == null) return;

        // 글자 수 제한: Supabase 전송 제한과 일치
        chatInputField.characterLimit = SupabaseManager.MaxChatMessageLength;

        // 모바일 키보드: 한 줄 입력 + 리턴 버튼을 "Send"로 표시
        chatInputField.lineType   = InputField.LineType.SingleLine;
        chatInputField.contentType = InputField.ContentType.Standard;

        // 플레이스홀더 텍스트 설정
        if (chatInputField.placeholder is Text placeholderText)
            placeholderText.text = "메시지를 입력하세요...";

        // 전송 버튼 연결
        if (sendChatButton != null)
            sendChatButton.onClick.AddListener(OnClickSendChat);
    }

    // ── 버튼 클릭 이벤트 ──────────────────────────────────────

    public void OnClickStartMatch()
    {
        if (startMatchButton != null) startMatchButton.interactable = false; // 연타 방지
        ShowMatchmakingPanel();
        MatchmakingManager.Instance?.StartSearch();
    }

    public void OnClickCancelMatch()
    {
        if (cancelMatchButton != null) cancelMatchButton.interactable = false; // 연타 방지
        MatchmakingManager.Instance?.CancelSearch();
        ShowLobbyPanel();
    }

    public void OnClickSendChat()
    {
        if (chatInputField == null) return;

        string msg = chatInputField.text;
        if (string.IsNullOrWhiteSpace(msg)) return;

        AppNetworkManager.Instance?.SendChatMessage(msg);
        chatInputField.text = "";

        // 📱 모바일: 전송 후 입력 필드에 포커스 유지 (연속 채팅 편의성)
        chatInputField.ActivateInputField();
    }

    // ── 매치메이킹 UI 콜백 ────────────────────────────────────

    private void HandleQueueCountChanged(int current, int max)
    {
        if (queueCountText != null)
            queueCountText.text = $"{current} / {max}명";
    }

    private void HandleTimerUpdated(float remaining)
    {
        if (timerText != null)
        {
            int sec = Mathf.CeilToInt(remaining);
            timerText.text = $"{sec:00}초";
        }

        if (timerSlider != null)
            timerSlider.value = remaining;
    }

    private void HandleStatusChanged(string message)
    {
        if (statusText != null)
            statusText.text = message;
    }

    private void HandleMatchFound()
    {
        if (statusText != null)
            statusText.text = "매칭 완료! 게임 입장 중...";
        
        // 🔒 매칭이 성사되면 더 이상 취소하지 못하도록 버튼 잠금
        if (cancelMatchButton != null)
            cancelMatchButton.interactable = false;
    }

    private void HandleMatchmakingFailed()
    {
        ShowLobbyPanel();
    }

    // ── 채팅/플레이어 UI 콜백 ─────────────────────────────────

    private void UpdatePlayerListUI(List<string> players)
    {
        if (playerListText != null)
            playerListText.text = "접속자 목록:\n" + string.Join("\n", players);
    }

    private void UpdateChatUI(string message)
    {
        if (chatLogText == null) return;

        // 채팅 로그 관리: 최대 줄 수 초과 시 오래된 메시지 삭제
        chatLines.Add(message);
        while (chatLines.Count > MaxChatLogLines)
            chatLines.RemoveAt(0);

        chatLogText.text = string.Join("\n", chatLines);

        // 📱 자동 스크롤: 새 메시지가 오면 항상 최하단으로 이동
        ScrollToBottom();
    }

    /// <summary>채팅 스크롤 영역을 맨 아래로 이동합니다.</summary>
    private void ScrollToBottom()
    {
        if (chatScrollRect != null)
        {
            // Canvas 레이아웃이 갱신된 후 스크롤해야 정확히 최하단에 위치
            Canvas.ForceUpdateCanvases();
            chatScrollRect.verticalNormalizedPosition = 0f;
        }
    }

    // ── 패널 전환 상태 관리 ───────────────────────────────────

    private void ShowLobbyPanel()
    {
        isPopupActive = false;

        if (lobbyPanel       != null) lobbyPanel.SetActive(true);
        if (dimBackground    != null) dimBackground.SetActive(false);    // 딤 배경 숨기기
        if (matchmakingPanel != null) matchmakingPanel.SetActive(false); // 팝업 숨기기

        if (startMatchButton  != null) startMatchButton.interactable = true;
        if (cancelMatchButton != null) cancelMatchButton.interactable = true;

        // UI 텍스트/슬라이더 초기화
        if (timerText      != null) timerText.text = $"{(int)maxWaitSeconds:00}초";
        if (timerSlider    != null) timerSlider.value = maxWaitSeconds;
        if (statusText     != null) statusText.text = "";
        if (queueCountText != null) queueCountText.text = "0 / 8명";
    }

    private void ShowMatchmakingPanel()
    {
        isPopupActive = true;

        if (lobbyPanel       != null) lobbyPanel.SetActive(true);        // 💡 뒤에 로비 화면 유지
        if (dimBackground    != null) dimBackground.SetActive(true);     // 💡 딤 배경 표시
        if (matchmakingPanel != null) matchmakingPanel.SetActive(true);  // 💡 매칭 팝업 표시
    }
}
