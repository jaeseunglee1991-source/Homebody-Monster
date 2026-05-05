using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

/// <summary>
/// 매칭 중 UX를 담당하는 UI 컴포넌트.
/// MatchmakingManager의 이벤트를 구독하여 단계별 안내, 타임아웃 처리,
/// 취소 확인 팝업, 매칭 성공 카운트다운을 제공합니다.
/// LobbyUIController와 별도로 동작합니다.
/// </summary>
public class MatchmakingUX : MonoBehaviour
{
    public static MatchmakingUX Instance { get; private set; }

    // ── Inspector ───────────────────────────────────────────────
    [Header("UX 루트 패널")]
    public GameObject uxPanel;                  // 매칭 중에만 활성화

    [Header("단계별 안내 텍스트")]
    public TextMeshProUGUI phaseText;           // "서버 연결 중...", "대기열 등록..." 등

    [Header("타임아웃 / 경과 시간")]
    public TextMeshProUGUI elapsedText;         // "00:42"
    public Slider          progressSlider;      // 0 ~ 120s

    [Header("취소 확인 팝업")]
    public GameObject confirmCancelPanel;
    public Button     confirmCancelYesButton;
    public Button     confirmCancelNoButton;
    public Button     cancelRequestButton;      // "취소" 버튼 → 확인 팝업 열기

    [Header("매칭 성공 카운트다운")]
    public GameObject countdownPanel;
    public TextMeshProUGUI countdownText;       // "5", "4", "3", "2", "1"

    // ── 상수 ────────────────────────────────────────────────────
    private const float TimeoutSeconds  = 120f;
    private const float PhaseInterval   = 30f;  // 단계 메시지 교체 간격

    private static readonly string[] PhaseMessages =
    {
        "매칭 서버에 연결 중...",
        "대기열에서 상대를 찾는 중...",
        "조건에 맞는 플레이어를 탐색 중...",
        "곧 게임이 시작됩니다. 잠시만 기다려 주세요."
    };

    // ── 내부 상태 ────────────────────────────────────────────────
    private Coroutine _uxCoroutine;
    private bool      _matchFound;
    // [버그 수정] OnMatchFound에서 받은 접속 정보를 카운트다운 후 사용하기 위해 캐시.
    // MatchmakingManager.HandleMatchSuccess가 MatchmakingUX 존재 시 ConnectToGameServer를
    // 호출하지 않으므로 카운트다운 종료 시 여기서 직접 호출해야 함.
    private string    _pendingIp   = null;
    private ushort    _pendingPort = AppNetworkManager.DefaultPort;
    // 이벤트 구독 해제용 캐시 (람다 재사용)
    private System.Action _onMatchFailedDelegate;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        HideAll();

        if (cancelRequestButton != null)
            cancelRequestButton.onClick.AddListener(OnCancelButtonClicked);
        if (confirmCancelYesButton != null)
            confirmCancelYesButton.onClick.AddListener(OnConfirmCancelYes);
        if (confirmCancelNoButton != null)
            confirmCancelNoButton.onClick.AddListener(OnConfirmCancelNo);

        // MatchmakingManager 이벤트 구독
        _onMatchFailedDelegate = () => OnMatchFailed();
        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnMatchFound        += OnMatchFound;
            MatchmakingManager.Instance.OnMatchmakingFailed += _onMatchFailedDelegate;
        }
    }

    private void OnDestroy()
    {
        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnMatchFound        -= OnMatchFound;
            MatchmakingManager.Instance.OnMatchmakingFailed -= _onMatchFailedDelegate;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 매칭 시작 시 LobbyUIController 또는 버튼에서 호출합니다.
    /// </summary>
    public void StartMatchmakingUX()
    {
        _matchFound = false;
        HideAll();
        uxPanel?.SetActive(true);
        confirmCancelPanel?.SetActive(false);
        countdownPanel?.SetActive(false);

        if (_uxCoroutine != null) StopCoroutine(_uxCoroutine);
        _uxCoroutine = StartCoroutine(UXRoutine());
    }

    /// <summary>
    /// 매칭 성공 이벤트 핸들러 (MatchmakingManager.OnMatchFound).
    /// ip/port를 캐시해두고 카운트다운 후 ConnectToGameServer를 호출합니다.
    ///
    /// [버그 수정] ConnectToGameServer 호출 누락.
    /// MatchmakingManager.HandleMatchSuccess의 폴백 조건
    ///   (FindAnyObjectByType&lt;MatchmakingUX&gt;() == null)이
    /// MatchmakingUX 존재 시 false가 되어 ConnectToGameServer가 호출되지 않았음.
    /// MatchFoundCountdown 종료 시 여기서 직접 호출하도록 수정.
    /// </summary>
    public void OnMatchFound(string ip, ushort port)
    {
        _matchFound  = true;
        _pendingIp   = ip;
        _pendingPort = port;
        if (_uxCoroutine != null) { StopCoroutine(_uxCoroutine); _uxCoroutine = null; }
        StartCoroutine(MatchFoundCountdown());
    }

    /// <summary>
    /// 매칭 실패 이벤트 핸들러 (MatchmakingManager.OnMatchmakingFailed).
    /// </summary>
    public void OnMatchFailed(string reason = "")
    {
        if (_uxCoroutine != null) { StopCoroutine(_uxCoroutine); _uxCoroutine = null; }
        SetPhaseText("매칭에 실패했습니다." + (string.IsNullOrEmpty(reason) ? "" : $"\n({reason})"));
        Invoke(nameof(HideAll), 3f);
    }

    // ════════════════════════════════════════════════════════════
    //  내부 코루틴
    // ════════════════════════════════════════════════════════════

    private IEnumerator UXRoutine()
    {
        float elapsed    = 0f;
        int   phaseIndex = 0;
        SetPhaseText(PhaseMessages[0]);

        while (elapsed < TimeoutSeconds && !_matchFound)
        {
            elapsed += Time.deltaTime;

            // 경과 시간 표시
            int   m = Mathf.FloorToInt(elapsed / 60f);
            int   s = Mathf.FloorToInt(elapsed % 60f);
            if (elapsedText != null)    elapsedText.text = $"{m:D2}:{s:D2}";
            if (progressSlider != null) progressSlider.value = elapsed / TimeoutSeconds;

            // 단계별 메시지 교체
            int newPhase = Mathf.Min(Mathf.FloorToInt(elapsed / PhaseInterval), PhaseMessages.Length - 1);
            if (newPhase != phaseIndex)
            {
                phaseIndex = newPhase;
                SetPhaseText(PhaseMessages[phaseIndex]);
            }

            yield return null;
        }

        if (!_matchFound)
        {
            // 120초 타임아웃
            SetPhaseText("매칭 시간이 초과되었습니다.\n잠시 후 다시 시도해 주세요.");
            MatchmakingManager.Instance?.CancelSearch();
            yield return new WaitForSeconds(3f);
            HideAll();
        }
    }

    private IEnumerator MatchFoundCountdown()
    {
        uxPanel?.SetActive(false);
        countdownPanel?.SetActive(true);

        for (int i = 5; i >= 1; i--)
        {
            if (countdownText != null) countdownText.text = i.ToString();
            yield return new WaitForSeconds(1f);
        }
        countdownPanel?.SetActive(false);

        // [버그 수정] 카운트다운 종료 후 게임 서버 접속 호출.
        // MatchmakingManager.HandleMatchSuccess의 폴백 조건이 MatchmakingUX 존재 시
        // ConnectToGameServer를 건너뛰므로, 여기서 직접 호출해야 함.
        // _pendingIp가 null이면 HandleMatchSuccess가 이미 저장한 GameManager 캐시로 복구.
        string ip   = !string.IsNullOrEmpty(_pendingIp)
                        ? _pendingIp
                        : GameManager.Instance?.gameServerIp;
        ushort port = _pendingPort != 0
                        ? _pendingPort
                        : (GameManager.Instance?.gameServerPort ?? AppNetworkManager.DefaultPort);

        if (!string.IsNullOrEmpty(ip))
            AppNetworkManager.Instance?.ConnectToGameServer(ip, port);
        else
            Debug.LogError("[MatchmakingUX] 게임 서버 IP를 알 수 없어 접속 불가.");
    }

    // ════════════════════════════════════════════════════════════
    //  취소 팝업
    // ════════════════════════════════════════════════════════════

    private void OnCancelButtonClicked()
    {
        confirmCancelPanel?.SetActive(true);
    }

    private void OnConfirmCancelYes()
    {
        confirmCancelPanel?.SetActive(false);
        if (_uxCoroutine != null) { StopCoroutine(_uxCoroutine); _uxCoroutine = null; }
        MatchmakingManager.Instance?.CancelSearch();
        HideAll();
    }

    private void OnConfirmCancelNo()
    {
        confirmCancelPanel?.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  헬퍼
    // ════════════════════════════════════════════════════════════

    private void HideAll()
    {
        uxPanel?.SetActive(false);
        confirmCancelPanel?.SetActive(false);
        countdownPanel?.SetActive(false);
    }

    private void SetPhaseText(string msg)
    {
        if (phaseText != null) phaseText.text = msg;
    }
}
