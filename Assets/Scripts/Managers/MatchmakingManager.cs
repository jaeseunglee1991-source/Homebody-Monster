using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Supabase.Realtime;
using Supabase.Realtime.PostgresChanges;

// ══════════════════════════════════════════════════════════════
//  Supabase 매칭 큐 DB 모델
// ══════════════════════════════════════════════════════════════
[Serializable]
public class MatchmakingEntry
{
    [Newtonsoft.Json.JsonProperty("id")]           public string Id        { get; set; }
    [Newtonsoft.Json.JsonProperty("player_id")]    public string PlayerId  { get; set; }
    [Newtonsoft.Json.JsonProperty("nickname")]     public string Nickname  { get; set; }
    [Newtonsoft.Json.JsonProperty("joined_at")]    public string JoinedAt  { get; set; }
    [Newtonsoft.Json.JsonProperty("room_id")]      public string RoomId    { get; set; }
    [Newtonsoft.Json.JsonProperty("status")]       public string Status    { get; set; } // waiting | matched | cancelled
}

/// <summary>
/// 자동 매칭 시스템 (Supabase Realtime 기반)
/// </summary>
public class MatchmakingManager : MonoBehaviour
{
    public static MatchmakingManager Instance { get; private set; }

    [Header("Matchmaking Settings")]
    public int   maxPlayers       = 8;
    public int   minPlayers       = 2;
    public float maxWaitSeconds   = 60f;

    // ── 이벤트 ─────────────────
    public event Action<int, int>  OnQueueCountChanged; 
    public event Action<float>     OnTimerUpdated;       
    public event Action<string>    OnStatusMessageChanged;
    public event Action            OnMatchFound;
    public event Action            OnMatchmakingFailed;

    // ── 내부 상태 ────────────────────────────────────────────
    private string          myQueueEntryId;
    private string          myPlayerId;
    private string          myNickname;
    private bool            isSearching     = false;
    private bool            matchCreated    = false;
    private float           elapsedWait     = 0f;
    private Coroutine       timerCoroutine;
    private RealtimeChannel realtimeChannel;
    private List<MatchmakingEntry> currentQueue = new List<MatchmakingEntry>();

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    private void OnDestroy()
    {
        _ = CancelSearchAsync();
    }

    public async void StartSearch()
    {
        if (isSearching) return;

        // 💡 1. 네트워크 예외 처리: 서버가 연결 안 되어있으면 팝업 닫기
        if (!ValidateSupabase()) 
        {
            OnMatchmakingFailed?.Invoke(); // UI에게 실패 알림 (팝업 닫힘)
            return;
        }

        isSearching   = true;
        matchCreated  = false;
        elapsedWait   = 0f;
        currentQueue.Clear();

        myPlayerId = SupabaseManager.Instance.Client.Auth.CurrentUser?.Id;
        myNickname = GameManager.Instance?.currentPlayerId ?? "Unknown";

        if (string.IsNullOrEmpty(myPlayerId))
        {
            NotifyStatus("로그인이 필요합니다.");
            isSearching = false;
            OnMatchmakingFailed?.Invoke(); // UI 복구
            return;
        }

        NotifyStatus("매칭 서버에 연결 중...");
        await CleanupMyPreviousEntry();

        bool inserted = await InsertQueueEntry();
        if (!inserted)
        {
            isSearching = false;
            OnMatchmakingFailed?.Invoke(); // 💡 큐 등록 실패 시 팝업 닫기
            return;
        }

        await SubscribeToQueue();
        timerCoroutine = StartCoroutine(MatchmakingTimer());

        NotifyStatus("상대를 찾는 중...");
    }

    public async void CancelSearch()
    {
        if (!isSearching) return;
        await CancelSearchAsync();
        NotifyStatus("매칭이 취소되었습니다.");
        OnMatchmakingFailed?.Invoke();
    }

    private IEnumerator MatchmakingTimer()
    {
        while (isSearching && !matchCreated)
        {
            elapsedWait += Time.deltaTime;
            float remaining = Mathf.Max(0f, maxWaitSeconds - elapsedWait);
            OnTimerUpdated?.Invoke(remaining);

            if (elapsedWait >= maxWaitSeconds)
            {
                int count = currentQueue.Count;
                if (count >= minPlayers)
                {
                    NotifyStatus($"{count}명으로 게임을 시작합니다!");
                    yield return new WaitForSeconds(1.5f);
                    _ = TryCreateMatch();
                }
                else
                {
                    NotifyStatus("상대를 더 기다리는 중... (연장)");
                    elapsedWait = 0f; 
                }
                yield break;
            }
            yield return null;
        }
    }

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
                if (list != null && list.Count > 0)
                    myQueueEntryId = list[0].Id;
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
                (sender, change) => MainThreadDispatcher.Enqueue(() => OnQueueInsert(change)));

            realtimeChannel.OnPostgresChange(ListenType.Updates, "public", "matchmaking_queue",
                (sender, change) => MainThreadDispatcher.Enqueue(() => OnQueueUpdate(change)));

            realtimeChannel.OnPostgresChange(ListenType.Deletes, "public", "matchmaking_queue",
                (sender, change) => MainThreadDispatcher.Enqueue(() => OnQueueDelete(change)));

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
            {
                currentQueue = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MatchmakingEntry>>(response.Content) ?? new List<MatchmakingEntry>();
            }
            NotifyQueueCount();
            CheckInstantStart();
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] 큐 조회 실패: {e.Message}"); }
    }

    private void OnQueueInsert(PostgresChangesEventArgs change)
    {
        if (!isSearching) return;
        try
        {
            var entry = Newtonsoft.Json.JsonConvert.DeserializeObject<MatchmakingEntry>(change.Response.Data.Record.ToString());
            if (entry != null && !currentQueue.Exists(e => e.Id == entry.Id))
            {
                currentQueue.Add(entry);
                NotifyQueueCount();
                CheckInstantStart();
            }
        }
        catch { _ = RefreshQueueSnapshot(); }
    }

    private void OnQueueUpdate(PostgresChangesEventArgs change)
    {
        if (!isSearching || matchCreated) return;
        try
        {
            var updated = Newtonsoft.Json.JsonConvert.DeserializeObject<MatchmakingEntry>(change.Response.Data.Record.ToString());
            if (updated?.PlayerId == myPlayerId && !string.IsNullOrEmpty(updated.RoomId))
                HandleMatchSuccess(updated.RoomId);
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] UPDATE 처리 오류: {e.Message}"); }
    }

    private void OnQueueDelete(PostgresChangesEventArgs change)
    {
        if (!isSearching) return;
        _ = RefreshQueueSnapshot();
    }

    private void CheckInstantStart()
    {
        if (matchCreated || !isSearching) return;
        if (currentQueue.Count >= maxPlayers)
        {
            NotifyStatus($"{maxPlayers}명 완성! 게임 시작!");
            _ = TryCreateMatch();
        }
    }

    private async Task TryCreateMatch()
    {
        if (matchCreated || !isSearching) return;
        bool iAmHost = currentQueue.Count > 0 && currentQueue[0].PlayerId == myPlayerId;
        if (!iAmHost) return;

        try
        {
            string roomId = await CreateMatchRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            List<string> matchedPlayerIds = new List<string>();
            int matchSize = Mathf.Min(currentQueue.Count, maxPlayers);
            for (int i = 0; i < matchSize; i++) matchedPlayerIds.Add(currentQueue[i].Id);

            await AssignRoomToPlayers(roomId, matchedPlayerIds);
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] 방 생성 실패: {e.Message}"); }
    }

    private async Task<string> CreateMatchRoom()
    {
        try
        {
            var result = await SupabaseManager.Instance.Client.Rpc("create_match_room", null);
            if (result?.Content != null)
            {
                var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(result.Content);
                return obj?.ContainsKey("room_id") == true ? obj["room_id"] : null;
            }
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] RPC create_match_room 오류: {e.Message}"); }
        return null;
    }

    private async Task AssignRoomToPlayers(string roomId, List<string> entryIds)
    {
        try
        {
            foreach (string entryId in entryIds)
            {
                await SupabaseManager.Instance.Client
                    .From("matchmaking_queue")
                    .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, entryId)
                    .Set("room_id", roomId)
                    .Set("status", "matched")
                    .Update();
            }
        }
        catch (Exception e) { Debug.LogError($"[Matchmaking] 플레이어 룸 배정 실패: {e.Message}"); }
    }

    private void HandleMatchSuccess(string roomId)
    {
        if (matchCreated) return;
        matchCreated = true;
        isSearching  = false;

        if (timerCoroutine != null) StopCoroutine(timerCoroutine);
        _ = UnsubscribeRealtime();

        if (GameManager.Instance != null) GameManager.Instance.currentRoomId = roomId;

        NotifyStatus("매칭 완료!");
        OnMatchFound?.Invoke();
        StartCoroutine(LoadGameSceneDelay(2f));
    }

    private IEnumerator LoadGameSceneDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        GameManager.Instance?.LoadScene("InGameScene");
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

    // 💡 2. 직접 Delete하던 로직을 안전한 RPC(서버 함수) 호출로 변경 완료
    private async Task CleanupMyPreviousEntry()
    {
        if (string.IsNullOrEmpty(myPlayerId)) return;
        try
        {
            var parameters = new Dictionary<string, object>
            {
                { "p_player_id", myPlayerId }
            };
            await SupabaseManager.Instance.Client.Rpc("leave_matchmaking_queue", parameters);
            Debug.Log("[Matchmaking] 큐 취소 (DB 삭제 완료)");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Matchmaking] 큐 취소(DB 삭제) 실패: {e.Message}");
        }
    }

    // 💡 3. 서버 연결 상태 확인 보강 (UI 피드백 향상)
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
