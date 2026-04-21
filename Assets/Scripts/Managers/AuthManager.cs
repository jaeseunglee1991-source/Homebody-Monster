using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;
using System.Collections.Generic;

public class AuthManager : MonoBehaviour
{
    public static AuthManager Instance { get; private set; }

    [Header("UI 연결 (Inspector에서 할당)")]
    public GameObject loginPanel;      // [빠른 시작] 버튼 패널
    public GameObject nicknamePanel;   // 닉네임 입력 패널
    public InputField nicknameInput;   // 닉네임 입력창
    public Text errorText;             // 상태/에러 메시지

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

    /// <summary>
    /// 1. [빠른 시작] 버튼 클릭 시 호출
    /// </summary>
    public async void SignInAsGuest()
    {
        if (errorText != null) errorText.text = "서버 연결 중...";
        
        try
        {
            if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
            {
                Debug.LogError("SupabaseManager가 초기화되지 않았습니다.");
                return;
            }

            var session = await SupabaseManager.Instance.Client.Auth.SignInAnonymously();

            if (session != null && session.User != null)
            {
                Debug.Log($"✅ 계정 생성 성공: {session.User.Id}");
                
                // 트리거가 프로필을 생성할 시간을 위해 아주 잠깐 대기
                await Task.Delay(500); 
                await CheckUserFlow(session.User.Id);
            }
        }
        catch (System.Exception e)
        {
            if (errorText != null) errorText.text = "접속 실패. 인터넷을 확인하세요.";
            Debug.LogError($"Login Error: {e.Message}");
        }
    }

    /// <summary>
    /// 2. 신규 유저인지 기존 유저인지 판별
    /// </summary>
    private async Task CheckUserFlow(string uid)
    {
        // SQL 트리거에 의해 이미 자동 생성된 프로필 조회
        var profile = await SupabaseManager.Instance.GetOrCreateProfile(uid);

        if (profile != null)
        {
            // 닉네임이 'NewPlayer_'로 시작하면 아직 닉네임을 설정하지 않은 신규 유저로 간주
            if (profile.Nickname.StartsWith("NewPlayer_"))
            {
                if (loginPanel != null) loginPanel.SetActive(false);
                if (nicknamePanel != null) nicknamePanel.SetActive(true);
                if (errorText != null) errorText.text = "사용하실 닉네임을 입력해주세요!";
            }
            else
            {
                // 이미 고유 닉네임이 있는 경우 바로 로비로
                EnterLobby(profile.Nickname);
            }
        }
        else
        {
            if (errorText != null) errorText.text = "프로필을 불러오지 못했습니다. 다시 시도해주세요.";
        }
    }

    /// <summary>
    /// 3. [닉네임 결정] 버튼 클릭 시 호출
    /// </summary>
    public async void SubmitNickname()
    {
        string newName = nicknameInput.text.Trim();
        if (newName.Length < 2) 
        {
            if (errorText != null) errorText.text = "닉네임은 최소 2글자 이상이어야 합니다.";
            return; 
        }

        if (errorText != null) errorText.text = "닉네임 저장 중...";

        try
        {
            string uid = SupabaseManager.Instance.Client.Auth.CurrentUser.Id;
            
            // 기존 프로필을 닉네임만 새 이름으로 업데이트 (SQL 트리거 충돌 방지)
            var profileToUpdate = new PlayerProfile { Id = uid, Nickname = newName };
            await SupabaseManager.Instance.Client.From<PlayerProfile>().Update(profileToUpdate);

            Debug.Log($"✅ 닉네임 설정 완료: {newName}");
            EnterLobby(newName);
        }
        catch (System.Exception e)
        {
            // 닉네임 유니크 제약 조건 위반 시 등 에러 처리
            if (e.Message.Contains("unique")) 
                if (errorText != null) errorText.text = "이미 존재하는 닉네임입니다.";
            else 
                if (errorText != null) errorText.text = "저장 실패. 다시 시도해 주세요.";
            
            Debug.LogError($"Nickname Update Error: {e.Message}");
        }
    }

    private void EnterLobby(string nickname)
    {
        if (GameManager.Instance != null)
        {
            GameManager.Instance.currentPlayerId = nickname;
            GameManager.Instance.LoadScene("LobbyScene");
        }
    }
}
