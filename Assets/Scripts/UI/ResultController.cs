using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 결과 씬 UI를 관리합니다.
/// GameManager.lastMatchResult에서 실제 게임 결과를 가져와 표시합니다.
/// </summary>
public class ResultController : MonoBehaviour
{
    [Header("Result UI")]
    public Text resultTitleText;    // "승리!" 또는 "패배 (X위)"
    public Text resultDetailText;   // 킬 수, 생존 시간 등 상세 정보

    [Header("Result Chat UI")]
    public Text       chatLogText;
    public InputField chatInputField;

    private void Start()
    {
        if (NetworkManager.Instance != null)
            NetworkManager.Instance.OnChatReceived += UpdateChatUI;

        DisplayMatchResult();
    }

    private void OnDestroy()
    {
        if (NetworkManager.Instance != null)
            NetworkManager.Instance.OnChatReceived -= UpdateChatUI;
    }

    // ── 결과 표시 ─────────────────────────────────────────────
    private void DisplayMatchResult()
    {
        if (GameManager.Instance == null) return;

        MatchResult result = GameManager.Instance.lastMatchResult;

        // 제목 텍스트
        if (resultTitleText != null)
        {
            if (result.isWinner)
                resultTitleText.text = "<color=#f1c40f>최후의 1인! 승리했습니다.</color>";
            else
                resultTitleText.text = $"<color=#e74c3c>{result.rank}위로 탈락했습니다.</color>";
        }

        // 상세 텍스트 (킬 수, 생존 시간)
        if (resultDetailText != null)
        {
            int min = (int)(result.survivedTime / 60f);
            int sec = (int)(result.survivedTime % 60f);
            resultDetailText.text = $"킬: {result.killCount}   생존 시간: {min:00}:{sec:00}";
        }
        
        // 참고: Supabase 저장은 InGameManager에서 게임 종료 시점에 수행하므로 
        // 여기서는 UI 표시만 담당합니다.
    }

    // ── 채팅 ─────────────────────────────────────────────────
    public void OnClickSendChat()
    {
        if (chatInputField != null && !string.IsNullOrEmpty(chatInputField.text))
        {
            NetworkManager.Instance?.SendChatMessage(chatInputField.text);
            chatInputField.text = "";
        }
    }

    private void UpdateChatUI(string message)
    {
        if (chatLogText != null)
            chatLogText.text += message + "\n";
    }

    // ── 로비로 복귀 ───────────────────────────────────────────
    public void OnClickExitToLobby()
    {
        GameManager.Instance?.ResetForNewMatch();
    }
}
