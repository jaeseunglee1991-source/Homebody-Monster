using UnityEngine;
using UnityEngine.UI;

public class ResultController : MonoBehaviour
{
    [Header("Result UI")]
    public Text resultTitleText; // 예: "승리!" 또는 "패배 (7위)"
    public Text resultDetailText; // 킬 수, 생존 시간 등
    
    [Header("Result Chat UI")]
    public Text chatLogText;
    public InputField chatInputField;

    private void Start()
    {
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnChatReceived += UpdateChatUI;
        }
        
        // 전적 기록 및 결과 텍스트 표시
        ProcessMatchResult();
    }

    private void OnDestroy()
    {
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnChatReceived -= UpdateChatUI;
        }
    }

    private void ProcessMatchResult()
    {
        // GameManager에 임시 저장된 현재 매치 순위/킬 수 데이터를 가져옵니다.
        // 이 데이터를 기반으로 Supabase DB의 match_history 테이블에 승/패 기록을 Update합니다.
        
        bool isWinner = false; // 실제로는 게임 로직에서 받아옴
        
        if (resultTitleText != null)
        {
            if (isWinner)
            {
                resultTitleText.text = "<color=#f1c40f>최후의 1인! 승리했습니다.</color>";
                // 승리 이력 DB 저장 로직...
            }
            else
            {
                resultTitleText.text = "<color=#e74c3c>사망했습니다.</color>";
                // 패배 이력 DB 저장 로직...
            }
        }
    }

    public void OnClickSendChat()
    {
        if (chatInputField != null && !string.IsNullOrEmpty(chatInputField.text))
        {
            NetworkManager.Instance.SendChatMessage(chatInputField.text);
            chatInputField.text = "";
        }
    }

    private void UpdateChatUI(string message)
    {
        if (chatLogText != null)
            chatLogText.text += message + "\n";
    }

    // 7. 로비로 복귀
    public void OnClickExitToLobby()
    {
        if (GameManager.Instance != null)
        {
            GameManager.Instance.ResetForNewMatch();
        }
    }
}
