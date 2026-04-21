using UnityEngine;
using System.Threading.Tasks;
using Supabase.Gotrue;

public class AuthManager : MonoBehaviour
{
    public static AuthManager Instance { get; private set; }

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
    /// [로그인 화면] 게스트 로그인 버튼에 연결할 함수
    /// </summary>
    public async void SignInAsGuest()
    {
        // 1. Supabase 초기화 확인
        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
        {
            Debug.LogError("❌ SupabaseManager가 아직 준비되지 않았습니다. Inspector에서 설정을 확인하세요.");
            return;
        }

        Debug.Log("🚀 게스트 로그인 시도 중...");

        try
        {
            // 2. 익명 로그인 실행
            var session = await SupabaseManager.Instance.Client.Auth.SignInAnonymously();

            if (session != null && session.User != null)
            {
                Debug.Log($"✅ 계정 생성 성공! 유저 ID: {session.User.Id}");

                // 3. SQL 트리거가 프로필을 생성할 때까지 아주 잠시 대기 (서버 지연 대비)
                await Task.Delay(500); 

                // 4. 자동으로 생성된 프로필 정보 가져오기 (SQL 트리거 연동)
                var profile = await SupabaseManager.Instance.GetOrCreateProfile(session.User.Id);
                
                if (profile != null)
                {
                    Debug.Log($"👤 프로필 자동 생성 확인! 닉네임: {profile.Nickname}");
                    // 5. 로비 씬으로 이동
                    if (GameManager.Instance != null)
                    {
                        GameManager.Instance.LoadScene("LobbyScene");
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"❌ 게스트 로그인 실패: {e.Message}");
        }
    }
}
