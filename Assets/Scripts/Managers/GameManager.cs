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

    [Header("Match Result (인게임 → 결과씬 데이터 전달)")]
    public MatchResult lastMatchResult;

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

    // 씬 전환 통합 메서드
    public void LoadScene(string sceneName)
    {
        SceneManager.LoadScene(sceneName);
    }

    // 결과 씬에서 로비로 돌아갈 때 데이터 초기화
    public void ResetForNewMatch()
    {
        if (myCharacterData != null)
        {
            myCharacterData.currentHp = myCharacterData.maxHp;
        }
        
        lastMatchResult = default;
        LoadScene("LobbyScene");
    }
}
