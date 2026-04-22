using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 로비 씬 UI 관리자. (팝업형 매칭 UI 및 모바일 호환성 적용 버전)
/// </summary>
public class LobbyUIController : MonoBehaviour
{
    [Header("로비 기본 UI")]
    public GameObject lobbyPanel;         // 플레이어 목록 + 채팅 + 시작 버튼 영역
    public Text       playerListText;
    public Text       chatLogText;
    public InputField chatInputField;
    public Button     startMatchButton;

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

    private void Start()
    {
        ShowLobbyPanel();

        // 1. 네트워크(채팅/접속자) 이벤트 연결
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnPlayerListUpdated += UpdatePlayerListUI;
            NetworkManager.Instance.OnChatReceived      += UpdateChatUI;
            NetworkManager.Instance.ConnectToLobby();
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
    }

    private void Update()
    {
        // 📱 실전 호환성: 안드로이드 뒤로가기(Escape) 버튼으로 매칭 취소 지원
        if (isPopupActive && Input.GetKeyDown(KeyCode.Escape))
        {
            OnClickCancelMatch();
        }
    }

    private void OnDestroy()
    {
        // 🧹 씬 전환 시 참조 해제 (MissingReferenceException 에러 방지)
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnPlayerListUpdated -= UpdatePlayerListUI;
            NetworkManager.Instance.OnChatReceived      -= UpdateChatUI;
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
        if (chatInputField != null && !string.IsNullOrEmpty(chatInputField.text))
        {
            NetworkManager.Instance?.SendChatMessage(chatInputField.text);
            chatInputField.text = "";
        }
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
        if (chatLogText != null)
            chatLogText.text += message + "\n";
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
