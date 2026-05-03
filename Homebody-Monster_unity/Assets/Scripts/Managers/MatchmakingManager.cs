using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Supabase.Realtime;
using Supabase.Realtime.PostgresChanges;
using static Supabase.Realtime.PostgresChanges.PostgresChangesOptions;

// ════════════════════════════════════════════════════════════════
//  Supabase matchmaking_queue 테이블 모델
// ════════════════════════════════════════════════════════════════
[Serializable]
[Supabase.Postgrest.Attributes.Table("matchmaking_queue")]
public class MatchmakingEntry : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [Newtonsoft.Json.JsonProperty("id")]          public string Id        { get; set; }
    [Supabase.Postgrest.Attributes.Column("player_id")]
    [Newtonsoft.Json.JsonProperty("player_id")]   public string PlayerId  { get; set; }
    [Supabase.Postgrest.Attributes.Column("nickname")]
    [Newtonsoft.Json.JsonProperty("nickname")]    public string Nickname  { get; set; }
    [Supabase.Postgrest.Attributes.Column("joined_at")]
    [Newtonsoft.Json.JsonProperty("joined_at")]   public string JoinedAt  { get; set; }
    [Supabase.Postgrest.Attributes.Column("room_id")]
    [Newtonsoft.Json.JsonProperty("room_id")]     public string RoomId    { get; set; } // 매칭 성사 시 "ip:port" 형식으로 기록
    [Supabase.Postgrest.Attributes.Column("status")]
    [Newtonsoft.Json.JsonProperty("status")]      public string Status    { get; set; } // waiting | matched | cancelled
}

/// <summary>
/// 자동 매칭 시스템.
///
/// [Fix #1] OnMatchFound 이벤트 시그니처를 Action → Action&lt;string, ushort&gt; 로 변경.
///   - 기존: public event Action OnMatchFound;
///     → MatchmakingUX.OnMatchFound(string ip, ushort port) 파라미터 없어 컴파일 에러
///   - 수정: public event Action&lt;string, ushort&gt; OnMatchFound;
///     → ip, port를 이벤트 페이로드로 전달 → MatchmakingUX가 직접 ConnectToGameServer 호출 가능
///
/// [Fix #2] CancelMatchmaking() 퍼블릭 메서드 추가 (CancelSearch 별칭).
///   - MatchmakingUX.OnConfirmCancel() 및 ReconnectManager 에서 호출하던 메서드가 없어
///     컴파일 에러 발생. CancelSearch()를 내부 구현으로 유지하고 외부 노출용 별칭 추가.
///
/// isDedicatedServerMode = true : 서버 프로세스로 동작 (큐 폴링 → 매칭 성사 → IP 배정).
/// isDedicatedServerMode = false: 클라이언트로 동작 (큐 등록 → DB 상태 변경 대기).
/// </summary>
public class MatchmakingManager : MonoBehaviour
{
    public static MatchmakingManager Instance { get; private set; }

    // ── Inspector ───────────────────────────────────────────────
    [Header("Matchmaking Settings")]
    public int   maxPlayers     = 8;
    public int   minPlayers     = 1; // 테스트를 위해 1명으로 수정
    public float maxWaitSeconds = 5f; // 테스트를 위해 5초로 단축

    [Header("데디케이티드 서버 설정")]
    [Tooltip("체크하면 이 인스턴스는 매칭 서버로 동작합니다. 커맨드라인 -dedicatedServer 인자로도 활성화됩니다.")]
    public bool   isDedicatedServerMode  = false;
    private bool  _isServerLoopRunning   = false;
    [Tooltip("클라이언트에게 알릴 이 서버의 공인 IP. 커맨드라인 -serverIp 또는 GAME_SERVER_IP 환경변수로 재정의됩니다.")]
    public string myServerIpFallback     = "127.0.0.1";
    [Tooltip("게임 서버 포트. 커맨드라인 -port로 재정의됩니다.")]
    public ushort myServerPort           = AppNetworkManager.DefaultPort;

    [Header("씬 전환 딜레이")]
    [Tooltip("매칭 DB 업데이트 후 클라이언트들이 ConnectToGameServer를 완료할 때까지 대기하는 시간.")]
    public float sceneLoadDelaySeconds = 3f;

    // ── 클라이언트 이벤트 ──────────────────────────────────────
    public event Action<int, int> OnQueueCountChanged;
    public event Action<float>    OnTimerUpdated;
    public event Action<string>   OnStatusMessageChanged;

    // [Fix #1] Action → Action<string, ushort>: ip, port를 이벤트로 전달
    public event Action<string, ushort> OnMatchFound;

    // [Fix #2] MatchmakingUX/ReconnectManager 연동용 (원본은 OnMatchmakingFailed)
    public event Action<string> OnMatchFailed;
    // 원본 호환성 유지 (LobbyUIController.HandleMatchmakingFailed 구독 중)
    public event Action         OnMatchmakingFailed;

    // ── 클라이언트 상태 ────────────────────────────────────────
    /// <summary>현재 매칭 탐색 중인지 여부. 외부(LobbyUIController 등)에서 읽기 전용으로 사용.</summary>
    public bool IsSearching => isSearching;

    private string          myQueueEntryId;
    private string          myPlayerId;
    private string          myNickname;
    private bool            isSearching  = false;
    private bool            matchCreated = false;
    private float           elapsedWait  = 0f;
    private Coroutine       timerCoroutine;
    private RealtimeChannel realtimeChannel;
    private List<MatchmakingEntry> currentQueue = new List<MatchmakingEntry>();

    // ── 서버 전용: DB joined_at 파싱 실패 대비 로컬 첫 감지 시각 추적 ──
    private readonly Dictionary<string, DateTime> _playerFirstSeen = new Dictionary<string, DateTime>();

    // ════════════════════════════════════════════════════════════
    //  초기화
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else { Destroy(gameObject); return; }

        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "-dedicatedServer"))
            isDedicatedServerMode = true;
    }

    private void Start()  => CheckServerMode();

    private void Update()
    {
        if (isDedicatedServerMode && !_isServerLoopRunning)
            CheckServerMode();
    }

    private void CheckServerMode()
    {
        if (isDedicatedServerMode && !_isServerLoopRunning)
        {
            _isServerLoopRunning = true;
            StartServerMode();
        }
    }

    private void OnDestroy()
    {
        if (!isDedicatedServerMode && isSearching)
            _ = CancelSearchAsync();
    }

    // ════════════════════════════════════════════════════════════
    //  퍼블릭 API — 외부 호출용
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// [Fix #2] MatchmakingUX, ReconnectManager에서 호출하는 표준 취소 메서드.
    /// 내부적으로 CancelSearch()와 동일.
    /// </summary>
    public void CancelMatchmaking()
    {
        CancelSearch();
    }

    public async void StartSearch()
    {
        if (isDedicatedServerMode || isSearching) return;
        if (!ValidateSupabase())
        {
            FireMatchFailed("Supabase 초기화 안 됨");
            return;
        }

        isSearching  = true;
        matchCreated = false;
        elapsedWait  = 0f;
        currentQueue.Clear();

        myPlayerId = SupabaseManager.Instance.Client.Auth.CurrentUser?.Id;
        myNickname = GameManager.Instance?.currentPlayerNickname ?? "Unknown";

        if (string.IsNullOrEmpty(myPlayerId))
        {
            NotifyStatus("로그인이 필요합니다.");
            isSearching = false;
            FireMatchFailed("로그인 필요");
            return;
        }

        NotifyStatus("매칭 서버에 연결 중...");
        await CleanupMyPreviousEntry();

        if (!await InsertQueueEntry())
        {
            isSearching = false;
            FireMatchFailed("큐 등록 실패");
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
        FireMatchFailed("사용자 취소");
    }

    // ════════════════════════════════════════════════════════════
    //  클라이언트 — 내부 흐름
    // ════════════════════════════════════════════════════════════

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

    /// <summary>
    /// [Fix #1] HandleMatchSuccess — ip, port를 OnMatchFound 이벤트 페이로드로 전달.
    /// 기존에는 파라미터 없는 OnMatchFound?.Invoke() 였으므로
    /// MatchmakingUX가 IP/Port를 알 수 없어 씬 전환 불가.
    /// </summary>
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

        // [Fix #1] ip, port 페이로드 포함 발행 → MatchmakingUX.OnMatchFound(ip, port) 수신
        OnMatchFound?.Invoke(ip, port);

        // MatchmakingUX가 없는 환경(구형 LobbyUIController)에서는 여기서 즉시 연결.
        if (FindAnyObjectByType<MatchmakingUX>() == null)
        {
            AppNetworkManager.Instance?.ConnectToGameServer(ip, port);
        }
    }

    private void HandleServerCancelledMatch()
    {
        if (!isSearching) return;
        Debug.Log("[Matchmaking] 서버에 의해 매칭이 취소되었습니다 (인원 부족).");
        _ = CancelSearchAsync();
        NotifyStatus("매칭 인원 부족으로 취소되었습니다.");
        FireMatchFailed("서버 취소");
    }

    // ════════════════════════════════════════════════════════════
    //  이벤트 발행 내부 유틸
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// OnMatchFailed(string)과 OnMatchmakingFailed() 를 동시에 발행합니다.
    /// LobbyUIController(파라미터 없는 이벤트)와
    /// MatchmakingUX(파라미터 있는 이벤트) 양쪽을 모두 지원.
    /// </summary>
    private void FireMatchFailed(string reason)
    {
        OnMatchFailed?.Invoke(reason);
        OnMatchmakingFailed?.Invoke();
    }

    private void NotifyStatus(string msg)
    {
        Debug.Log($"[Matchmaking] {msg}");
        OnStatusMessageChanged?.Invoke(msg);
    }

    // ════════════════════════════════════════════════════════════
    //  ☁️ 서버 모드
    // ════════════════════════════════════════════════════════════

    private void StartServerMode()
    {
        ushort port   = GetServerPort();
        string myIp   = GetMyServerIP();

        // [FIX] 루프백 IP(127.0.0.1) 경고 및 자동 공인 IP 감지.
        if (myIp == "127.0.0.1" || myIp == "localhost")
        {
            string detectedIp = DetectLocalIPv4();
            if (!string.IsNullOrEmpty(detectedIp))
            {
                Debug.LogWarning($"[Server] 공인 IP 미설정 — 로컬 IP 자동 감지: {detectedIp}\n" +
                                 "출시 빌드에서는 -serverIp <공인IP> 또는 GAME_SERVER_IP 환경변수를 반드시 설정하세요.");
                myIp = detectedIp;
            }
            else
            {
                Debug.LogError("[Server] 서버 공인 IP가 127.0.0.1입니다. 클라이언트 접속 불가.\n" +
                               "-serverIp <공인IP> 또는 GAME_SERVER_IP 환경변수를 설정하세요.");
            }
        }

        Debug.Log($"[Server] ☁️ 매칭 서버 가동 (IP: {myIp}, Port: {port})");
        AppNetworkManager.Instance?.StartAsDedicatedServer(port);
        _ = RunServerLoop();
    }

    private static string DetectLocalIPv4()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(ip))
                    return ip.ToString();
            }
        }
        catch (Exception e) { Debug.LogWarning($"[Server] 로컬 IP 감지 실패: {e.Message}"); }
        return string.Empty;
    }

    private async Task RunServerLoop()
    {
        // Supabase 초기화 대기
        while (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
            await Task.Delay(1000);

        Debug.Log("[Server] ✅ Supabase 연결 완료. 매칭 루프 시작.");

        while (true)
        {
            await Task.Delay(2000);
            if (!ValidateSupabase()) continue;
            try
            {
                var queue = await FetchWaitingPlayers();
                UpdatePlayerFirstSeen(queue);
                var eligible = GetEligiblePlayers(queue);
                if (eligible.Count >= minPlayers)
                    await ExecuteServerMatch(eligible);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Server] 매칭 처리 오류: {e.Message}");
            }
        }
    }

    private async Task<List<MatchmakingEntry>> FetchWaitingPlayers()
    {
        // 서버 자신 제외
        string serverPlayerId = SupabaseManager.Instance?.Client.Auth.CurrentUser?.Id;

        var response = await SupabaseManager.Instance.Client
            .From<MatchmakingEntry>()
            .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "waiting")
            .Order("joined_at", Supabase.Postgrest.Constants.Ordering.Ascending)
            .Get();

        var result = response?.Models?.ToList() ?? new List<MatchmakingEntry>();

        if (!string.IsNullOrEmpty(serverPlayerId))
            result = result.Where(p => p.PlayerId != serverPlayerId).ToList();

        return result;
    }

    private void UpdatePlayerFirstSeen(List<MatchmakingEntry> queue)
    {
        var ids = new HashSet<string>(queue.Select(p => p.PlayerId));
        foreach (var k in _playerFirstSeen.Keys.ToList())
            if (!ids.Contains(k)) _playerFirstSeen.Remove(k);
        foreach (var p in queue)
            if (!_playerFirstSeen.ContainsKey(p.PlayerId))
                _playerFirstSeen[p.PlayerId] = DateTime.UtcNow;
    }

    private List<MatchmakingEntry> GetEligiblePlayers(List<MatchmakingEntry> queue)
    {
        if (queue.Count == 0) return new List<MatchmakingEntry>();

        // maxPlayers 단위로 즉시 매칭
        if (queue.Count >= maxPlayers)
            return queue.Take(maxPlayers).ToList();

        // 대기 시간 초과 시 minPlayers 충족이면 시작
        bool timeoutReached = _playerFirstSeen.Values.Any(t =>
            (DateTime.UtcNow - t).TotalSeconds >= maxWaitSeconds);

        if (timeoutReached && queue.Count >= minPlayers)
            return queue.Take(maxPlayers).ToList();

        return new List<MatchmakingEntry>();
    }

    private async Task ExecuteServerMatch(List<MatchmakingEntry> players)
    {
        foreach (var p in players) _playerFirstSeen.Remove(p.PlayerId);

        string ip       = GetMyServerIP();
        ushort port     = GetServerPort();
        string endpoint = $"{ip}:{port}";

        Debug.Log($"[Server] 🚀 매칭 성사 ({players.Count}명) → {endpoint}");

        try
        {
            var param = new Dictionary<string, object>
            {
                { "p_queue_ids", players.Select(p => p.Id).ToList() },
                { "p_server_ip", endpoint }
            };
            await SupabaseManager.Instance.Client.Rpc<string>("server_assign_match", param);

            if (Unity.Netcode.NetworkManager.Singleton != null &&
                Unity.Netcode.NetworkManager.Singleton.IsServer)
            {
                Debug.Log($"[Server] 씬 로드 {sceneLoadDelaySeconds}초 전 대기 중 (클라이언트 접속 대기)...");
                await Task.Delay((int)(sceneLoadDelaySeconds * 1000));

                Debug.Log("[Server] 🎬 인게임 씬 로드 시작...");
                Unity.Netcode.NetworkManager.Singleton.SceneManager
                    .LoadScene("InGameScene", UnityEngine.SceneManagement.LoadSceneMode.Single);
            }
        }
        catch (Exception e) { Debug.LogError($"[Server] 매칭 DB 업데이트 실패: {e.Message}"); }
    }

    // ════════════════════════════════════════════════════════════
    //  📡 Supabase Realtime 구독
    // ════════════════════════════════════════════════════════════

    private async Task SubscribeToQueue()
    {
        try
        {
            realtimeChannel = SupabaseManager.Instance.Client.Realtime.Channel("matchmaking_queue");

            realtimeChannel.Register(new PostgresChangesOptions("public", "matchmaking_queue"));
            realtimeChannel.AddPostgresChangeHandler(ListenType.Inserts,
                (_, c) => MainThreadDispatcher.Enqueue(() => OnQueueInsert(c)));
            realtimeChannel.AddPostgresChangeHandler(ListenType.Updates,
                (_, c) => MainThreadDispatcher.Enqueue(() => OnQueueUpdate(c)));
            realtimeChannel.AddPostgresChangeHandler(ListenType.Deletes,
                (_, c) => MainThreadDispatcher.Enqueue(() => OnQueueDelete(c)));

            await realtimeChannel.Subscribe();
            await RefreshQueueSnapshot();
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] Realtime 구독 실패: {e.Message}"); }
    }

    private void OnQueueInsert(PostgresChangesResponse change)
    {
        if (!isSearching) return;
        try
        {
            var entry = change.Model<MatchmakingEntry>();
            if (entry != null && !currentQueue.Exists(e => e.Id == entry.Id))
            {
                currentQueue.Add(entry);
                NotifyQueueCount();
            }
        }
        catch { _ = RefreshQueueSnapshot(); }
    }

    private void OnQueueUpdate(PostgresChangesResponse change)
    {
        if (!isSearching || matchCreated) return;
        try
        {
            var updated = change.Model<MatchmakingEntry>();

            if (string.IsNullOrEmpty(myPlayerId)) return;
            if (updated == null || updated.PlayerId != myPlayerId) return;

            if (updated.Status == "matched" && !string.IsNullOrEmpty(updated.RoomId))
                HandleMatchSuccess(updated.RoomId);
            else if (updated.Status == "cancelled")
                HandleServerCancelledMatch();
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] UPDATE 처리 오류: {e.Message}"); }
    }

    private void OnQueueDelete(PostgresChangesResponse change)
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
            var newEntry = new MatchmakingEntry
            {
                PlayerId = myPlayerId,
                Nickname = myNickname,
                Status   = "waiting",
                JoinedAt = DateTime.UtcNow.ToString("o"),
                RoomId   = null
            };
            var response = await SupabaseManager.Instance.Client.From<MatchmakingEntry>().Insert(newEntry);
            if (response?.Models != null && response.Models.Count > 0)
            {
                myQueueEntryId = response.Models[0].Id;
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

    private async Task RefreshQueueSnapshot()
    {
        try
        {
            var response = await SupabaseManager.Instance.Client
                .From<MatchmakingEntry>()
                .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "waiting")
                .Order("joined_at", Supabase.Postgrest.Constants.Ordering.Ascending)
                .Get();

            if (response?.Models != null)
                // [FIX] ToList() 없이 직접 대입하면 OnQueueInsert에서 NotSupportedException 발생.
                currentQueue = response.Models.ToList();
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

    private Task UnsubscribeRealtime()
    {
        if (realtimeChannel != null)
        {
            try { realtimeChannel.Unsubscribe(); } catch { }
            realtimeChannel = null;
        }
        return Task.CompletedTask;
    }

    private async Task CleanupMyPreviousEntry()
    {
        if (string.IsNullOrEmpty(myPlayerId)) return;
        try
        {
            var param = new Dictionary<string, object> { { "p_player_id", myPlayerId } };
            await SupabaseManager.Instance.Client.Rpc<string>("leave_matchmaking_queue", param);
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
}
