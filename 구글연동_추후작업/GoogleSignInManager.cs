using UnityEngine;
using System.Threading.Tasks;

#if UNITY_ANDROID
using GooglePlayGames;
using GooglePlayGames.BasicApi;
#endif

/// <summary>
/// Google Play Games Services SDK를 통해 Google ID Token을 획득합니다.
///
/// ─────────────────────────────────────────────────────────────
///  사전 설정 (플레이스토어 출시 필수)
/// ─────────────────────────────────────────────────────────────
///  1. Unity Package Manager → Google Play Games Plugin for Unity 설치
///     (https://github.com/playgameservices/play-games-plugin-for-unity)
///
///  2. Google Cloud Console
///     - OAuth 2.0 클라이언트 ID 생성 (유형: Android)
///     - SHA-1 인증서 지문 입력 (Debug / Release 각각)
///     - 유형: 웹 애플리케이션 클라이언트 ID도 추가 생성
///       (이것이 아래 WEB_CLIENT_ID — Supabase에 입력하는 값)
///
///  3. Supabase Dashboard
///     - Authentication → Providers → Google → Enabled
///     - Client ID: 위의 웹 애플리케이션 클라이언트 ID 입력
///     - Client Secret: Google Cloud Console에서 복사
///
///  4. Assets/Plugins/Android/google-services.json 배치
///     (Firebase Console 또는 Google Cloud Console에서 다운로드)
///
///  5. Player Settings → Other Settings → Custom Gradle Templates 활성화
///     (Play Games Plugin이 자동으로 Gradle 의존성 추가함)
/// ─────────────────────────────────────────────────────────────
/// </summary>
public class GoogleSignInManager : MonoBehaviour
{
    public static GoogleSignInManager Instance { get; private set; }

    [Header("Google Cloud Console → 웹 애플리케이션 OAuth 클라이언트 ID")]
    [Tooltip("Supabase Auth Google Provider에 입력한 Web Client ID와 동일해야 합니다.")]
    public string webClientId = "YOUR_WEB_CLIENT_ID.apps.googleusercontent.com";

    private bool _initialized = false;

    private void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); return; }
    }

    private void Start()
    {
        InitializePlayGames();
    }

    // ════════════════════════════════════════════════════════════
    //  초기화
    // ════════════════════════════════════════════════════════════

    private void InitializePlayGames()
    {
#if UNITY_ANDROID
        var config = new PlayGamesClientConfiguration.Builder()
            .RequestIdToken()               // Supabase 인증에 필요한 ID Token 요청
            .RequestEmail()                 // 선택 사항
            .Build();

        PlayGamesPlatform.InitializeInstance(config);
        PlayGamesPlatform.DebugLogEnabled = Debug.isDebugBuild;
        PlayGamesPlatform.Activate();

        _initialized = true;
        Debug.Log("[GoogleSignIn] Google Play Games 초기화 완료");
#else
        Debug.LogWarning("[GoogleSignIn] Google Play Games는 Android에서만 지원됩니다.");
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  Google ID Token 획득 (AuthManager에서 호출)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Google Play Games SDK로 로그인하여 ID Token을 반환합니다.
    /// 실패 시 null을 반환합니다.
    /// </summary>
    public Task<string> GetGoogleIdTokenAsync()
    {
        var tcs = new TaskCompletionSource<string>();

#if UNITY_ANDROID
        if (!_initialized)
        {
            Debug.LogError("[GoogleSignIn] 초기화되지 않았습니다.");
            tcs.SetResult(null);
            return tcs.Task;
        }

        // 이미 로그인된 경우 바로 토큰 반환
        if (PlayGamesPlatform.Instance.IsAuthenticated())
        {
            string existingToken = ((PlayGamesLocalUser)Social.localUser).GetIdToken();
            if (!string.IsNullOrEmpty(existingToken))
            {
                tcs.SetResult(existingToken);
                return tcs.Task;
            }
        }

        // 새 로그인 시도
        Social.localUser.Authenticate(success =>
        {
            if (success)
            {
                string idToken = ((PlayGamesLocalUser)Social.localUser).GetIdToken();

                if (!string.IsNullOrEmpty(idToken))
                {
                    Debug.Log("[GoogleSignIn] ID Token 획득 성공");
                    tcs.SetResult(idToken);
                }
                else
                {
                    Debug.LogError("[GoogleSignIn] 로그인 성공했지만 ID Token이 null입니다. webClientId를 확인하세요.");
                    tcs.SetResult(null);
                }
            }
            else
            {
                Debug.Log("[GoogleSignIn] 사용자가 로그인을 취소했거나 실패했습니다.");
                tcs.SetResult(null);
            }
        });
#else
        // 에디터 / 비안드로이드 환경: 테스트용 더미
        Debug.LogWarning("[GoogleSignIn] Android가 아닌 환경입니다. null 반환.");
        tcs.SetResult(null);
#endif

        return tcs.Task;
    }

    // ════════════════════════════════════════════════════════════
    //  로그아웃 (필요 시 호출)
    // ════════════════════════════════════════════════════════════

    public void SignOut()
    {
#if UNITY_ANDROID
        PlayGamesPlatform.Instance.SignOut();
        Debug.Log("[GoogleSignIn] 구글 계정 로그아웃 완료");
#endif
    }
}

