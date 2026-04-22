using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Supabase.Realtime;
using Supabase.Realtime.PostgresChanges;

// ════════════════════════════════════════════════════════════════
//  Supabase matchmaking_queue 테이블 모델
// ════════════════════════════════════════════════════════════════
[Serializable]
public class MatchmakingEntry
{
    [Newtonsoft.Json.JsonProperty("id")]          public string Id        { get; set; }
    [Newtonsoft.Json.JsonProperty("player_id")]   public string PlayerId  { get; set; }
    [Newtonsoft.Json.JsonProperty("nickname")]    public string Nickname  { get; set; }
    [Newtonsoft.Json.JsonProperty("joined_at")]   public string JoinedAt  { get; set; }
    [Newtonsoft.Json.JsonProperty("room_id")]     public string RoomId    { get; set; } // 매칭 성사 시 "ip:port" 형식으로 기록
    [Newtonsoft.Json.JsonProperty("status")]      public string Status    { get; set; } // waiting | matched | cancelled
}

/// <summary>
/// 자동 매칭 시스템.
/// isDedicatedServerMode = true : 서버 프로세스로 동작 (큐 폴링 → 매칭 성사 → IP 배정).
/// isDedicatedServerMode = false: 클라이언트로 동작 (큐 등록 → DB 상태 변경 대기).
/// </summary>
public class MatchmakingManager : MonoBehaviour
{
    public static MatchmakingManager Instance { get; private set; }

    // ── Inspector ───────────────────────────────────────────────
    [Header("Matchmaking Settings")]
    public int   maxPlayers     = 8;
    public int   minPlayers     = 2;
    public float maxWaitSeconds = 60f;

    [Header("데디케이티드 서버 설정")]
    [Tooltip("체크하면 이 인스턴스는 매칭 서버로 동작합니다. 커맨드라인 -dedicatedServer 인자로도 활성화됩니다.")]
    public bool   isDedicatedServerMode  = false;
    [Tooltip("클라이언트에게 알릴 이 서버의 공인 IP. 커맨드라인 -serverIp 또는 GAME_SERVER_IP 환경변수로 재정의됩니다.")]
    public string myServerIpFallback     = "127.0.0.1";
    [Tooltip("게임 서버 포트. 커맨드라인 -port로 재정의됩니다.")]
    public ushort myServerPort           = AppNetworkManager.DefaultPort;

    // ── 클라이언트 이벤트 ──────────────────────────────────────
    public event Action<int, int> OnQueueCountChanged;
    public event Action<float>    OnTimerUpdated;
    public event Action<string>   OnStatusMessageChanged;
    public event Action           OnMatchFound;
    public event Action           OnMatchmakingFailed;

    // ── 클라이언트 상태 ────────────────────────────────────────
    private string          myQueueEntryId;
    private string          myPlayerId;
    private string          myNickname;
    private bool            isSearching  = false;
    private bool            matchCreated = false;
    private float           elapsedWait  = 0f;
    private Coroutine       timerCoroutine;
    private RealtimeChannel realtimeChannel;
    private List<MatchmakingEntry> currentQueue = new List<MatchmakingEntry>();

    // ════════════════════════════════════════════════════════════
    //  초기화
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        // 커맨드라인 인자 -dedicatedServer 로 서버 모드 활성화
        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "-dedicatedServer"))
            isDedicatedServerMode = true;
    }

    private void Start()
    {
        if (isDedicatedServerMode) StartServerMode();
    }

    private void OnDestroy()
    {
        // 매칭 중에만 정리. 매칭 성사 후 씬 전환 시에는 실행되지 않음.
        if (!isDedicatedServerMode && isSearching)
            _ = CancelSearchAsync();
    }

    // ════════════════════════════════════════════════════════════
    //  ☁️ 서버 모드
    // ════════════════════════════════════════════════════════════

    private void StartServerMode()
    {
        ushort port   = GetServerPort();
        string myIp   = GetMyServerIP();
        Debug.Log($"[Server] ☁️ 매칭 서버 가동 (IP: {myIp}, Port: {port})");
        AppNetworkManager.Instance?.StartAsDedicatedServer(port);
        StartCoroutine(ServerMatchmakingLoop());
    }

    private IEnumerator ServerMatchmakingLoop()
    {
        // Supabase 초기화 대기
        while (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
            yield return new WaitForSeconds(1f);

        Debug.Log("[Server] ✅ Supabase 연결 완료. 매칭 루프 시작.");

        while (true)
        {
            yield return new WaitForSeconds(2f);
            _ = ProcessServerMatchmaking();
        }
    }

    private async Task ProcessServerMatchmaking()
    {
        try
        {
            // 대기 중인 플레이어 목록 (입장 순 정렬)
            var response = await SupabaseManager.Instance.Client
                .From("matchmaking_queue")
                .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "waiting")
                .Order("joined_at", Supabase.Postgrest.Constants.Ordering.Ascending)
                .Get();

            if (response?.Content == null) return;

            var queue = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MatchmakingEntry>>(response.Content)
                        ?? new List<MatchmakingEntry>();

            if (queue.Count == 0) return;

            // maxPlayers 단위로 즉시 매칭 (여러 매치 동시 처리)
            while (queue.Count >= maxPlayers)
            {
                var batch = queue.Take(maxPlayers).ToList();
                queue     = queue.Skip(maxPlayers).ToList();
                await ExecuteServerMatch(batch);
            }

            if (queue.Count == 0) return;

            // 남은 플레이어 중 가장 오래 기다린 플레이어 기준으로 판단
            var oldest    = queue[0]; // joined_at ASC 정렬이므로 첫 번째
            float waitSec = GetWaitSeconds(oldest.JoinedAt);

            if (waitSec >= maxWaitSeconds)
            {
                if (queue.Count >= minPlayers)
                {
                    // 최소 인원 충족 → 현재 인원으로 즉시 시작
                    await ExecuteServerMatch(queue);
                }
                else
                {
                    // 인원 부족 → 서버가 해당 플레이어 큐에서 제거
                    Debug.Log($"[Server] 인원 부족으로 매칭 취소: {oldest.Nickname} ({waitSec:0}초 대기)");
                    var param = new Dictionary<string, object> { { "p_player_id", oldest.PlayerId } };
                    await SupabaseManager.Instance.Client.Rpc("leave_matchmaking_queue", param);
                }
            }
        }
        catch (Exception e) { Debug.LogError($"[Server] 매칭 처리 오류: {e.Message}"); }
    }

    private async Task ExecuteServerMatch(List<MatchmakingEntry> players)
    {
        string ip       = GetMyServerIP();
        ushort port     = GetServerPort();
        string endpoint = $"{ip}:{port}"; // "1.2.3.4:7777" 형태로 room_id에 저장

        Debug.Log($"[Server] 🚀 매칭 성사 ({players.Count}명) → {endpoint}");

        try
        {
            var param = new Dictionary<string, object>
            {
                { "p_queue_ids", players.Select(p => p.Id).ToList() },
                { "p_server_ip", endpoint }
            };
            await SupabaseManager.Instance.Client.Rpc("server_assign_match", param);
        }
        catch (Exception e) { Debug.LogError($"[Server] 매칭 DB 업데이트 실패: {e.Message}"); }
    }

    // ════════════════════════════════════════════════════════════
    //  📱 클라이언트 모드
    // ════════════════════════════════════════════════════════════

    public async void StartSearch()
    {
        if (isDedicatedServerMode || isSearching) return;
        if (!ValidateSupabase()) { OnMatchmakingFailed?.Invoke(); return; }

        isSearching  = true;
        matchCreated = false;
        elapsedWait  = 0f;
        currentQueue.Clear();

        myPlayerId = SupabaseManager.Instance.Client.Auth.CurrentUser?.Id;
        myNickname = GameManager.Instance?.currentPlayerId ?? "Unknown";

        if (string.IsNullOrEmpty(myPlayerId))
        {
            NotifyStatus("로그인이 필요합니다.");
            isSearching = false;
            OnMatchmakingFailed?.Invoke();
            return;
        }

        NotifyStatus("매칭 서버에 연결 중...");
        await CleanupMyPreviousEntry();

        if (!await InsertQueueEntry())
        {
            isSearching = false;
            OnMatchmakingFailed?.Invoke();
            return;
        }

        await SubscribeToQueue();
        timerCoroutine = StartCoroutine(ClientWaitTimer());
        NotifyStatus("상대를 찾는 중...");
    }

    public async void CancelSearch()
    {
        if (!isSearching || isDedicatedServerMode) return;
        await CancelSearchAsync();
        NotifyStatus("매칭이 취소되었습니다.");
        OnMatchmakingFailed?.Invoke();
    }

    private IEnumerator ClientWaitTimer()
    {
        // 클라이언트는 타이머 표시만 담당. 매칭 결정은 서버가 함.
        while (isSearching && !matchCreated)
        {
            elapsedWait += Time.deltaTime;
            float remaining = Mathf.Max(0f, maxWaitSeconds - elapsedWait);
            OnTimerUpdated?.Invoke(remaining);

            // 서버 무응답 안전 타임아웃 (서버 장애 대비)
            if (elapsedWait >= maxWaitSeconds + 15f)
            {
                NotifyStatus("매칭 서버 응답 없음. 다시 시도해주세요.");
                CancelSearch();
                yield break;
            }
            yield return null;
        }
    }

    private void HandleMatchSuccess(string endpoint)
    {
        if (matchCreated) return;
        matchCreated = true;
        isSearching  = false;

        if (timerCoroutine != null) StopCoroutine(timerCoroutine);
        _ = UnsubscribeRealtime();

        // "ip:port" 파싱
        string ip   = endpoint;
        ushort port = AppNetworkManager.DefaultPort;
        int colonIdx = endpoint.LastIndexOf(':');
        if (colonIdx > 0 && ushort.TryParse(endpoint.Substring(colonIdx + 1), out ushort parsed))
        {
            ip   = endpoint.Substring(0, colonIdx);
            port = parsed;
        }

        if (GameManager.Instance != null)
        {
            GameManager.Instance.currentRoomId  = endpoint;
            GameManager.Instance.gameServerIp   = ip;
            GameManager.Instance.gameServerPort = port;

            // 매칭 성사 시점에 로컬 플레이어 캐릭터를 랜덤 생성하여 GameManager에 저장
            // 직업/등급/상성 랜덤 부여 + 액티브(1~4개) + 패시브(0~2개) 스킬 배정
            GameManager.Instance.myCharacterData = StatCalculator.GenerateRandomCharacter(myNickname);
        }


        NotifyStatus("매칭 완료! 게임 서버로 접속합니다...");
        OnMatchFound?.Invoke();

        AppNetworkManager.Instance?.ConnectToGameServer(ip, port);
        StartCoroutine(LoadGameSceneDelay(2f));
    }

    private void HandleServerCancelledMatch()
    {
        if (!isSearching) return;
        Debug.Log("[Matchmaking] 서버에 의해 매칭이 취소되었습니다 (인원 부족).");
        _ = CancelSearchAsync();
        NotifyStatus("매칭 인원 부족으로 취소되었습니다.");
        OnMatchmakingFailed?.Invoke();
    }

    private IEnumerator LoadGameSceneDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        GameManager.Instance?.LoadScene("InGameScene");
    }

    // ════════════════════════════════════════════════════════════
    //  📡 Supabase Realtime 콜백
    // ════════════════════════════════════════════════════════════

    private void OnQueueInsert(PostgresChangesEventArgs change)
    {
        if (!isSearching) return;
        try
        {
            var entry = Newtonsoft.Json.JsonConvert.DeserializeObject<MatchmakingEntry>(
                change.Response.Data.Record.ToString());
            if (entry != null && !currentQueue.Exists(e => e.Id == entry.Id))
            {
                currentQueue.Add(entry);
                NotifyQueueCount();
            }
        }
        catch { _ = RefreshQueueSnapshot(); }
    }

    private void OnQueueUpdate(PostgresChangesEventArgs change)
    {
        if (!isSearching || matchCreated) return;
        try
        {
            var updated = Newtonsoft.Json.JsonConvert.DeserializeObject<MatchmakingEntry>(
                change.Response.Data.Record.ToString());

            if (updated?.PlayerId != myPlayerId) return;

            if (updated.Status == "matched" && !string.IsNullOrEmpty(updated.RoomId))
                HandleMatchSuccess(updated.RoomId);
            else if (updated.Status == "cancelled")
                HandleServerCancelledMatch();
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] UPDATE 처리 오류: {e.Message}"); }
    }

    private void OnQueueDelete(PostgresChangesEventArgs change)
    {
        if (!isSearching) return;
        _ = RefreshQueueSnapshot();
    }

    // ════════════════════════════════════════════════════════════
    //  🛠 공통 내부 메서드
    // ════════════════════════════════════════════════════════════

    private async Task<bool> InsertQueueEntry()
    {
        try
        {
            var entry = new Dictionary<string, object>
            {
                { "player_id", myPlayerId },
                { "nickname",  myNickname },
                { "status",    "waiting"  },
                { "room_id",   null       }
            };
            var response = await SupabaseManager.Instance.Client.From("matchmaking_queue").Insert(entry);
            if (response?.Content != null)
            {
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MatchmakingEntry>>(response.Content);
                if (list?.Count > 0) myQueueEntryId = list[0].Id;
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Matchmaking] 큐 등록 실패: {e.Message}");
            NotifyStatus("서버 연결에 실패했습니다.");
            return false;
        }
    }

    private async Task SubscribeToQueue()
    {
        try
        {
            realtimeChannel = SupabaseManager.Instance.Client.Realtime.Channel("matchmaking_queue");

            realtimeChannel.OnPostgresChange(ListenType.Inserts, "public", "matchmaking_queue",
                (s, c) => MainThreadDispatcher.Enqueue(() => OnQueueInsert(c)));

            realtimeChannel.OnPostgresChange(ListenType.Updates, "public", "matchmaking_queue",
                (s, c) => MainThreadDispatcher.Enqueue(() => OnQueueUpdate(c)));

            realtimeChannel.OnPostgresChange(ListenType.Deletes, "public", "matchmaking_queue",
                (s, c) => MainThreadDispatcher.Enqueue(() => OnQueueDelete(c)));

            await realtimeChannel.Subscribe();
            await RefreshQueueSnapshot();
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] Realtime 구독 실패: {e.Message}"); }
    }

    private async Task RefreshQueueSnapshot()
    {
        try
        {
            var response = await SupabaseManager.Instance.Client
                .From("matchmaking_queue")
                .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "waiting")
                .Order("joined_at", Supabase.Postgrest.Constants.Ordering.Ascending)
                .Get();

            if (response?.Content != null)
                currentQueue = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MatchmakingEntry>>(response.Content)
                               ?? new List<MatchmakingEntry>();
            NotifyQueueCount();
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] 큐 조회 실패: {e.Message}"); }
    }

    private async Task CancelSearchAsync()
    {
        isSearching = false;
        if (timerCoroutine != null) { StopCoroutine(timerCoroutine); timerCoroutine = null; }
        await UnsubscribeRealtime();
        await CleanupMyPreviousEntry();
        myQueueEntryId = null;
        currentQueue.Clear();
    }

    private async Task UnsubscribeRealtime()
    {
        if (realtimeChannel != null)
        {
            try { await realtimeChannel.Unsubscribe(); } catch { }
            realtimeChannel = null;
        }
    }

    private async Task CleanupMyPreviousEntry()
    {
        if (string.IsNullOrEmpty(myPlayerId)) return;
        try
        {
            var param = new Dictionary<string, object> { { "p_player_id", myPlayerId } };
            await SupabaseManager.Instance.Client.Rpc("leave_matchmaking_queue", param);
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] 큐 취소 실패: {e.Message}"); }
    }

    // ════════════════════════════════════════════════════════════
    //  ⚙️ 런타임 설정값 결정 (커맨드라인 > 환경변수 > Inspector)
    // ════════════════════════════════════════════════════════════

    private string GetMyServerIP()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-serverIp") return args[i + 1];

        string env = Environment.GetEnvironmentVariable("GAME_SERVER_IP");
        if (!string.IsNullOrEmpty(env)) return env;

        return myServerIpFallback;
    }

    private ushort GetServerPort()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-port" && ushort.TryParse(args[i + 1], out ushort p)) return p;

        string env = Environment.GetEnvironmentVariable("GAME_SERVER_PORT");
        if (!string.IsNullOrEmpty(env) && ushort.TryParse(env, out ushort envPort)) return envPort;

        return myServerPort;
    }

    // DB joined_at 타임스탬프로부터 경과 초 계산 (서버 시간 기준, Time.time보다 정확)
    private float GetWaitSeconds(string joinedAt)
    {
        if (string.IsNullOrEmpty(joinedAt)) return 0f;
        try
        {
            var joined = DateTime.Parse(joinedAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
            return (float)(DateTime.UtcNow - joined.ToUniversalTime()).TotalSeconds;
        }
        catch { return 0f; }
    }

    private bool ValidateSupabase()
    {
        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
        {
            NotifyStatus("서버 연결이 불안정합니다. 네트워크를 확인해주세요.");
            return false;
        }
        if (SupabaseManager.Instance.Client.Auth.CurrentUser == null)
        {
            NotifyStatus("세션이 만료되었습니다. 다시 로그인해주세요.");
            return false;
        }
        return true;
    }

    private void NotifyQueueCount() => OnQueueCountChanged?.Invoke(currentQueue.Count, maxPlayers);
    private void NotifyStatus(string msg) => OnStatusMessageChanged?.Invoke(msg);
}
