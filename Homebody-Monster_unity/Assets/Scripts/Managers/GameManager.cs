using UnityEngine;
using UnityEngine.SceneManagement;
using System.Threading.Tasks;

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

    // ── 씬 이름 상수 ──────────────────────────────────────────────
    public const string SceneResult = "ResultScene";

    [Header("Player Data")]
    public string currentPlayerId;
    public string currentPlayerNickname; // 채팅 표시용 닉네임
    public CharacterData myCharacterData;
    public int reviveTicketCount = 0;

    [Header("Match Info")]
    public string currentRoomId;        // 매칭된 방 식별자 ("ip:port" 형태)
    public string gameServerIp;         // 파싱된 게임 서버 IP
    public ushort gameServerPort;       // 파싱된 게임 서버 포트
    public MatchResult lastMatchResult; // InGameManager에서 저장, ResultController에서 사용

    /// <summary>
    /// PlayerNetworkSync.SaveMatchResultAsync의 Task를 보관합니다.
    ///
    /// [이전 버그]
    /// NotifyMatchResultClientRpc에서 _ = SaveMatchResultAsync(...)로 fire-and-forget 실행.
    /// 씬 전환(NGO SceneManager.LoadScene)은 저장 완료를 기다리지 않으므로
    /// ResultScene 진입 시 save_match_result RPC가 아직 완료되지 않은 상태일 수 있음.
    /// ResultController.LoadAndDisplayRecord()가 GetOrCreateProfile을 호출하면
    /// 이번 매치 결과가 반영되기 전의 전적(이전 승/패 수)이 표시됨.
    ///
    /// [수정]
    /// SaveMatchResultAsync 시작 시 Task를 여기에 저장.
    /// ResultController.LoadAndDisplayRecord()에서 이 Task를 await한 후
    /// GetOrCreateProfile을 호출하여 항상 최신 전적이 표시되도록 보장.
    /// DontDestroyOnLoad 오브젝트에 저장하므로 씬 전환 후에도 접근 가능.
    /// </summary>
    [System.NonSerialized]
    public Task MatchResultSaveTask = null;

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
        LoadingScreenManager.LoadSceneAsync(sceneName);
    }

    public void ResetForNewMatch()
    {
        if (myCharacterData != null)
        {
            myCharacterData.currentHp = myCharacterData.maxHp;
        }
        
        currentRoomId       = null;
        gameServerIp        = null;
        gameServerPort      = 0;
        lastMatchResult     = default;
        MatchResultSaveTask = null;
        // [버그 수정 연동] DisconnectAsync()로 Presence 해제 완료 후 NGO Shutdown 보장.
        // 기존 Disconnect()는 fire-and-forget이라 유령 접속자가 남을 수 있었음.
        if (AppNetworkManager.Instance != null)
            _ = AppNetworkManager.Instance.DisconnectAsync();
        LoadScene("LobbyScene");
    }
}
