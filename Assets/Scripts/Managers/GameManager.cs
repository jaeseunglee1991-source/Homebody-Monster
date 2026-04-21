using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Player Data")]
    public string currentPlayerId;
    public CharacterData myCharacterData; // 이전에 구성한 로우스탯 기반 데이터

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
        // 체력 등 일회성 전투 데이터 초기화 로직
        if (myCharacterData != null)
        {
            myCharacterData.currentHp = myCharacterData.maxHp;
        }
        LoadScene("LobbyScene");
    }
}
