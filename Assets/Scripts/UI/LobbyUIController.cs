using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

public class LobbyUIController : MonoBehaviour
{
    [Header("UI Elements")]
    public Text playerListText;
    public Text chatLogText;
    public InputField chatInputField;
    public Button startMatchButton;
    public GameObject matchmakingLoadingPanel;

    private void Start()
    {
        // 네트워크 이벤트 구독
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnPlayerListUpdated += UpdatePlayerListUI;
            NetworkManager.Instance.OnChatReceived += UpdateChatUI;
            NetworkManager.Instance.OnMatchFound += HandleMatchFound;

            NetworkManager.Instance.ConnectToLobby();
        }
        
        if (matchmakingLoadingPanel != null)
            matchmakingLoadingPanel.SetActive(false);
    }

    private void OnDestroy()
    {
        // 이벤트 구독 해제
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnPlayerListUpdated -= UpdatePlayerListUI;
            NetworkManager.Instance.OnChatReceived -= UpdateChatUI;
            NetworkManager.Instance.OnMatchFound -= HandleMatchFound;
        }
    }

    public void OnClickStartMatch()
    {
        if (startMatchButton != null) startMatchButton.interactable = false;
        if (matchmakingLoadingPanel != null) matchmakingLoadingPanel.SetActive(true);
        NetworkManager.Instance.StartMatchmaking();
    }

    public void OnClickSendChat()
    {
        if (chatInputField != null && !string.IsNullOrEmpty(chatInputField.text))
        {
            NetworkManager.Instance.SendChatMessage(chatInputField.text);
            chatInputField.text = "";
        }
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

    private void HandleMatchFound()
    {
        // 씬 전환은 NetworkManager가 호출하므로 여기서는 UI 처리만 담당
        if (matchmakingLoadingPanel != null)
            matchmakingLoadingPanel.SetActive(false);
    }
}
