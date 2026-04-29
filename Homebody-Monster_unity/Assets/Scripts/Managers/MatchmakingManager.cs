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

        // 커맨드라인 인자 -dedicatedServer 로 서버 모드 활성화
        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "-dedicatedServer"))
            isDedicatedServerMode = true;
    }

    private void Start()
    {
        CheckServerMode();
    }

    private void Update()
    {
        // Inspector 토글 지원 (가드로 효율화: 실제 시작은 1회만)
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
                .From<MatchmakingEntry>()
                .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "waiting")
                .Order("joined_at", Supabase.Postgrest.Constants.Ordering.Ascending)
                .Get();

            if (response?.Models == null) return;

            var queue = response.Models.ToList();

            // 서버 자신이 큐에 있으면 제외 (데디케이티드 서버가 클라이언트로 등록되면 안 됨)
            string serverPlayerId = SupabaseManager.Instance?.Client.Auth.CurrentUser?.Id;
            if (!string.IsNullOrEmpty(serverPlayerId))
                queue = queue.Where(p => p.PlayerId != serverPlayerId).ToList();

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
            var oldest = queue[0];

            // 서버 로컬 첫 감지 시각 등록 (DB joined_at 파싱 실패 대비)
            foreach (var p in queue)
                if (!_playerFirstSeen.ContainsKey(p.PlayerId))
                    _playerFirstSeen[p.PlayerId] = DateTime.UtcNow;

            // 대기 시간 계산: DB 값 우선, 실패 시 서버 로컬 시각 사용
            float waitSec = GetWaitSeconds(oldest.JoinedAt);
            if (waitSec <= 0f && _playerFirstSeen.TryGetValue(oldest.PlayerId, out DateTime firstSeen))
                waitSec = (float)(DateTime.UtcNow - firstSeen).TotalSeconds;

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
                    var param = new Dictionary<string, object> { { "p_player_id", oldest.PlayerId } };
                    await SupabaseManager.Instance.Client.Rpc<string>("leave_matchmaking_queue", param);
                }
            }
        }
        catch (Exception e) { Debug.LogError($"[Server] 매칭 처리 오류: {e.Message}"); }
    }

    private async Task ExecuteServerMatch(List<MatchmakingEntry> players)
    {
        // 매칭된 플레이어 로컬 추적에서 제거
        foreach (var p in players)
            _playerFirstSeen.Remove(p.PlayerId);

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
            await SupabaseManager.Instance.Client.Rpc<string>("server_assign_match", param);

            if (Unity.Netcode.NetworkManager.Singleton != null &&
                Unity.Netcode.NetworkManager.Singleton.IsServer)
            {
                Debug.Log($"[Server] 씬 로드 {sceneLoadDelaySeconds}초 전 대기 중 (클라이언트 접속 대기)...");
                await System.Threading.Tasks.Task.Delay((int)(sceneLoadDelaySeconds * 1000));

                Debug.Log("[Server] 🎬 인게임 씬 로드 시작...");
                Unity.Netcode.NetworkManager.Singleton.SceneManager
                    .LoadScene("InGameScene", UnityEngine.SceneManagement.LoadSceneMode.Single);
            }
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
        myNickname = GameManager.Instance?.currentPlayerNickname ?? "Unknown";

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
        // StartCoroutine(LoadGameSceneDelay(2f)); // 클라이언트가 혼자 씬을 다시 로드하면 네트코드 동기화가 깨지므로 삭제합니다.
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

            if (updated?.PlayerId != myPlayerId) return;

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
                JoinedAt = DateTime.UtcNow.ToString("o"), // ISO 8601 형식으로 현재 UTC 시간 저장
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
                currentQueue = response.Models;
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
            try { realtimeChannel.Unsubscribe(); } catch { }
            realtimeChannel = null;
        }
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

    // DB joined_at 타임스탬프로부터 경과 초 계산 (서버 시간 기준, Time.time보다 정확)
    private float GetWaitSeconds(string joinedAt)
    {
        if (string.IsNullOrEmpty(joinedAt)) return 0f;
        try
        {
            // 다양한 ISO 8601 형식을 처리하기 위해 DateTimeOffset 사용 권장
            if (DateTimeOffset.TryParse(joinedAt, out DateTimeOffset joined))
            {
                return (float)(DateTimeOffset.UtcNow - joined).TotalSeconds;
            }
            return 0f;
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
