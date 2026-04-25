using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using TMPro;

/// <summary>
/// 로그인 씬 전체를 관장합니다.
///
/// 수정 사항:
///  1. 앱 재실행 시 기존 Supabase 세션 자동 복원 → 로비 직행
///  2. 구글 로그인 연동 (GoogleSignInManager 위임)
///  3. 버튼 중복 탭 방지 (로딩 중 전체 버튼 비활성화)
///  4. 닉네임 유효성: 2~12자, 한/영/숫자만, 공백 금지
///  5. UI 상태 관리 (로딩 패널, 에러 텍스트 초기화)
/// </summary>
public class AuthManager : MonoBehaviour
{
    public static AuthManager Instance { get; private set; }

    [Header("패널")]
    public GameObject loginPanel;
    public GameObject loadingPanel;    // 로딩 스피너 패널 (Inspector 연결)
    public GameObject nicknamePanel;

    [Header("로그인 버튼")]
    public Button guestLoginButton;
    public Button googleLoginButton;   // 구글 로그인 버튼 (Inspector 연결)

    [Header("닉네임")]
    public TMP_InputField nicknameInput;
    public Button submitNicknameButton;

    [Header("공통 UI")]
    public TMP_Text errorText;

    // 닉네임 규칙: 2~12자, 한글/영문/숫자만, 앞뒤 공백 금지
    private static readonly Regex NicknameRegex = new Regex(
        @"^[가-힣a-zA-Z0-9]{2,12}$",
        RegexOptions.Compiled
    );

    private bool _isBusy = false;

    // ════════════════════════════════════════════════════════════
    //  초기화
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); return; }
    }

    private async void Start()
    {
        SetPanelState(loading: true);
        await TryRestoreSession();
    }

    // ════════════════════════════════════════════════════════════
    //  ① 세션 자동 복원 (앱 재실행 시)
    // ════════════════════════════════════════════════════════════

    private async Task TryRestoreSession()
    {
        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
        {
            ShowError("서버 연결에 실패했습니다. 인터넷을 확인해주세요.");
            SetPanelState(loading: false);
            return;
        }

        try
        {
            // Supabase SDK가 저장된 refresh token으로 세션을 자동 복원함
            var currentUser = SupabaseManager.Instance.Client.Auth.CurrentUser;

            if (currentUser != null)
            {
                Debug.Log($"[Auth] 기존 세션 복원 성공: {currentUser.Id}");
                await CheckUserFlow(currentUser.Id);
                return;
            }
        }
        catch (System.Exception e)
        {
            Debug.Log($"[Auth] 세션 없음, 로그인 화면 표시: {e.Message}");
        }

        // 세션 없음 → 로그인 화면
        SetPanelState(loading: false);
    }

    // ════════════════════════════════════════════════════════════
    //  ② 게스트 로그인
    // ════════════════════════════════════════════════════════════

    public async void SignInAsGuest()
    {
        Debug.Log("[Auth] Guest Button Clicked!");
        if (_isBusy) return;
        if (!ValidateSupabase()) 
        {
            Debug.LogWarning("[Auth] Supabase validation failed.");
            return;
        }

        SetBusy(true, "서버 연결 중...");
        Debug.Log("[Auth] Attempting SignInAnonymously...");

        try
        {
            var task = SupabaseManager.Instance.Client.Auth.SignInAnonymously();
            var session = await task;

            Debug.Log("[Auth] SignInAnonymously call finished.");

            if (session?.User != null)
            {
                Debug.Log($"[Auth] 게스트 계정 생성 완료: {session.User.Id}");
                Debug.Log("[Auth] Calling CheckUserFlow now...");
                await CheckUserFlow(session.User.Id);
                Debug.Log("[Auth] CheckUserFlow finished.");
            }
            else
            {
                Debug.LogWarning("[Auth] Session or User is null.");
                ShowError("로그인에 실패했습니다. 다시 시도해주세요.");
            }
        }
        catch (System.Exception e)
        {
            ShowError("접속 실패. 인터넷을 확인해주세요.");
            Debug.LogError($"[Auth] 게스트 로그인 오류: {e.GetType()} - {e.Message}\n{e.StackTrace}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  ③ 구글 로그인 (GoogleSignInManager 위임)
    // ════════════════════════════════════════════════════════════

    /*
    public async void SignInWithGoogle()
    {
        if (_isBusy) return;
        if (!ValidateSupabase()) return;

        SetBusy(true, "구글 로그인 중...");

        try
        {
            // GoogleSignInManager가 Google ID Token을 가져와 Supabase에 전달
            string idToken = await GoogleSignInManager.Instance.GetGoogleIdTokenAsync();

            if (string.IsNullOrEmpty(idToken))
            {
                ShowError("구글 로그인이 취소되었습니다.");
                return;
            }

            var session = await SupabaseManager.Instance.Client.Auth.SignInWithIdToken(
                Supabase.Gotrue.Constants.Provider.Google, idToken
            );

            if (session?.User != null)
            {
                Debug.Log($"[Auth] 구글 로그인 성공: {session.User.Id}");
                await CheckUserFlow(session.User.Id);
            }
            else
            {
                ShowError("구글 로그인에 실패했습니다.");
            }
        }
        catch (System.Exception e)
        {
            ShowError("구글 로그인 실패. 다시 시도해주세요.");
            Debug.LogError($"[Auth] 구글 로그인 오류: {e.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }
    */

    // ════════════════════════════════════════════════════════════
    //  공통: 프로필 확인 → 닉네임 설정 또는 로비 진입
    // ════════════════════════════════════════════════════════════

    private async Task CheckUserFlow(string uid)
    {
        Debug.Log($"[Auth] Checking flow for UID: {uid}");
        if (string.IsNullOrEmpty(uid)) { ShowError("사용자 정보를 불러오지 못했습니다."); return; }

        try
        {
            Debug.Log("[Auth] Fetching profile...");
            var profile = await SupabaseManager.Instance.GetOrCreateProfile(uid);

            if (profile == null)
            {
                Debug.LogError("[Auth] Profile is null after GetOrCreateProfile");
                ShowError("프로필을 불러오지 못했습니다. 다시 시도해주세요.");
                SetPanelState(loading: false);
                return;
            }

            Debug.Log($"[Auth] Profile found. Nickname: {profile.Nickname}");

            bool needsNickname = string.IsNullOrEmpty(profile.Nickname)
                              || profile.Nickname.StartsWith("NewPlayer_");

            if (needsNickname)
            {
                Debug.Log("[Auth] User needs to set a nickname.");
                SetBusy(false); // 로딩 상태 해제
                ShowNicknamePanel();
            }
            else
            {
                Debug.Log("[Auth] Valid profile found. Entering lobby.");
                EnterLobby(profile.Nickname);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Auth] CheckUserFlow error: {e.Message}");
            ShowError("데이터 확인 중 오류가 발생했습니다.");
            SetPanelState(loading: false);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  ④ 닉네임 설정 — 강화된 유효성 검사
    // ════════════════════════════════════════════════════════════

    public async void SubmitNickname()
    {
        Debug.Log("[Auth] Submit Nickname Button Clicked!");
        if (_isBusy) 
        {
            Debug.LogWarning("[Auth] SubmitNickname ignored because _isBusy is true");
            return;
        }

        string raw = nicknameInput != null ? nicknameInput.text : "";
        string newName = raw.Trim();

        // 클라이언트 사전 검증
        if (!NicknameRegex.IsMatch(newName))
        {
            if (newName.Length < 2)
                ShowError("닉네임은 최소 2글자 이상이어야 합니다.");
            else if (newName.Length > 12)
                ShowError("닉네임은 최대 12글자까지 가능합니다.");
            else
                ShowError("닉네임은 한글, 영문, 숫자만 사용 가능합니다.");
            return;
        }

        SetBusy(true, "닉네임 확인 중...");

        try
        {
            bool isAvailable = await SupabaseManager.Instance.IsNicknameAvailable(newName);
            if (!isAvailable)
            {
                ShowError("이미 사용 중이거나 사용할 수 없는 닉네임입니다.");
                return;
            }

            string uid = SupabaseManager.Instance.Client.Auth.CurrentUser?.Id;
            if (string.IsNullOrEmpty(uid))
            {
                ShowError("세션이 만료되었습니다. 다시 로그인해주세요.");
                SetPanelState(loading: false);
                return;
            }

            await SupabaseManager.Instance.Client
                .From<UserProfile>()
                .Where(x => x.Id == uid)
                .Set(x => x.Nickname, newName)
                .Update();

            Debug.Log($"[Auth] 닉네임 설정 완료: {newName}");
            EnterLobby(newName);
        }
        catch (System.Exception e)
        {
            ShowError("저장에 실패했습니다. 다시 시도해주세요.");
            Debug.LogError($"[Auth] 닉네임 저장 오류: {e.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  UI 상태 관리
    // ════════════════════════════════════════════════════════════

    private void SetPanelState(bool loading)
    {
        if (loadingPanel  != null) loadingPanel.SetActive(loading);
        if (loginPanel    != null) loginPanel.SetActive(!loading);
        if (nicknamePanel != null) nicknamePanel.SetActive(false);
        ClearError();
    }

    private void ShowNicknamePanel()
    {
        Debug.Log($"[Auth] ShowNicknamePanel called. Panel is null? {nicknamePanel == null}");
        if (loadingPanel  != null) loadingPanel.SetActive(false);
        if (loginPanel    != null) loginPanel.SetActive(false);
        
        if (nicknamePanel != null) 
        {
            nicknamePanel.SetActive(true);
            Debug.Log("[Auth] Nickname Panel activated!");
        }
        else
        {
            Debug.LogError("[Auth] Nickname Panel is NOT connected in Inspector!");
        }

        ShowError("사용하실 닉네임을 입력해주세요!");
    }

    /// <summary>로딩 중 모든 버튼을 비활성화하여 중복 탭을 방지합니다.</summary>
    private void SetBusy(bool busy, string message = "")
    {
        _isBusy = busy;
        if (guestLoginButton    != null) guestLoginButton.interactable    = !busy;
        if (googleLoginButton   != null) googleLoginButton.interactable   = !busy;
        if (submitNicknameButton != null) submitNicknameButton.interactable = !busy;

        if (busy && !string.IsNullOrEmpty(message))
            ShowError(message);
        else if (!busy)
            ClearError();
    }

    private void ShowError(string msg)
    {
        if (errorText != null) errorText.text = msg;
    }

    private void ClearError()
    {
        if (errorText != null) errorText.text = "";
    }

    private void EnterLobby(string nickname)
    {
        if (GameManager.Instance != null)
        {
            GameManager.Instance.currentPlayerId = nickname;
            GameManager.Instance.LoadScene("LobbyScene");
        }
        else
        {
            Debug.LogError("[Auth] GameManager.Instance가 없습니다.");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  내부 유틸
    // ════════════════════════════════════════════════════════════

    private bool ValidateSupabase()
    {
        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
        {
            ShowError("서버에 연결되지 않았습니다. 잠시 후 다시 시도해주세요.");
            return false;
        }
        return true;
    }
}
