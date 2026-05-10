using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

/// <summary>
/// 로비씬 설정 패널. 좌측 상단 플레이어명 버튼 클릭 시 오픈.
///
/// ─ 기존 코드 연동 ─────────────────────────────────────────────
///  SettingsManager    : SetBgmVolume / SetSfxVolume / SetTargetFPS / SetLowResolution / SaveAll
///  AudioManager       : PlayButtonClick
///  LobbyUIController  : RefreshUserProfileUI → OnProfileRefreshed 콜백 연동 (적용 완료)
///  MatchmakingManager : IsSearching, CancelMatchmaking — 로그아웃 전 매칭 취소
///  AppNetworkManager  : DisconnectAsync — Presence 해제 + NGO Shutdown
///  SupabaseManager    : Client.Auth.SignOut
///  GameManager        : currentPlayerNickname / currentPlayerId / reviveTicketCount 초기화
///
/// ─ Inspector 연결 체크리스트 ──────────────────────────────────
///  [패널]
///   settingsPanel      : CanvasGroup 컴포넌트 추가 필수 (Interactable=true, BlocksRaycasts=true)
///   dimBackground      : Image + Button 컴포넌트 (Raycast Target ON)
///   closeButton        : X 닫기 버튼
///
///  [트리거]
///   playerNameButton   : TopBar 좌측 플레이어명 버튼
///
///  [볼륨]  슬라이더 Min=0, Max=1
///   masterVolumeSlider / bgmVolumeSlider / sfxVolumeSlider
///   masterVolumeText   / bgmVolumeText   / sfxVolumeText
///
///  [계정]
///   loggedInNameText   : 현재 닉네임 TMP
///   logoutButton
///
///  [게임 설정]
///   fps30Toggle / fps60Toggle / fps90Toggle → 동일 ToggleGroup, Allow Switch Off: false
///   lowResToggle
///   quitGameButton
///
///  [확인 팝업]
///   confirmLogoutPanel / confirmLogoutYes / confirmLogoutNo
///   confirmQuitPanel   / confirmQuitYes   / confirmQuitNo
/// </summary>
public class LobbySettingsPanel : MonoBehaviour
{
    public static LobbySettingsPanel Instance { get; private set; }

    // ── 패널 ──────────────────────────────────────────────────
    [Header("패널 루트")]
    public GameObject settingsPanel;   // CanvasGroup 컴포넌트 필수
    public GameObject dimBackground;
    public Button     closeButton;

    // ── 트리거 ────────────────────────────────────────────────
    [Header("오픈 트리거 (TopBar 플레이어명 버튼)")]
    public Button playerNameButton;

    // ── 계정 ──────────────────────────────────────────────────
    [Header("계정 섹션")]
    public TextMeshProUGUI loggedInNameText;
    public Button          logoutButton;

    // ── 볼륨 ──────────────────────────────────────────────────
    [Header("볼륨 섹션 (Slider Min=0 Max=1)")]
    public Slider          masterVolumeSlider;
    public Slider          bgmVolumeSlider;
    public Slider          sfxVolumeSlider;
    public TextMeshProUGUI masterVolumeText;
    public TextMeshProUGUI bgmVolumeText;
    public TextMeshProUGUI sfxVolumeText;

    // ── 게임 설정 ─────────────────────────────────────────────
    [Header("게임 설정 섹션")]
    public Toggle fps30Toggle;
    public Toggle fps60Toggle;
    public Toggle fps90Toggle;
    public Toggle lowResToggle;
    public Button quitGameButton;

    // ── 확인 팝업 ─────────────────────────────────────────────
    [Header("로그아웃 확인 팝업")]
    public GameObject confirmLogoutPanel;
    public Button     confirmLogoutYes;
    public Button     confirmLogoutNo;

    [Header("게임 종료 확인 팝업")]
    public GameObject confirmQuitPanel;
    public Button     confirmQuitYes;
    public Button     confirmQuitNo;

    // ── 애니메이션 ────────────────────────────────────────────
    [Header("열기/닫기 애니메이션 시간(초)")]
    [Range(0.1f, 0.4f)]
    public float animDuration = 0.2f;

    // ── 마스터 볼륨 ───────────────────────────────────────────
    // SettingsManager에 없으므로 AudioListener.volume + PlayerPrefs로 별도 관리
    private const string KEY_MASTER_VOLUME = "Setting_MasterVolume";

    private bool      _isOpen        = false;
    private bool      _isAnimating   = false;  // 애니메이션 진행 중 여부
    private bool      _isLoggingOut  = false;  // 로그아웃 비동기 처리 중 여부
    private bool      _openAfterAnim = false;  // 닫기 애니메이션 완료 후 재열기 예약 플래그
    private Coroutine _animRoutine   = null;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (settingsPanel      != null) settingsPanel.SetActive(false);
        if (dimBackground      != null) dimBackground.SetActive(false);
        if (confirmLogoutPanel != null) confirmLogoutPanel.SetActive(false);
        if (confirmQuitPanel   != null) confirmQuitPanel.SetActive(false);
    }

    private void Start()
    {
        // 패널 열기/닫기
        playerNameButton?.onClick.AddListener(OpenSettings);
        closeButton?.onClick.AddListener(CloseSettings);

        // 계정
        logoutButton?.onClick.AddListener(OnClickLogout);

        // 볼륨 슬라이더 이벤트
        masterVolumeSlider?.onValueChanged.AddListener(OnMasterVolumeChanged);
        bgmVolumeSlider?.onValueChanged.AddListener(OnBgmVolumeChanged);
        sfxVolumeSlider?.onValueChanged.AddListener(OnSfxVolumeChanged);

        // FPS 토글 — SettingsUIController와 동일 패턴 (isOn=true 때만 처리)
        fps30Toggle?.onValueChanged.AddListener(isOn => { if (isOn) SettingsManager.Instance?.SetTargetFPS(30); });
        fps60Toggle?.onValueChanged.AddListener(isOn => { if (isOn) SettingsManager.Instance?.SetTargetFPS(60); });
        fps90Toggle?.onValueChanged.AddListener(isOn => { if (isOn) SettingsManager.Instance?.SetTargetFPS(90); });
        lowResToggle?.onValueChanged.AddListener(isOn => SettingsManager.Instance?.SetLowResolution(isOn));

        // 게임 종료
        quitGameButton?.onClick.AddListener(OnClickQuitGame);

        // 확인 팝업 버튼
        confirmLogoutYes?.onClick.AddListener(ExecuteLogout);
        confirmLogoutNo?.onClick.AddListener(() => confirmLogoutPanel?.SetActive(false));
        confirmQuitYes?.onClick.AddListener(ExecuteQuitGame);
        confirmQuitNo?.onClick.AddListener(() => confirmQuitPanel?.SetActive(false));

        // [버그4 수정] RemoveAllListeners 대신 RemoveListener+AddListener
        // → Inspector에서 연결된 다른 리스너를 삭제하지 않음
        SetupDimClickClose();
    }

    // [버그6 수정] OnDestroy에서 싱글톤 참조 정리
    // 씬 전환 시 파괴된 오브젝트를 Instance가 계속 참조하는 문제 방지
    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ════════════════════════════════════════════════════════════
    //  열기 / 닫기
    // ════════════════════════════════════════════════════════════

    public void OpenSettings()
    {
        // 이미 열려있으면 무시
        if (_isOpen) return;

        // [버그7 수정] 닫기 애니메이션 진행 중이면 완료 후 자동 열기 예약
        // 빠른 재탭 시 패널이 영구적으로 응답 없는 것처럼 보이는 문제 해결
        if (_isAnimating)
        {
            _openAfterAnim = true;
            return;
        }

        _isOpen = true;
        _openAfterAnim = false;

        RefreshAccountInfo();
        SyncAllUI();

        if (settingsPanel != null) settingsPanel.SetActive(true);
        if (dimBackground != null) dimBackground.SetActive(true);

        if (_animRoutine != null) StopCoroutine(_animRoutine);
        _animRoutine = StartCoroutine(AnimatePanel(true));

        AudioManager.Instance?.PlayButtonClick();
    }

    public void CloseSettings()
    {
        if (!_isOpen) return;

        // 닫기 애니메이션이 이미 진행 중이면 차단
        if (_isAnimating && _animRoutine != null) return;

        // 확인 팝업이 열려있으면 먼저 닫기
        if (confirmLogoutPanel != null && confirmLogoutPanel.activeSelf)
        { confirmLogoutPanel.SetActive(false); return; }
        if (confirmQuitPanel != null && confirmQuitPanel.activeSelf)
        { confirmQuitPanel.SetActive(false); return; }

        _isOpen = false;
        SettingsManager.Instance?.SaveAll();   // PlayerPrefs.Save 포함

        if (_animRoutine != null) StopCoroutine(_animRoutine);
        _animRoutine = StartCoroutine(AnimatePanel(false));

        AudioManager.Instance?.PlayButtonClick();
    }

    // ════════════════════════════════════════════════════════════
    //  UI 동기화 — SettingsUIController와 동일 패턴
    //  SetValueWithoutNotify / SetIsOnWithoutNotify 로 이벤트 재호출 방지
    // ════════════════════════════════════════════════════════════

    private void SyncAllUI()
    {
        var sm = SettingsManager.Instance;
        if (sm == null) return;

        // 마스터 볼륨 (AudioListener.volume)
        float masterVol = PlayerPrefs.GetFloat(KEY_MASTER_VOLUME, 1f);
        masterVolumeSlider?.SetValueWithoutNotify(masterVol);
        SetVolumeText(masterVolumeText, masterVol);

        // BGM / SFX
        bgmVolumeSlider?.SetValueWithoutNotify(sm.BgmVolume);
        SetVolumeText(bgmVolumeText, sm.BgmVolume);
        sfxVolumeSlider?.SetValueWithoutNotify(sm.SfxVolume);
        SetVolumeText(sfxVolumeText, sm.SfxVolume);

        // FPS 토글
        fps30Toggle?.SetIsOnWithoutNotify(sm.TargetFPS == 30);
        fps60Toggle?.SetIsOnWithoutNotify(sm.TargetFPS == 60);
        fps90Toggle?.SetIsOnWithoutNotify(sm.TargetFPS == 90);

        // 저사양 모드
        lowResToggle?.SetIsOnWithoutNotify(sm.IsLowResolution);
    }

    private void RefreshAccountInfo()
    {
        if (loggedInNameText == null) return;
        string name = GameManager.Instance?.currentPlayerNickname;
        if (string.IsNullOrEmpty(name))
            name = GameManager.Instance?.currentPlayerId ?? "Unknown";
        loggedInNameText.text = name;
    }

    private static void SetVolumeText(TextMeshProUGUI tmp, float v)
    {
        if (tmp != null) tmp.text = $"{Mathf.RoundToInt(v * 100f)}%";
    }

    // ════════════════════════════════════════════════════════════
    //  볼륨 콜백
    // ════════════════════════════════════════════════════════════

    private void OnMasterVolumeChanged(float val)
    {
        // SettingsManager에 마스터 볼륨 없음 → AudioListener 직접 제어
        AudioListener.volume = val;
        PlayerPrefs.SetFloat(KEY_MASTER_VOLUME, val);
        // [버그5 수정] 슬라이더 드래그 중 강제 종료 시에도 저장되도록 즉시 Save
        PlayerPrefs.Save();
        SetVolumeText(masterVolumeText, val);
    }

    private void OnBgmVolumeChanged(float val)
    {
        // SettingsManager.SetBgmVolume → 내부에서 AudioManager.SetBgmVolume 자동 호출
        SettingsManager.Instance?.SetBgmVolume(val);
        SetVolumeText(bgmVolumeText, val);
    }

    private void OnSfxVolumeChanged(float val)
    {
        SettingsManager.Instance?.SetSfxVolume(val);
        SetVolumeText(sfxVolumeText, val);
    }

    // ════════════════════════════════════════════════════════════
    //  로그아웃
    // ════════════════════════════════════════════════════════════

    private void OnClickLogout()
    {
        if (confirmLogoutPanel != null)
            confirmLogoutPanel.SetActive(true);
        else
            ExecuteLogout();
    }

    private async void ExecuteLogout()
    {
        // [버그1 수정] _isLoggingOut으로 중복 진입 방지 (_isAnimating과 분리)
        if (_isLoggingOut) return;
        _isLoggingOut = true;

        confirmLogoutPanel?.SetActive(false);
        CloseSettingsImmediate();

        // [NEW-06] 매칭 탐색 중이면 취소 — DisconnectAsync 전에 RPC 완료를 보장
        // BUG-20: async void에서 미보호 await 예외는 UnhandledException → 앱 크래시.
        // CancelMatchmakingAsync 내부 Supabase RPC 실패도 try/catch로 흡수.
        if (MatchmakingManager.Instance != null && MatchmakingManager.Instance.IsSearching)
        {
            try { await MatchmakingManager.Instance.CancelMatchmakingAsync(); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[LobbySettingsPanel] 매칭 취소 오류 (무시): {e.Message}");
            }
        }

        // Supabase 세션 로그아웃
        if (SupabaseManager.Instance != null
            && SupabaseManager.Instance.IsInitialized
            && SupabaseManager.Instance.Client?.Auth?.CurrentUser != null)
        {
            try
            {
                await SupabaseManager.Instance.Client.Auth.SignOut();
                Debug.Log("[LobbySettingsPanel] Supabase 로그아웃 완료");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[LobbySettingsPanel] 로그아웃 오류 (무시): {e.Message}");
            }
        }

        // [버그8 수정] await 이후 씬 전환으로 this가 파괴된 경우 조기 종료
        // DailyRewardSystem, LobbyUIController 등 기존 코드베이스와 동일 패턴
        if (this == null) return;

        // GameManager 상태 초기화
        if (GameManager.Instance != null)
        {
            GameManager.Instance.currentPlayerId       = "";
            GameManager.Instance.currentPlayerNickname = "";
            GameManager.Instance.reviveTicketCount     = 0;
            GameManager.Instance.myCharacterData       = null;
        }

        // [버그1 수정] try-finally 로 씬 전환 실패/예외 시에도 플래그 반드시 해제
        try
        {
            // Presence 해제 + NGO Shutdown
            // AppNetworkManager.DisconnectAsync → DisconnectLobbyChatAsync + NGO.Shutdown
            if (AppNetworkManager.Instance != null)
                await AppNetworkManager.Instance.DisconnectAsync();

            // [버그8 수정] DisconnectAsync await 이후에도 파괴 여부 재확인
            if (this == null) return;

            GameManager.Instance?.LoadScene("LoginScene");
        }
        finally
        {
            // this가 null이면 finally는 실행되지만 멤버 접근은 불가
            // → 파괴된 MonoBehaviour의 필드 접근은 Unity에서 허용되므로 안전
            if (this != null) _isLoggingOut = false;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  게임 종료
    // ════════════════════════════════════════════════════════════

    private void OnClickQuitGame()
    {
        if (confirmQuitPanel != null)
            confirmQuitPanel.SetActive(true);
        else
            ExecuteQuitGame();
    }

    private void ExecuteQuitGame()
    {
        confirmQuitPanel?.SetActive(false);
        SettingsManager.Instance?.SaveAll();
        Debug.Log("[LobbySettingsPanel] 앱 종료");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  패널 애니메이션 (Scale + Alpha)
    // ════════════════════════════════════════════════════════════

    private IEnumerator AnimatePanel(bool opening)
    {
        _isAnimating = true;
        if (settingsPanel == null) { _isAnimating = false; yield break; }

        var cg = settingsPanel.GetComponent<CanvasGroup>();
        var rt = settingsPanel.GetComponent<RectTransform>();

        float elapsed   = 0f;
        float fromScale = opening ? 0.88f : 1f;
        float toScale   = opening ? 1f    : 0.88f;
        float fromAlpha = opening ? 0f    : 1f;
        float toAlpha   = opening ? 1f    : 0f;

        // [버그3 수정] 닫기 시작 즉시 raycasts 차단 → 애니메이션 중 클릭 이벤트 차단
        if (!opening && cg != null)
        {
            cg.interactable   = false;
            cg.blocksRaycasts = false;
        }

        while (elapsed < animDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t  = Mathf.SmoothStep(0f, 1f, elapsed / animDuration);
            if (rt != null) rt.localScale = Vector3.one * Mathf.Lerp(fromScale, toScale, t);
            if (cg != null) cg.alpha      = Mathf.Lerp(fromAlpha, toAlpha, t);
            yield return null;
        }

        if (opening)
        {
            // [버그3 수정] 열기 완료 후 클릭 가능하도록 명시적 설정
            // CanvasGroup 초기값이 false일 경우 패널이 보여도 클릭 불가 방지
            if (cg != null)
            {
                cg.interactable   = true;
                cg.blocksRaycasts = true;
            }
        }
        else
        {
            if (settingsPanel != null) settingsPanel.SetActive(false);
            if (dimBackground != null) dimBackground.SetActive(false);
        }

        _isAnimating = false;
        _animRoutine = null;

        // [버그7 수정] 닫기 완료 후 재열기가 예약되어 있으면 실행
        if (!opening && _openAfterAnim)
        {
            _openAfterAnim = false;
            OpenSettings();
        }
    }

    /// <summary>로그아웃처럼 즉시 닫아야 할 때 사용 (애니메이션 없음)</summary>
    private void CloseSettingsImmediate()
    {
        if (_animRoutine != null) { StopCoroutine(_animRoutine); _animRoutine = null; }
        _isOpen        = false;
        _isAnimating   = false;
        _openAfterAnim = false;

        // CanvasGroup 상태도 즉시 초기화
        var cg = settingsPanel != null ? settingsPanel.GetComponent<CanvasGroup>() : null;
        if (cg != null) { cg.alpha = 0f; cg.interactable = false; cg.blocksRaycasts = false; }

        if (settingsPanel      != null) settingsPanel.SetActive(false);
        if (dimBackground      != null) dimBackground.SetActive(false);
        if (confirmLogoutPanel != null) confirmLogoutPanel.SetActive(false);
        if (confirmQuitPanel   != null) confirmQuitPanel.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  Dim 클릭으로 닫기
    // ════════════════════════════════════════════════════════════

    private void SetupDimClickClose()
    {
        if (dimBackground == null) return;
        var btn = dimBackground.GetComponent<Button>() ?? dimBackground.AddComponent<Button>();
        // [버그4 수정] RemoveAllListeners 대신 RemoveListener+AddListener
        // → Inspector에서 연결된 다른 리스너를 삭제하지 않음
        btn.onClick.RemoveListener(CloseSettings);
        btn.onClick.AddListener(CloseSettings);
    }

    // ════════════════════════════════════════════════════════════
    //  외부 연동 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// LobbyUIController.RefreshUserProfileUIAsync() 완료 후 호출.
    /// 패널이 열려있을 때 계정 표시를 최신화합니다. (이미 LobbyUIController에 연동됨)
    /// </summary>
    public void OnProfileRefreshed()
    {
        if (_isOpen) RefreshAccountInfo();
    }
}
