using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 인게임 핑 HUD 컴포넌트.
///
/// [Inspector 연결]
///  • pingText           : TextMeshProUGUI
///  • signalImage        : Image
///  • signalSprites[0..3]: Excellent/Good/Poor/Critical 스프라이트 (선택)
///  • lossText           : TextMeshProUGUI (기본 비활성)
///  • highPingBanner     : GameObject + CanvasGroup 컴포넌트 추가 필요
///  • highPingBannerText : TextMeshProUGUI
///
/// [배치] InGameHUD 캔버스 좌상단, Safe Area 내 배치.
///
/// Fix-7  OnEnable/Start 이중 구독 패턴 정리 : _subscribed 플래그로 단일 진입점 보장.
/// Fix-10 Unsubscribe()에서 Instance null 시 -= 실행 불가 : _monitorRef 캐시로 해결.
/// </summary>
public class PingHUD : MonoBehaviour
{
    public static PingHUD Instance { get; private set; }

    // ── Inspector ───────────────────────────────────────────────
    [Header("핑 수치 UI")]
    public TextMeshProUGUI pingText;
    public Image           signalImage;

    [Tooltip("신호 등급별 스프라이트. 인덱스 0=Excellent 1=Good 2=Poor 3=Critical. 미설정 시 색상 틴팅만.")]
    public Sprite[] signalSprites;

    [Header("패킷 손실률 UI")]
    public TextMeshProUGUI lossText;

    [Tooltip("이 비율 이상이면 lossText를 표시합니다. (0.0 ~ 1.0)")]
    [Range(0f, 1f)] public float lossDisplayThreshold = 0.05f;

    [Header("고핑 경고 배너")]
    [Tooltip("CanvasGroup 컴포넌트를 함께 부착해야 페이드가 동작합니다.")]
    public GameObject      highPingBanner;
    public TextMeshProUGUI highPingBannerText;

    [Tooltip("배너 표시 유지 시간(초).")]
    [Range(1f, 6f)] public float bannerDuration = 3.5f;

    [Header("초기화 전 표시 텍스트")]
    public string waitingText = "-- ms";

    // ── 내부 상태 ─────────────────────────────────────────────
    private Coroutine _bannerCoroutine;
    private int       _lastRttMs = -1;

    // [Fix-7] 중복 구독 방지 플래그
    private bool _subscribed = false;

    // [Fix-10] Subscribe 시 캐시한 참조 — Instance가 null이 돼도 -= 가능
    private NetworkPingMonitor _monitorRef = null;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (pingText != null)       pingText.text = waitingText;
        if (lossText != null)       lossText.gameObject.SetActive(false);
        if (highPingBanner != null) highPingBanner.SetActive(false);
    }

    private void Start()
    {
        // [Fix-7] Start에서 중복 구독 시도 → _subscribed 플래그로 차단됨
        TrySubscribe();
        ForceRefresh();
    }

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
        if (Instance == this) Instance = null;
    }

    // ════════════════════════════════════════════════════════════
    //  구독 관리
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// [Fix-7] 단일 진입점. _subscribed 플래그로 중복 구독을 원천 차단합니다.
    /// [Fix-10] 구독 성공 시 _monitorRef에 참조를 캐시합니다.
    /// </summary>
    private void TrySubscribe()
    {
        if (_subscribed) return;
        if (NetworkPingMonitor.Instance == null) return;

        _monitorRef = NetworkPingMonitor.Instance; // [Fix-10] 참조 캐시
        _monitorRef.OnPingUpdated      += HandlePingUpdated;
        _monitorRef.OnHighPingDetected += HandleHighPingDetected;
        _subscribed = true;
    }

    /// <summary>
    /// [Fix-10] _monitorRef 캐시를 사용해 -= 실행.
    /// NetworkPingMonitor.Instance가 null이 돼도 안전하게 해제됩니다.
    /// </summary>
    private void Unsubscribe()
    {
        if (!_subscribed) return;

        if (_monitorRef != null)
        {
            _monitorRef.OnPingUpdated      -= HandlePingUpdated;
            _monitorRef.OnHighPingDetected -= HandleHighPingDetected;
            _monitorRef = null;
        }

        _subscribed = false;
    }

    // ════════════════════════════════════════════════════════════
    //  이벤트 핸들러
    // ════════════════════════════════════════════════════════════

    private void HandlePingUpdated(
        int rttMs, float lossRate, NetworkPingMonitor.NetworkQuality quality)
    {
        if (rttMs == _lastRttMs) return;
        _lastRttMs = rttMs;

        UpdatePingText(rttMs, quality);
        UpdateSignalIcon(quality);
        UpdateLossText(lossRate);
    }

    private void HandleHighPingDetected(int rttMs)
    {
        ShowHighPingBanner(rttMs);
    }

    // ════════════════════════════════════════════════════════════
    //  UI 갱신
    // ════════════════════════════════════════════════════════════

    private void UpdatePingText(int rttMs, NetworkPingMonitor.NetworkQuality quality)
    {
        if (pingText == null) return;
        string hex = ColorUtility.ToHtmlStringRGB(NetworkPingMonitor.GetQualityColor(quality));
        pingText.text = $"<color=#{hex}>{rttMs}ms</color>";
    }

    private void UpdateSignalIcon(NetworkPingMonitor.NetworkQuality quality)
    {
        if (signalImage == null) return;

        signalImage.color = NetworkPingMonitor.GetQualityColor(quality);

        if (signalSprites != null && signalSprites.Length == 4)
        {
            var sprite = signalSprites[(int)quality];
            if (sprite != null) signalImage.sprite = sprite;
        }
    }

    private void UpdateLossText(float lossRate)
    {
        if (lossText == null) return;

        if (lossRate >= lossDisplayThreshold)
        {
            lossText.gameObject.SetActive(true);
            lossText.text = $"<color=#E84B4A>손실 {Mathf.RoundToInt(lossRate * 100f)}%</color>";
        }
        else
        {
            lossText.gameObject.SetActive(false);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  고핑 경고 배너
    // ════════════════════════════════════════════════════════════

    private void ShowHighPingBanner(int rttMs)
    {
        if (highPingBanner == null) return;

        if (highPingBannerText != null)
        {
            highPingBannerText.text = rttMs >= 200
                ? $"⚠ 네트워크 상태가 매우 불안정합니다 ({rttMs}ms)\n네트워크 환경을 확인해주세요."
                : $"📶 네트워크 지연이 감지되었습니다 ({rttMs}ms)";
        }

        if (_bannerCoroutine != null) StopCoroutine(_bannerCoroutine);
        _bannerCoroutine = StartCoroutine(BannerRoutine());
    }

    private IEnumerator BannerRoutine()
    {
        highPingBanner.SetActive(true);
        var group = highPingBanner.GetComponent<CanvasGroup>();

        if (group != null)
        {
            group.alpha = 0f;
            float e = 0f;
            while (e < 0.25f)
            {
                e += Time.deltaTime;
                group.alpha = Mathf.Clamp01(e / 0.25f);
                yield return null;
            }
            group.alpha = 1f;
        }

        yield return new WaitForSeconds(bannerDuration);

        if (group != null)
        {
            float e = 0f;
            while (e < 0.4f)
            {
                e += Time.deltaTime;
                group.alpha = Mathf.Lerp(1f, 0f, e / 0.4f);
                yield return null;
            }
            group.alpha = 0f;
        }

        highPingBanner.SetActive(false);
        _bannerCoroutine = null;
    }

    // ════════════════════════════════════════════════════════════
    //  외부 호출 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// NetworkPingMonitor가 늦게 스폰된 경우 현재 값으로 즉시 동기화합니다.
    /// 구독이 아직 안 된 경우 재시도도 함께 수행합니다.
    /// </summary>
    public void ForceRefresh()
    {
        TrySubscribe();

        var monitor = _monitorRef ?? NetworkPingMonitor.Instance;
        if (monitor == null)
        {
            if (pingText != null) pingText.text = waitingText;
            return;
        }
        HandlePingUpdated(monitor.SmoothedRttMs, monitor.PacketLossRate, monitor.Quality);
    }
}
