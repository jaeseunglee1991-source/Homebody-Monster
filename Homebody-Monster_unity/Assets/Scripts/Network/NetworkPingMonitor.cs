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
/// ─ 배치 방법 ──────────────────────────────────────────────────
///  Player 프리팹(PlayerNetworkSync, PlayerController와 동일 오브젝트)에 추가합니다.
///  NetworkSpawnManager.SpawnAsPlayerObject(clientId)로 스폰되므로
///  각 클라이언트는 자신의 Player 오브젝트에서 IsOwner=true가 됩니다.
///  IsOwner=true인 클라이언트에서만 PingRoutine이 시작됩니다.
///
///  ※ 별도 NetworkObject 프리팹으로 서버가 Spawn()하면 서버가 Owner가 되어
///    모든 클라이언트에서 IsOwner=false → PingRoutine 미시작 (Fix-16)
///
/// Fix-1  Awake 싱글톤 등록 → OnNetworkSpawn(IsOwner)으로 이동
/// Fix-2  _pendingPings Queue 메모리 누수 제거 → _sendTimes Dictionary로 완결
/// Fix-3  PingTimeoutRoutine 고아 코루틴 → CancellationToken으로 즉시 종료
/// Fix-4  SaveSessionPingAsync async void → async Task + CancellationToken
/// Fix-5  SmoothedRttMs 초기값(0) rttHint=0 전달 방지
/// Fix-16 [CRITICAL] 서버 Spawn 방식 → Player 프리팹에 붙이는 방식으로 재설계
/// </summary>
[DisallowMultipleComponent]
public class NetworkPingMonitor : NetworkBehaviour
{
    // [Fix-1][Fix-16] OnNetworkSpawn에서 IsOwner=true인 경우에만 등록
    public static NetworkPingMonitor Instance { get; private set; }

    public enum NetworkQuality { Excellent, Good, Poor, Critical }

    // ── Inspector ───────────────────────────────────────────────
    [Header("측정 설정")]
    [Range(0.3f, 5f)] public float sampleInterval   = 0.5f;
    [Range(3, 20)]    public int   smoothingSamples = 8;
    [Range(1f, 5f)]   public float pingTimeout      = 2f;

    [Header("등급 임계값 (ms)")]
    public int thresholdExcellent = 60;
    public int thresholdGood      = 120;
    public int thresholdPoor      = 200;

    [Header("고핑 경고")]
    public int                highPingWarningMs    = 150;
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

    private Coroutine               _pingCoroutine;
    private CancellationTokenSource _pingCts;
    private CancellationTokenSource _saveCts;
    private Task                    _saveTask; // 저장 Task 참조 보관 — GC 조기 수집 방지
    // BUG-10: OnNetworkDespawn 이중 호출 시에도 중복 저장이 발생하지 않도록 한 번 시작했으면 락.
    private bool _saveStarted = false;

    // ════════════════════════════════════════════════════════════
    //  NGO 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        // [Fix-6] 인스턴스는 더 이상 Awake에서 결정하지 않는다.
        // 클라이언트별로 NetworkPingMonitor가 스폰되며(SpawnWithOwnership),
        // 서버 프로세스에는 N개의 NetworkPingMonitor가 동시에 존재한다.
        // 로컬 클라이언트는 자신이 Owner인 한 개만 Instance로 사용해야 RTT 측정이 정확.
        // → Instance 결정은 OnNetworkSpawn(IsOwner)에서 수행.
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // [Fix-6] Owner(=로컬 클라이언트)만 Instance 등록 + 핑 루틴 시작.
        // 서버 프로세스에 존재하는 비-Owner 인스턴스들은 RPC 라우팅에만 사용된다.
        if (!IsOwner) return;

        // [Fix-16 보존] 동일 클라이언트에 두 인스턴스가 IsOwner=true로 잡히는 비정상 상황 방어
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PingMonitor] 이미 로컬 Instance가 존재합니다. 이 인스턴스는 측정하지 않습니다.");
            return;
        }

        Instance = this;

        _pingCts       = new CancellationTokenSource();
        _pingCoroutine = StartCoroutine(PingRoutine());
        Debug.Log("[PingMonitor] 핑 측정 시작 (Owner)");
    }

    public override void OnNetworkDespawn()
    {
        // [Fix-3] 코루틴 일괄 종료
        _pingCts?.Cancel();
        _pingCts?.Dispose();
        _pingCts = null;

        if (_pingCoroutine != null)
        {
            StopCoroutine(_pingCoroutine);
            _pingCoroutine = null;
        }

        // [Fix-4] 저장 Task: 이전 CTS 정리 후 새로 생성해 Task 시작.
        // _saveTask 참조를 보관해 GC 조기 수집을 방지한다.
        // OnDestroy에서는 _saveCts를 취소하지 않음 — Supabase 왕복 완료 전 취소 방지.
        // [C-8] 진행 중인 저장 Task가 있으면 새로 시작하지 않음 (중복 저장 방지)
        // [Fix-16 보존] Owner(로컬 클라이언트)만 저장 — 서버 프로세스의 비-Owner 인스턴스 중복 저장 방지
        if (IsOwner && !_saveStarted && (_saveTask == null || _saveTask.IsCompleted))
        {
            _saveStarted = true; // BUG-10: 이중 호출 방지
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts  = new CancellationTokenSource();
            _saveTask = SaveSessionPingAsync(_saveCts.Token);
        }
        else if (IsOwner)
        {
            Debug.Log("[PingMonitor] 이전 저장 Task 진행 중 또는 이미 시작됨 — 새 저장 스킵");
        }

        if (Instance == this) Instance = null;

        base.OnNetworkDespawn();
    }

    private void OnDestroy()
    {
        _pingCts?.Cancel();
        _pingCts?.Dispose();
        _pingCts = null;

        // [BUG-06] _saveCts는 여기서 취소하지 않음 — OnNetworkDespawn에서 시작한
        // SaveSessionPingAsync가 Supabase 응답을 완료할 때까지 보장해야 함.
        // 씬 전환 후 GC에 의해 자연 수거됨.
        if (Instance == this) Instance = null;
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

            StartCoroutine(PingTimeoutRoutine(seq, _pingCts.Token));

            // [Fix-5] SmoothedRttMs=0(초기값)이면 서버에서 무시
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
    /// rttHint: SmoothedRttMs를 함께 보내 PingAdaptiveCombat 서버 캐시를 갱신합니다.
    /// [Fix-5] rttHint=0(초기값)이면 UpdateClientRtt를 생략합니다.
    /// </summary>
    [ServerRpc]
    private void PingServerRpc(ulong seq, int rttHint, ServerRpcParams rpcParams = default)
    {
        // [Fix-5] 0은 초기값 — 아직 샘플 없음
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
    /// 씬 전환 중 SupabaseManager Destroy 후 await 재개 시 NPE 방지.
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
