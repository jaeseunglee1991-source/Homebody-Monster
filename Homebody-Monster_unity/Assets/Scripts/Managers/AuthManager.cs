using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;
using System.Collections.Generic;

public class AuthManager : MonoBehaviour
{
    public static AuthManager Instance { get; private set; }

    [Header("UI 연결 (Inspector에서 할당)")]
    public GameObject loginPanel;      
    public GameObject nicknamePanel;   
    public InputField nicknameInput;   
    public Text errorText;             

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

    private async Task CheckUserFlow(string uid)
    {
        var profile = await SupabaseManager.Instance.GetOrCreateProfile(uid);

        if (profile != null)
        {
            if (string.IsNullOrEmpty(profile.Nickname) || profile.Nickname.StartsWith("NewPlayer_"))
            {
                if (loginPanel != null) loginPanel.SetActive(false);
                if (nicknamePanel != null) nicknamePanel.SetActive(true);
                if (errorText != null) errorText.text = "사용하실 닉네임을 입력해주세요!";
            }
            else
            {
                EnterLobby(profile.Nickname);
            }
        }
        else
        {
            if (errorText != null) errorText.text = "프로필을 불러오지 못했습니다. 다시 시도해주세요.";
        }
    }

    public async void SubmitNickname()
    {
        string newName = nicknameInput.text.Trim();
        if (newName.Length < 2) 
        {
            if (errorText != null) errorText.text = "닉네임은 최소 2글자 이상이어야 합니다.";
            return; 
        }

        if (errorText != null) errorText.text = "닉네임 가능 여부 확인 중...";

        // 보안 강화: RPC를 통해 닉네임 중복 확인
        bool isAvailable = await SupabaseManager.Instance.IsNicknameAvailable(newName);
        if (!isAvailable)
        {
            if (errorText != null) errorText.text = "이미 사용 중이거나 부적절한 닉네임입니다.";
            return;
        }

        if (errorText != null) errorText.text = "닉네임 저장 중...";

        try
        {
            string uid = SupabaseManager.Instance.Client.Auth.CurrentUser.Id;
            
            // 닉네임 업데이트
            await SupabaseManager.Instance.Client.From<UserProfile>("profiles")
                .Where(x => x.Id == uid)
                .Set(x => x.Nickname, newName)
                .Update();

            Debug.Log($"✅ 닉네임 설정 완료: {newName}");
            EnterLobby(newName);
        }
        catch (System.Exception e)
        {
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
