using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// 로비 씬 UI 관리자.
/// </summary>
public class LobbyUIController : MonoBehaviour
{
    [Header("로비 기본 UI")]
    public Text    playerListText;
    public Text    chatLogText;
    public InputField chatInputField;

    [Header("매칭 패널")]
    public GameObject lobbyPanel;         // 플레이어 목록 + 채팅 + 시작 버튼
    public Button     startMatchButton;

    [Header("매칭 대기 패널")]
    public GameObject matchmakingPanel;   // 매칭 중일 때 보임
    public Text       queueCountText;     // "3 / 8명"
    public Text       timerText;          // "00:42"
    public Text       statusText;         // "상대를 찾는 중..."
    public Slider     timerSlider;        // 60초 진행 바
    public Button     cancelMatchButton;

    private float maxWaitSeconds = 60f;

    private void Start()
    {
        ShowLobbyPanel();

        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnPlayerListUpdated += UpdatePlayerListUI;
            NetworkManager.Instance.OnChatReceived      += UpdateChatUI;
            NetworkManager.Instance.ConnectToLobby();
        }

        if (MatchmakingManager.Instance != null)
        {
            maxWaitSeconds = MatchmakingManager.Instance.maxWaitSeconds;
            MatchmakingManager.Instance.OnQueueCountChanged   += HandleQueueCountChanged;
            MatchmakingManager.Instance.OnTimerUpdated        += HandleTimerUpdated;
            MatchmakingManager.Instance.OnStatusMessageChanged += HandleStatusChanged;
            MatchmakingManager.Instance.OnMatchFound          += HandleMatchFound;
            MatchmakingManager.Instance.OnMatchmakingFailed   += HandleMatchmakingFailed;
        }

        if (timerSlider != null)
            timerSlider.maxValue = maxWaitSeconds;
    }

    private void OnDestroy()
    {
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

    public void OnClickStartMatch()
    {
        ShowMatchmakingPanel();
        MatchmakingManager.Instance?.StartSearch();
    }

    public void OnClickCancelMatch()
    {
        MatchmakingManager.Instance?.CancelSearch();
    }

    public void OnClickSendChat()
    {
        if (chatInputField != null && !string.IsNullOrEmpty(chatInputField.text))
        {
            NetworkManager.Instance?.SendChatMessage(chatInputField.text);
            chatInputField.text = "";
        }
    }

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
        if (cancelMatchButton != null)
            cancelMatchButton.interactable = false;
    }

    private void HandleMatchmakingFailed()
    {
        ShowLobbyPanel();
    }

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

    private void ShowLobbyPanel()
    {
        if (lobbyPanel      != null) lobbyPanel.SetActive(true);
        if (matchmakingPanel != null) matchmakingPanel.SetActive(false);
        if (cancelMatchButton != null) cancelMatchButton.interactable = true;

        if (timerText   != null) timerText.text = $"{(int)maxWaitSeconds:00}초";
        if (timerSlider != null) timerSlider.value = maxWaitSeconds;
        if (statusText  != null) statusText.text = "";
        if (queueCountText != null) queueCountText.text = "0 / 8명";
    }

    private void ShowMatchmakingPanel()
    {
        if (lobbyPanel       != null) lobbyPanel.SetActive(false);
        if (matchmakingPanel != null) matchmakingPanel.SetActive(true);
    }
}
