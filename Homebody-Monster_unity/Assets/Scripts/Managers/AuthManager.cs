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

    // 닉네임 규칙: 2~12자, 첫 글자 한글/영문, 한글/영문/숫자/_만, 공백·기타특수문자 금지
    private static readonly Regex NicknameRegex = new Regex(
        @"^[가-힣a-zA-Z][가-힣a-zA-Z0-9_]{1,11}$",
        RegexOptions.Compiled
    );

    private bool _isBusy = false;

    // ════════════════════════════════════════════════════════════
    //  초기화
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        // AuthManager는 LoginScene 전용. 씬 전환 시 함께 파괴되도록 DontDestroyOnLoad 사용 안 함.
        if (Instance == null) { Instance = this; }
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
            // [NEW-03] 저장된 세션 복원을 먼저 시도. SignInAnonymously는 매번 새 익명 계정을
            // 생성하므로, 복원이 가능하면 기존 닉네임/전적/피자/부활권 데이터를 보존해야 한다.
            try
            {
                var restored = await SupabaseManager.Instance.Client.Auth.RetrieveSessionAsync();
                if (restored?.User != null)
                {
                    Debug.Log($"[Auth] 기존 익명 세션 복원 성공: {restored.User.Id}");
                    await CheckUserFlow(restored.User.Id);
                    return;
                }
            }
            catch (System.Exception restoreEx)
            {
                Debug.Log($"[Auth] 세션 복원 실패 → 신규 게스트 계정 생성으로 진행: {restoreEx.Message}");
            }

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
            // preserveError: true — catch/내부 return에서 ShowError한 메시지를 보존
            SetBusy(false, preserveError: true);
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
        if (string.IsNullOrEmpty(uid)) { ShowError("사용자 정보를 불러오지 못했습니다."); SetBusy(false, preserveError: true); return; }

        try
        {
            Debug.Log("[Auth] Fetching profile...");
            var profile = await SupabaseManager.Instance.GetProfile(uid);

            if (profile == null)
            {
                Debug.LogError("[Auth] Profile is null after GetProfile");
                ShowError("프로필을 불러오지 못했습니다. 다시 시도해주세요.");
                SetPanelState(loading: false);
                SetBusy(false, preserveError: true); // H-17
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
                // [버그 수정] ReviveTicketCount를 함께 전달
                EnterLobby(uid, profile.Nickname, profile.ReviveTicketCount);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Auth] CheckUserFlow error: {e.Message}");
            ShowError("데이터 확인 중 오류가 발생했습니다.");
            SetPanelState(loading: false);
            SetBusy(false, preserveError: true); // H-17
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

        if (!NicknameRegex.IsMatch(newName))
        {
            if (newName.Length < 2)
                ShowError("닉네임은 최소 2글자 이상이어야 합니다.");
            else if (newName.Length > 12)
                ShowError("닉네임은 최대 12글자까지 가능합니다.");
            else if (newName.Length >= 1 && char.IsDigit(newName[0]))
                ShowError("닉네임은 숫자로 시작할 수 없습니다.");
            else
                ShowError("닉네임은 한글, 영문, 숫자만 사용 가능합니다.");
            return;
        }

        // 금칙어 체크 (공통 ForbiddenWords.List 사용)
        string lowerName = newName.ToLower();
        foreach (string word in ForbiddenWords.List)
        {
            if (lowerName.Contains(word.ToLower()))
            {
                ShowError("사용할 수 없는 닉네임입니다.");
                return;
            }
        }

        SetBusy(true, "닉네임 중복 확인 중...");

        try
        {
            Debug.Log($"[Auth] Checking if nickname '{newName}' is available...");
            bool isAvailable = await SupabaseManager.Instance.IsNicknameAvailable(newName);
            Debug.Log($"[Auth] Nickname available: {isAvailable}");

            if (!isAvailable)
            {
                ShowError("이미 사용 중인 닉네임입니다.");
                return;
            }

            string uid = SupabaseManager.Instance.Client.Auth.CurrentUser?.Id;
            if (string.IsNullOrEmpty(uid))
            {
                ShowError("세션이 만료되었습니다. 다시 로그인해주세요.");
                SetPanelState(loading: false);
                return;
            }

            bool updated = await SupabaseManager.Instance.UpdateNickname(newName);
            if (!updated)
            {
                ShowError("저장에 실패했습니다. 다시 시도해주세요.");
                return;
            }

            Debug.Log($"[Auth] 닉네임 설정 완료: {newName}");
            // 신규 가입 최초 닉네임 설정 경로 — 부활권 0장이 정상값
            EnterLobby(uid, newName, reviveTicketCount: 0);
        }
        catch (System.Exception e)
        {
            ShowError("저장에 실패했습니다. 다시 시도해주세요.");
            Debug.LogError($"[Auth] 닉네임 저장 중 치명적 오류: {e.GetType()} - {e.Message}\n{e.StackTrace}");
        }
        finally
        {
            // preserveError: true — "이미 사용 중", "세션 만료", "저장 실패" 등
            // try 내부 ShowError + return 이후 finally가 에러 텍스트를 덮어쓰지 않도록 보존
            // NEW-05: SubmitNickname 성공 시 LobbyScene 로드로 이 컴포넌트가 파괴되어
            // SetBusy 내부의 컴포넌트 접근이 MissingReferenceException을 던질 수 있음.
            if (this != null) SetBusy(false, preserveError: true);
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
    /// <param name="busy">true = 로딩 진입, false = 로딩 해제</param>
    /// <param name="message">busy=true일 때 표시할 안내 메시지 (선택)</param>
    /// <param name="preserveError">
    /// busy=false일 때 true로 지정하면 현재 에러 텍스트를 지우지 않습니다.
    /// try 블록 안에서 ShowError + return 후 finally의 SetBusy(false)가
    /// 에러 메시지를 즉시 지워버리는 문제를 방지하기 위해 사용합니다.
    ///
    /// [이전 버그]
    /// try {
    ///     ShowError("이미 사용 중인 닉네임입니다.");
    ///     return;           ← try 탈출
    /// } finally {
    ///     SetBusy(false);   ← 반드시 실행
    ///     // → ClearError() 호출 → 방금 표시한 에러가 즉시 지워짐
    /// }
    /// </param>
    private void SetBusy(bool busy, string message = "", bool preserveError = false)
    {
        _isBusy = busy;
        if (guestLoginButton     != null) guestLoginButton.interactable     = !busy;
        if (googleLoginButton    != null) googleLoginButton.interactable    = !busy;
        if (submitNicknameButton != null) submitNicknameButton.interactable = !busy;

        if (busy && !string.IsNullOrEmpty(message))
            ShowError(message);
        else if (!busy && !preserveError)
            ClearError();
        // busy=false && preserveError=true → 에러 텍스트 유지 (아무것도 안 함)
    }

    private void ShowError(string msg)
    {
        if (errorText != null) errorText.text = msg;
    }

    private void ClearError()
    {
        if (errorText != null) errorText.text = "";
    }

    /// <summary>
    /// [버그 수정] EnterLobby에서 reviveTicketCount 미세팅 버그.
    /// 기존 코드는 uid·nickname만 GameManager에 저장하고
    /// profile.ReviveTicketCount를 반영하지 않아 로비 진입 후 항상 0으로 시작.
    /// → 인게임 부활 버튼 비활성화 + 기존 보유 티켓 사용 불가.
    /// </summary>
    private void EnterLobby(string uid, string nickname, int reviveTicketCount = 0)
    {
        if (GameManager.Instance != null)
        {
            Debug.Log($"[Auth] 로비 진입 — UID: {uid}, 닉네임: {nickname}, 부활권: {reviveTicketCount}장");
            GameManager.Instance.currentPlayerId       = uid;
            GameManager.Instance.currentPlayerNickname = nickname;
            GameManager.Instance.reviveTicketCount     = reviveTicketCount;
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
