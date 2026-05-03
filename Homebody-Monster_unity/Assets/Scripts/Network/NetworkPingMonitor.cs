using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 인게임 실시간 네트워크 품질 모니터.
///
/// ServerRpc ↔ ClientRpc 왕복으로 실제 체감 RTT를 측정합니다.
/// NGO UnityTransport.GetCurrentRtt()는 UDP 레이어 RTT만 반환하며
/// 게임 로직 처리 지연을 포함하지 않아 체감과 다릅니다.
///
/// [배치] InGameScene에 별도 NetworkObject 프리팹(PingMonitor_Host)을 만들고
/// NetworkSpawnManager.OnNetworkSpawn() 서버 분기에서 스폰합니다.
/// Player 프리팹에 붙이면 안 됩니다 (Fix-1 참고).
///
/// Fix-1  Awake Destroy(gameObject) → Destroy(this) : 다른 플레이어 오브젝트 전체 파괴 방지
/// Fix-2  _pendingPings Queue 메모리 누수 제거 : _sendTimes Dictionary로 완결
/// Fix-3  PingTimeoutRoutine 고아 코루틴 : CancellationToken으로 Despawn 즉시 종료
/// Fix-4  SaveSessionPingAsync async void → async Task + CancellationToken : 씬 전환 NPE 방지
/// Fix-5  SmoothedRttMs 초기값(0) 전달 방지 : rttHint > 0일 때만 UpdateClientRtt 호출
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class NetworkPingMonitor : NetworkBehaviour
{
    public static NetworkPingMonitor Instance { get; private set; }

    public enum NetworkQuality { Excellent, Good, Poor, Critical }

    // ── Inspector ───────────────────────────────────────────────
    [Header("측정 설정")]
    [Range(0.3f, 5f)] public float sampleInterval  = 0.5f;
    [Range(3, 20)]    public int   smoothingSamples = 8;
    [Range(1f, 5f)]   public float pingTimeout      = 2f;

    [Header("등급 임계값 (ms)")]
    public int thresholdExcellent = 60;
    public int thresholdGood      = 120;
    public int thresholdPoor      = 200;

    [Header("고핑 경고")]
    public int highPingWarningMs                   = 150;
    [Range(2, 10)] public int highPingWarningCount = 3;

    // ── 공개 프로퍼티 ────────────────────────────────────────────
    public int            CurrentRttMs   { get; private set; } = 0;
    public int            SmoothedRttMs  { get; private set; } = 0;
    public float          PacketLossRate { get; private set; } = 0f;
    public NetworkQuality Quality        { get; private set; } = NetworkQuality.Excellent;

    public event Action<int, float, NetworkQuality> OnPingUpdated;
    public event Action<int>                        OnHighPingDetected;

    // ── 내부 상태 ─────────────────────────────────────────────
    private readonly Queue<int>  _rttSamples  = new Queue<int>();
    private readonly Queue<bool> _lossSamples = new Queue<bool>();

    // [Fix-2] _pendingPings Queue 제거 — _sendTimes로 seq 추적 완결
    private readonly Dictionary<ulong, float> _sendTimes = new Dictionary<ulong, float>();

    private ulong _pingSeq          = 0;
    private int   _highPingStreak   = 0;
    private float _sessionPingSum   = 0f;
    private int   _sessionPingCount = 0;

    private Coroutine             _pingCoroutine;
    private CancellationTokenSource _pingCts; // [Fix-3] 코루틴 일괄 종료용
    private CancellationTokenSource _saveCts; // [Fix-4] 저장 Task 종료용

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // [Fix-1] Destroy(gameObject) → Destroy(this)
            // gameObject를 파괴하면 다른 플레이어 오브젝트 전체가 사라지는 치명적 버그.
            Destroy(this);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (!IsOwner) return;

        _pingCts       = new CancellationTokenSource();
        _pingCoroutine = StartCoroutine(PingRoutine());
        Debug.Log("[PingMonitor] 핑 측정 시작");
    }

    public override void OnNetworkDespawn()
    {
        // [Fix-3] CTS 취소 → 모든 하위 코루틴(PingTimeoutRoutine 포함) 즉시 종료
        _pingCts?.Cancel();
        _pingCts?.Dispose();
        _pingCts = null;

        if (_pingCoroutine != null)
        {
            StopCoroutine(_pingCoroutine);
            _pingCoroutine = null;
        }

        // [Fix-4] 새 CTS로 저장 Task 실행 (씬 전환 완료 시 취소)
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        _ = SaveSessionPingAsync(_saveCts.Token);

        if (Instance == this) Instance = null;

        base.OnNetworkDespawn();
    }

    private new void OnDestroy()
    {
        _pingCts?.Cancel(); _pingCts?.Dispose();
        _saveCts?.Cancel(); _saveCts?.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  핑 측정 루프
    // ════════════════════════════════════════════════════════════

    private IEnumerator PingRoutine()
    {
        var wait = new WaitForSecondsRealtime(sampleInterval);

        while (true)
        {
            yield return wait;

            // [Fix-3] CTS 취소 시 즉시 종료
            if (_pingCts == null || _pingCts.IsCancellationRequested) yield break;
            if (!IsSpawned || !IsOwner) yield break;

            ulong seq = _pingSeq++;
            _sendTimes[seq] = Time.realtimeSinceStartup;

            // [Fix-3] 토큰 전달로 Despawn 후 고아 코루틴 방지
            StartCoroutine(PingTimeoutRoutine(seq, _pingCts.Token));

            // [Fix-5] rttHint로 SmoothedRttMs 전달 (0이면 서버에서 무시)
            PingServerRpc(seq, SmoothedRttMs);
        }
    }

    // [Fix-3] CancellationToken으로 Despawn 즉시 종료
    private IEnumerator PingTimeoutRoutine(ulong seq, CancellationToken token)
    {
        float waited = 0f;
        while (waited < pingTimeout)
        {
            if (token.IsCancellationRequested) yield break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        if (token.IsCancellationRequested) yield break;

        if (_sendTimes.ContainsKey(seq))
        {
            _sendTimes.Remove(seq);
            RecordSample(-1, lost: true);
            Debug.LogWarning($"[PingMonitor] 핑 타임아웃 (seq={seq})");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  NGO RPC
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 클라이언트 → 서버 핑 전송.
    /// rttHint: 현재 SmoothedRttMs를 함께 보내 PingAdaptiveCombat 서버 캐시를 갱신합니다.
    /// [Fix-5] rttHint == 0(아직 샘플 없음)이면 UpdateClientRtt 호출을 생략합니다.
    /// </summary>
    [ServerRpc]
    private void PingServerRpc(ulong seq, int rttHint, ServerRpcParams rpcParams = default)
    {
        // [Fix-5] 0은 초기값 → 아직 샘플 없음 → UpdateClientRtt 생략
        if (rttHint > 0)
            PingAdaptiveCombat.Instance?.UpdateClientRtt(
                rpcParams.Receive.SenderClientId, rttHint);

        PingResponseClientRpc(seq, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { rpcParams.Receive.SenderClientId }
            }
        });
    }

    [ClientRpc]
    private void PingResponseClientRpc(ulong seq, ClientRpcParams rpcParams = default)
    {
        if (!_sendTimes.TryGetValue(seq, out float sendTime)) return;
        _sendTimes.Remove(seq);

        int rttMs = Mathf.RoundToInt((Time.realtimeSinceStartup - sendTime) * 1000f);
        RecordSample(rttMs, lost: false);
    }

    // ════════════════════════════════════════════════════════════
    //  샘플 기록 및 통계
    // ════════════════════════════════════════════════════════════

    private void RecordSample(int rttMs, bool lost)
    {
        _lossSamples.Enqueue(lost);
        while (_lossSamples.Count > smoothingSamples) _lossSamples.Dequeue();

        int lostCount = 0;
        foreach (bool l in _lossSamples) if (l) lostCount++;
        PacketLossRate = (float)lostCount / _lossSamples.Count;

        if (lost) return;

        _rttSamples.Enqueue(rttMs);
        while (_rttSamples.Count > smoothingSamples) _rttSamples.Dequeue();

        int total = 0;
        foreach (int r in _rttSamples) total += r;

        CurrentRttMs  = rttMs;
        SmoothedRttMs = total / _rttSamples.Count;

        _sessionPingSum   += rttMs;
        _sessionPingCount++;

        NetworkQuality prev = Quality;
        Quality = ClassifyQuality(SmoothedRttMs);

        if (SmoothedRttMs >= highPingWarningMs)
        {
            _highPingStreak++;
            if (_highPingStreak >= highPingWarningCount)
                OnHighPingDetected?.Invoke(SmoothedRttMs);
        }
        else
        {
            _highPingStreak = 0;
        }

        OnPingUpdated?.Invoke(SmoothedRttMs, PacketLossRate, Quality);

        if (Quality != prev)
            Debug.Log($"[PingMonitor] 품질: {prev} → {Quality} ({SmoothedRttMs}ms)");
    }

    private NetworkQuality ClassifyQuality(int ms)
    {
        if (ms < thresholdExcellent) return NetworkQuality.Excellent;
        if (ms < thresholdGood)      return NetworkQuality.Good;
        if (ms < thresholdPoor)      return NetworkQuality.Poor;
        return                              NetworkQuality.Critical;
    }

    // ════════════════════════════════════════════════════════════
    //  세션 핑 저장
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// [Fix-4] async void → async Task + CancellationToken.
    /// 씬 전환 중 SupabaseManager가 Destroy된 후 await 재개 시 NPE 방지.
    /// </summary>
    private async Task SaveSessionPingAsync(CancellationToken token)
    {
        if (_sessionPingCount == 0) return;
        if (token.IsCancellationRequested) return;
        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized) return;

        int    avgPing = Mathf.RoundToInt(_sessionPingSum / _sessionPingCount);
        string roomId  = GameManager.Instance?.currentRoomId ?? "unknown";

        try
        {
            await SupabaseManager.Instance.SaveSessionPing(roomId, avgPing, PacketLossRate);

            if (!token.IsCancellationRequested)
                Debug.Log($"[PingMonitor] 세션 핑 저장: {avgPing}ms / 손실 {PacketLossRate:P0}");
        }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested)
                Debug.LogWarning($"[PingMonitor] 세션 핑 저장 실패 (무시): {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  공개 유틸
    // ════════════════════════════════════════════════════════════

    public static Color GetQualityColor(NetworkQuality q) => q switch
    {
        NetworkQuality.Excellent => new Color(0.18f, 0.80f, 0.44f),
        NetworkQuality.Good      => new Color(0.95f, 0.77f, 0.06f),
        NetworkQuality.Poor      => new Color(0.90f, 0.49f, 0.13f),
        NetworkQuality.Critical  => new Color(0.91f, 0.30f, 0.24f),
        _                        => Color.white
    };

    public static string GetQualityLabel(NetworkQuality q) => q switch
    {
        NetworkQuality.Excellent => "우수",
        NetworkQuality.Good      => "양호",
        NetworkQuality.Poor      => "불안정",
        NetworkQuality.Critical  => "매우 불안정",
        _                        => "측정 중"
    };
}
