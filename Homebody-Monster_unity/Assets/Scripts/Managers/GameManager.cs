using UnityEngine;
using UnityEngine.SceneManagement;

[System.Serializable]
public struct MatchResult
{
    public bool isWinner;
    public int rank;
    public int killCount;
    public float survivedTime;
}

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Player Data")]
    public string currentPlayerId;
    public CharacterData myCharacterData; 

    [Header("Match Info")]
    public string currentRoomId;        // 매칭된 방 식별자 ("ip:port" 형태)
    public string gameServerIp;         // 파싱된 게임 서버 IP
    public ushort gameServerPort;       // 파싱된 게임 서버 포트
    public MatchResult lastMatchResult; // InGameManager에서 저장, ResultController에서 사용

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    public void LoadScene(string sceneName)
    {
        SceneManager.LoadScene(sceneName);
    }

    public void ResetForNewMatch()
    {
        if (myCharacterData != null)
        {
            myCharacterData.currentHp = myCharacterData.maxHp;
        }
        
        currentRoomId   = null;
        gameServerIp    = null;
        gameServerPort  = 0;
        lastMatchResult = default;
        AppNetworkManager.Instance?.Disconnect();
        LoadScene("LobbyScene");
    }
}
