using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

/// <summary>
/// 인게임 도중 연결이 끊겼을 때 자동 재접속을 시도합니다.
/// AppNetworkManager.OnClientDisconnected 이벤트를 구독합니다.
///
/// 동작:
///  - 최대 5회 / 4초 간격으로 재접속 시도
///  - 성공 시 인게임 상태 복귀
///  - 30초 타임아웃(5회 실패) 시 로비 씬으로 이동
/// </summary>
public class ReconnectManager : MonoBehaviour
{
    public static ReconnectManager Instance { get; private set; }

    // ── Inspector ───────────────────────────────────────────────
    [Header("재접속 설정")]
    public int   maxRetries     = 5;
    public float retryInterval  = 4f;   // 초

    [Header("UI (선택)")]
    public GameObject reconnectPanel;
    public UnityEngine.UI.Slider  progressSlider;
    public TMPro.TextMeshProUGUI  statusText;

    // ── 내부 상태 ────────────────────────────────────────────────
    private string    _lastIp;
    private ushort    _lastPort;
    private Coroutine _reconnectCoroutine;
    private bool      _isReconnecting;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        if (AppNetworkManager.Instance != null)
            AppNetworkManager.Instance.OnClientDisconnected += OnClientDisconnected;
    }

    private void OnDisable()
    {
        if (AppNetworkManager.Instance != null)
            AppNetworkManager.Instance.OnClientDisconnected -= OnClientDisconnected;
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 접속 정보를 저장해 재접속에 사용합니다.
    /// AppNetworkManager.ConnectToGameServer() 직전에 호출하세요.
    /// </summary>
    public void RegisterServer(string ip, ushort port)
    {
        _lastIp   = ip;
        _lastPort = port;
    }

    /// <summary>
    /// 현재 재접속 시도를 중단합니다.
    /// </summary>
    public void Cancel()
    {
        if (_reconnectCoroutine != null)
        {
            StopCoroutine(_reconnectCoroutine);
            _reconnectCoroutine = null;
        }
        _isReconnecting = false;
        reconnectPanel?.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  이벤트 핸들러
    // ════════════════════════════════════════════════════════════

    private void OnClientDisconnected(string reason)
    {
        // 인게임 씬에서만 재접속 시도
        if (SceneManager.GetActiveScene().name != "InGameScene") return;
        if (_isReconnecting) return;
        if (string.IsNullOrEmpty(_lastIp)) return;

        Debug.Log($"[ReconnectManager] 연결 끊김({reason}). 재접속 시도 시작.");
        _reconnectCoroutine = StartCoroutine(ReconnectRoutine());
    }

    // ════════════════════════════════════════════════════════════
    //  재접속 코루틴
    // ════════════════════════════════════════════════════════════

    private IEnumerator ReconnectRoutine()
    {
        _isReconnecting = true;
        reconnectPanel?.SetActive(true);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            SetStatus($"재접속 시도 중... ({attempt}/{maxRetries})");
            if (progressSlider != null)
                progressSlider.value = (float)(attempt - 1) / maxRetries;

            // 재접속 시도
            if (AppNetworkManager.Instance != null)
                AppNetworkManager.Instance.ConnectToGameServer(_lastIp, _lastPort);

            // retryInterval 동안 접속 성공 여부 체크
            float waited = 0f;
            while (waited < retryInterval)
            {
                waited += Time.deltaTime;
                var netMgr = Unity.Netcode.NetworkManager.Singleton;
                if (netMgr != null && netMgr.IsConnectedClient)
                {
                    Debug.Log($"[ReconnectManager] 재접속 성공 (시도 {attempt}회).");
                    OnReconnectSuccess();
                    yield break;
                }
                yield return null;
            }

            Debug.LogWarning($"[ReconnectManager] 재접속 실패 ({attempt}/{maxRetries}).");
        }

        // 전부 실패 → 로비
        Debug.LogError("[ReconnectManager] 재접속 최대 횟수 초과. 로비로 이동합니다.");
        OnReconnectFailed();
    }

    private void OnReconnectSuccess()
    {
        _isReconnecting = false;
        reconnectPanel?.SetActive(false);
        SetStatus("");
    }

    private void OnReconnectFailed()
    {
        _isReconnecting = false;
        reconnectPanel?.SetActive(false);
        _lastIp = null;

        // NGO 연결 정리 후 로비 씬 이동
        var netMgr = Unity.Netcode.NetworkManager.Singleton;
        if (netMgr != null && (netMgr.IsClient || netMgr.IsServer))
            netMgr.Shutdown();

        GameManager.Instance?.LoadScene("LobbyScene");
    }

    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }
}
