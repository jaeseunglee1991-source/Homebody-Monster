using UnityEngine;
using Supabase;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;

// ════════════════════════════════════════════════════════════════
//  데이터 모델
// ════════════════════════════════════════════════════════════════
[System.Serializable]
[Supabase.Postgrest.Attributes.Table("profiles")]
public class UserProfile : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [JsonProperty("id")]                  public string Id                { get; set; }
    [Supabase.Postgrest.Attributes.Column("nickname")]
    [JsonProperty("nickname")]            public string Nickname          { get; set; }
    [Supabase.Postgrest.Attributes.Column("win_count")]
    [JsonProperty("win_count")]           public int    WinCount          { get; set; }
    [Supabase.Postgrest.Attributes.Column("lose_count")]
    [JsonProperty("lose_count")]          public int    LoseCount         { get; set; }
    [Supabase.Postgrest.Attributes.Column("pizza_count")]
    [JsonProperty("pizza_count")]         public int    PizzaCount        { get; set; }
    [Supabase.Postgrest.Attributes.Column("revive_ticket_count")]
    [JsonProperty("revive_ticket_count")] public int    ReviveTicketCount { get; set; }
}

/// <summary>
/// Supabase 클라이언트 초기화 + 공통 DB 작업.
///
/// DB RPC 함수 목록 (Homebody-Monster 프로젝트):
///   save_match_result(p_room_id, p_is_winner, p_rank, p_kill_count, p_survived_time) → void
///   check_nickname_available(p_nickname)                                              → boolean
///   use_revive_ticket()                                                               → boolean
///   purchase_revive_ticket()                                                          → boolean  (비용: 피자 30개)
///   grant_ad_reward(p_reward_type)                                                    → boolean
///   grant_match_rewards(p_rank, p_kill_count, p_ad_doubled)                          → integer
/// </summary>
public partial class SupabaseManager : MonoBehaviour
{
    public static SupabaseManager Instance { get; private set; }

    [Header("⚠️ Inspector에서 입력")]
    public string supabaseUrl;
    public string supabaseAnonKey;

    public Client Client        { get; private set; }
    public bool   IsInitialized { get; private set; }

    async void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            EnsureMainThreadDispatcher();
            await InitSupabase();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void EnsureMainThreadDispatcher()
    {
        if (FindFirstObjectByType<MainThreadDispatcher>() == null)
        {
            var go = new GameObject("[MainThreadDispatcher]");
            go.AddComponent<MainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }
    }

    private async Task InitSupabase()
    {
        string url = supabaseUrl;
        string key = supabaseAnonKey;

        var config = Resources.Load<SupabaseConfig>("SupabaseConfig");
        if (config != null)
        {
            url = config.SupabaseUrl;
            key = config.SupabaseAnonKey;
            Debug.Log("[Supabase] SupabaseConfig.asset 로드 완료");
        }
        else
        {
            Debug.LogWarning("[Supabase] SupabaseConfig.asset 없음 — Inspector 값 사용 (출시 빌드에서는 권장하지 않음)");
        }

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
        {
            Debug.LogError("❌ SupabaseManager: URL 또는 Key가 설정되지 않았습니다.");
            return;
        }

        try
        {
            var options = new SupabaseOptions
            {
                AutoConnectRealtime = true,
                AutoRefreshToken    = true,
            };

            Client = new Client(url, key, options);
            await Client.InitializeAsync();
            IsInitialized = true;
            Debug.Log("✅ Supabase 초기화 완료");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"❌ Supabase 초기화 실패: {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  프로필 조회 / 생성
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// profiles 테이블에서 유저 프로필을 조회합니다.
    /// </summary>
    public async Task<UserProfile> GetOrCreateProfile(string userId)
    {
        if (!IsInitialized) 
        {
            Debug.LogWarning("[Supabase] GetOrCreateProfile failed: Client not initialized");
            return null;
        }

        try
        {
            Debug.Log($"[Supabase] Querying profiles table for ID: {userId}");
            var profile = await Client
                .From<UserProfile>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, userId)
                .Single();

            return profile;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Supabase] Profile query error: {e.Message}. Attempting retry...");
            await Task.Delay(500);
            try
            {
                var profile = await Client
                    .From<UserProfile>()
                    .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, userId)
                    .Single();

                return profile;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"⚠️ 프로필 로드 실패: {ex.Message}");
                return null;
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  게임 결과 저장
    //  DB: save_match_result(p_room_id, p_is_winner, p_rank,
    //                        p_kill_count, p_survived_time) → void
    // ════════════════════════════════════════════════════════════

    public async Task SaveMatchResult(bool isWinner, int rank, int kills, float survivedTime)
    {
        if (!IsInitialized || Client.Auth.CurrentUser == null) return;

        string roomId = GameManager.Instance?.currentRoomId ?? "unknown";

        var parameters = new Dictionary<string, object>
        {
            { "p_room_id",       roomId       },
            { "p_is_winner",     isWinner     },
            { "p_rank",          rank         },
            { "p_kill_count",    kills        },
            { "p_survived_time", survivedTime }
        };

        try
        {
            await Client.Rpc("save_match_result", parameters);
            Debug.Log("🏆 게임 결과 저장 완료");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 결과 저장 실패: {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  닉네임 저장
    //  DB: update_nickname(p_nickname) → void
    // ════════════════════════════════════════════════════════════

    public async Task<bool> UpdateNickname(string nickname)
    {
        if (!IsInitialized) return false;

        try
        {
            await Client.Rpc("update_nickname",
                new Dictionary<string, object> { { "p_nickname", nickname } });
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 닉네임 저장 실패: {e.Message}");
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  닉네임 중복 확인
    //  DB: check_nickname_available(p_nickname) → boolean
    // ════════════════════════════════════════════════════════════

    public async Task<bool> IsNicknameAvailable(string nickname)
    {
        if (!IsInitialized) return false;

        try
        {
            var result = await Client.Rpc("check_nickname_available",
                new Dictionary<string, object> { { "p_nickname", nickname } });

            if (result?.Content != null)
                return bool.TryParse(result.Content.Trim('"'), out bool available) && available;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 닉네임 확인 실패: {e.Message}");
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════
    //  [인게임] 부활권 사용
    //  DB: use_revive_ticket() → boolean
    //    true  = 티켓 1장 차감 성공 → 부활 허용
    //    false = 보유 티켓 없음    → 부활 거부
    //
    //  호출 위치: PlayerNetworkSync.ProcessReviveWithSupabase()
    //  (서버가 비동기 코루틴으로 호출 — 클라이언트 직접 호출 금지)
    // ════════════════════════════════════════════════════════════

    public async Task<bool> UseReviveTicket()
    {
        if (!IsInitialized || Client.Auth.CurrentUser == null) return false;

        try
        {
            // 파라미터 없음 — DB 함수가 auth.uid()로 직접 유저 조회
            var result = await Client.Rpc("use_revive_ticket", null);

            if (result?.Content != null &&
                bool.TryParse(result.Content.Trim('"'), out bool ok))
            {
                if (ok)
                {
                    // 로컬 캐시 감소 (HUD 즉시 반영용)
                    if (GameManager.Instance != null)
                        GameManager.Instance.reviveTicketCount =
                            Mathf.Max(0, GameManager.Instance.reviveTicketCount - 1);

                    Debug.Log($"[Supabase] 부활권 사용 성공 — 잔여: {GameManager.Instance?.reviveTicketCount}장");
                }
                else
                {
                    Debug.LogWarning("[Supabase] 부활권 사용 실패 — 보유 티켓 없음");
                }
                return ok;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 부활권 차감 실패: {e.Message}");
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════
    //  [로비] 피자로 부활권 구매 (피자 30개 → 부활권 1장)
    //  DB: purchase_revive_ticket() → boolean
    //    true  = 구매 성공 (피자 30 차감, 부활권 1 증가)
    //    false = 피자 부족
    //
    //  호출 위치: LobbyUIController 구매 버튼
    // ════════════════════════════════════════════════════════════

    public async Task<bool> PurchaseReviveTicket()
    {
        if (!IsInitialized || Client.Auth.CurrentUser == null) return false;

        try
        {
            var result = await Client.Rpc("purchase_revive_ticket", null);

            if (result?.Content != null &&
                bool.TryParse(result.Content.Trim('"'), out bool ok))
            {
                if (ok)
                {
                    // 로컬 캐시 증가
                    if (GameManager.Instance != null)
                        GameManager.Instance.reviveTicketCount++;

                    Debug.Log($"[Supabase] 부활권 구매 성공 (피자 30 차감) — 보유: {GameManager.Instance?.reviveTicketCount}장");
                }
                else
                {
                    Debug.LogWarning("[Supabase] 부활권 구매 실패 — 피자 부족 (30개 필요)");
                }
                return ok;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 부활권 구매 실패: {e.Message}");
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════
    //  [로비] 광고 시청 보상
    //  DB: grant_ad_reward(p_reward_type text) → boolean
    //    "revive_ticket" → 부활권 1장 지급
    //    "pizza"         → 피자 20개 지급
    //
    //  호출 위치: LobbyUIController 광고 시청 완료 콜백
    // ════════════════════════════════════════════════════════════

    public async Task<bool> GrantAdReward(string rewardType)
    {
        if (!IsInitialized || Client.Auth.CurrentUser == null) return false;

        var param = new Dictionary<string, object>
        {
            { "p_reward_type", rewardType }
        };

        try
        {
            var result = await Client.Rpc("grant_ad_reward", param);

            if (result?.Content != null &&
                bool.TryParse(result.Content.Trim('"'), out bool ok))
            {
                if (ok)
                {
                    // 부활권 지급인 경우 로컬 캐시 증가
                    if (rewardType == "revive_ticket" && GameManager.Instance != null)
                        GameManager.Instance.reviveTicketCount++;

                    Debug.Log($"[Supabase] 광고 보상 지급 성공: {rewardType}");
                }
                return ok;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 광고 보상 지급 실패: {e.Message}");
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════
    //  [결과창] 경기 후 피자 보상 지급
    //  DB: grant_match_rewards(p_rank int,
    //                          p_kill_count int,
    //                          p_ad_doubled bool DEFAULT false) → integer
    //
    //  보상 구조 (DB 기준):
    //    1위=100, 2위=60, 3~4위=30, 5위+=10 피자
    //    킬당 +5 (최대 +50)
    //    광고 시청 시 전체 2배
    //
    //  반환값: 실제 지급된 피자 수량 (결과창 UI 표시용)
    //  호출 위치: ResultScene 또는 InGameManager.FinishGame()
    //
    //  ※ p_total_players 파라미터 없음 (DB에 존재하지 않음)
    // ════════════════════════════════════════════════════════════

    public async Task<int> GrantMatchRewards(int rank, int killCount, bool adDoubled = false)
    {
        if (!IsInitialized || Client.Auth.CurrentUser == null) return 0;

        var param = new Dictionary<string, object>
        {
            { "p_rank",       rank      },
            { "p_kill_count", killCount },
            { "p_ad_doubled", adDoubled }
        };

        try
        {
            var result = await Client.Rpc("grant_match_rewards", param);

            if (result?.Content != null &&
                int.TryParse(result.Content.Trim('"'), out int pizza))
            {
                Debug.Log($"🍕 피자 {pizza}개 지급 완료 (순위:{rank}, 킬:{killCount}, 광고:{adDoubled})");
                return pizza;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 피자 지급 실패: {e.Message}");
        }
        return 0;
    }

    // ════════════════════════════════════════════════════════════
    //  [로비] Supabase Realtime 채팅
    //
    //  Broadcast 방식 사용 (DB 테이블 불필요, 실시간 전송 전용).
    //  채널 이름: "lobby-chat"
    //  이벤트: "chat_message"
    //  페이로드: { "nickname": string, "message": string, "timestamp": long }
    //
    //  호출 위치:
    //    구독:   AppNetworkManager.ConnectToLobby()
    //    해제:   AppNetworkManager.Disconnect() / LobbyUIController.OnDestroy()
    //    전송:   AppNetworkManager.SendChatMessage()
    // ════════════════════════════════════════════════════════════

    /// <summary>로비 채팅 메시지 수신 시 발생하는 이벤트. (nickname, message)</summary>
    public event System.Action<string, string> OnLobbyChatReceived;

    /// <summary>로비 접속자 목록 변경 시 발생하는 이벤트. (nicknames)</summary>
    public event System.Action<List<string>> OnLobbyPresenceUpdated;

    private Supabase.Realtime.RealtimeChannel _lobbyChatChannel;
    private Supabase.Realtime.RealtimePresence<LobbyPresence> _lobbyPresence;
    private bool _isLobbyChannelSubscribed = false;

    /// <summary>스팸 방지: 마지막 메시지 전송 시각</summary>
    private float _lastChatSendTime = -999f;

    /// <summary>스팸 방지: 최소 전송 간격 (초)</summary>
    private const float ChatCooldownSeconds = 1.0f;

    /// <summary>메시지 최대 길이 (바이트 절약 + 욕설 우회 방지)</summary>
    public const int MaxChatMessageLength = 100;

    /// <summary>
    /// 로비 채팅 Realtime 채널을 구독합니다.
    /// 이미 구독 중이면 중복 구독하지 않습니다.
    /// </summary>
    public async Task SubscribeLobbyChat()
    {
        if (!IsInitialized || Client == null)
        {
            Debug.LogWarning("[Supabase] 채팅 구독 실패 — Supabase 미초기화");
            return;
        }

        if (_isLobbyChannelSubscribed && _lobbyChatChannel != null)
        {
            Debug.Log("[Supabase] 로비 채팅 이미 구독 중");
            return;
        }

        try
        {
            _lobbyChatChannel = Client.Realtime.Channel("lobby-chat");

            // Broadcast 이벤트 리스너 등록
            var broadcast = _lobbyChatChannel.Register<LobbyChatBroadcast>();
            broadcast.AddBroadcastEventHandler((sender, payload) =>
            {
                var typed = payload as LobbyChatBroadcast;
                MainThreadDispatcher.Enqueue(() =>
                {
                    string nick = typed?.Payload?.Nickname ?? "???";
                    string msg  = typed?.Payload?.Message  ?? "";
                    OnLobbyChatReceived?.Invoke(nick, msg);
                });
            });

            // Presence 등록 — Subscribe 전에 Register 해야 함
            string presenceKey = GameManager.Instance?.currentPlayerId ?? System.Guid.NewGuid().ToString();
            _lobbyPresence = _lobbyChatChannel.Register<LobbyPresence>(presenceKey);

            // Presence 이벤트 핸들러 (Sync/Join/Leave 모두 감지)
            _lobbyPresence.AddPresenceEventHandler(Supabase.Realtime.Interfaces.IRealtimePresence.EventType.Sync, OnPresenceEvent);
            _lobbyPresence.AddPresenceEventHandler(Supabase.Realtime.Interfaces.IRealtimePresence.EventType.Join, OnPresenceEvent);
            _lobbyPresence.AddPresenceEventHandler(Supabase.Realtime.Interfaces.IRealtimePresence.EventType.Leave, OnPresenceEvent);

            await _lobbyChatChannel.Subscribe();
            _isLobbyChannelSubscribed = true;
            Debug.Log("[Supabase] ✅ 로비 채팅 채널 구독 완료");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Supabase] 로비 채팅 구독 실패: {e.Message}");
            _isLobbyChannelSubscribed = false;
        }
    }

    /// <summary>
    /// 로비 채팅 채널 구독을 해제합니다.
    /// 씬 전환(로비 → 인게임) 또는 앱 종료 시 호출하세요.
    /// </summary>
    public Task UnsubscribeLobbyChat()
    {
        if (_lobbyChatChannel == null) return Task.CompletedTask;

        try
        {
            _lobbyChatChannel.Unsubscribe();
            Debug.Log("[Supabase] 로비 채팅 채널 구독 해제");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Supabase] 채팅 채널 해제 중 오류 (무시 가능): {e.Message}");
        }
        finally
        {
            _lobbyChatChannel = null;
            _lobbyPresence = null;
            _isLobbyChannelSubscribed = false;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 로비 채팅 메시지를 Broadcast로 전송합니다.
    /// 스팸 방지(1초 쿨다운) 및 메시지 길이 제한이 적용됩니다.
    /// </summary>
    /// <returns>전송 성공 여부</returns>
    public async Task<bool> SendLobbyChatMessage(string nickname, string message)
    {
        if (!_isLobbyChannelSubscribed || _lobbyChatChannel == null)
        {
            Debug.LogWarning("[Supabase] 채팅 전송 실패 — 채널 미구독");
            return false;
        }

        // 스팸 방지: 쿨다운 체크
        if (Time.time - _lastChatSendTime < ChatCooldownSeconds)
        {
            Debug.Log("[Supabase] 채팅 쿨다운 중 — 메시지 무시됨");
            return false;
        }

        // 빈 메시지 무시
        if (string.IsNullOrWhiteSpace(message)) return false;

        // 길이 제한
        if (message.Length > MaxChatMessageLength)
            message = message.Substring(0, MaxChatMessageLength);

        // [Fix] 쿨다운 타임스탬프를 await 이전에 갱신 (낙관적 잠금).
        //
        // 이전 버그:
        //   쿨다운 체크 통과 → await Send() 시작 → (대기 중) → _lastChatSendTime 갱신
        //   await 완료 전에 두 번째 Send 호출이 들어오면 _lastChatSendTime이 아직 갱신 전이므로
        //   쿨다운 체크를 통과 → 두 메시지가 동시에 전송됨 (쿨다운 무력화).
        //   모바일에서 전송 버튼을 빠르게 두 번 탭하면 재현됨.
        //
        // 수정:
        //   await 이전에 _lastChatSendTime을 선점(optimistic lock)으로 갱신.
        //   전송 실패 시 _lastChatSendTime을 복구하여 즉시 재시도 허용.
        _lastChatSendTime = Time.time;

        try
        {
            // Supabase C# SDK 버그 우회: 
            // Broadcast 이벤트 전송 시 서버가 ACK를 반환하지 않으면 await가 영구 대기(Hang)에 빠질 수 있습니다.
            // 어차피 Broadcast는 Fire-and-forget 속성이므로, await 하지 않고 즉시 성공으로 처리합니다.
            _ = _lobbyChatChannel.Send(
                Supabase.Realtime.Constants.ChannelEventName.Broadcast,
                "chat_message",
                new LobbyChatPayload
                {
                    Nickname  = nickname,
                    Message   = message,
                    Timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            );

            return true;
        }
        catch (System.Exception e)
        {
            // 전송 실패 시 쿨다운 복구 — 네트워크 오류 시 즉시 재시도 허용
            _lastChatSendTime = -999f;
            Debug.LogError($"[Supabase] 채팅 전송 실패: {e.Message}");
            return false;
        }
    }

    /// <summary>현재 로비 채팅 채널이 활성 상태인지 확인합니다.</summary>
    public bool IsLobbyChatSubscribed => _isLobbyChannelSubscribed;

    // ════════════════════════════════════════════════════════════
    //  [로비] Supabase Presence (접속자 수 실시간 추적)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Presence 이벤트 핸들러. 모든 접속자의 닉네임을 추출하여 OnLobbyPresenceUpdated를 발생시킵니다.
    /// SubscribeLobbyChat()과 TrackLobbyPresence() 양쪽에서 호출됩니다.
    /// </summary>
    private void OnPresenceEvent(Supabase.Realtime.Interfaces.IRealtimePresence sender, Supabase.Realtime.Interfaces.IRealtimePresence.EventType type)
    {
        var nicknames = new List<string>();
        if (_lobbyPresence?.CurrentState != null)
        {
            foreach (var pair in _lobbyPresence.CurrentState)
            {
                foreach (var p in pair.Value)
                {
                    if (!string.IsNullOrEmpty(p.Nickname))
                        nicknames.Add(p.Nickname);
                }
            }
        }

        MainThreadDispatcher.Enqueue(() => OnLobbyPresenceUpdated?.Invoke(nicknames));
    }

    /// <summary>
    /// 로비 Presence를 Track합니다. SubscribeLobbyChat() 완료 후, 닉네임 로드 후 호출하세요.
    /// </summary>
    public void TrackLobbyPresence(string nickname)
    {
        if (_lobbyPresence == null || !_isLobbyChannelSubscribed)
        {
            Debug.LogWarning("[Supabase] Presence Track 실패 — 채널 미구독");
            return;
        }

        try
        {
            _lobbyPresence.Track(new LobbyPresence { Nickname = nickname });
            Debug.Log($"[Supabase] ✅ Presence 등록 완료: {nickname}");

            // Track 직후 즉시 리스트 업데이트 시도
            OnPresenceEvent(null, Supabase.Realtime.Interfaces.IRealtimePresence.EventType.Sync);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Supabase] Presence Track 실패: {e.Message}");
        }
    }

    /// <summary>로비 Presence를 해제합니다. DisconnectLobbyChat() 내에서 호출됩니다.</summary>
    public Task UntrackLobbyPresence()
    {
        if (_lobbyPresence == null) return Task.CompletedTask;

        try
        {
            _lobbyPresence.Untrack();
            Debug.Log("[Supabase] Presence 해제 완료");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Supabase] Presence Untrack 실패 (무시 가능): {e.Message}");
        }
        return Task.CompletedTask;
    }
}

// ════════════════════════════════════════════════════════════════
//  Supabase Realtime Broadcast 페이로드 (lobby-chat 전용)
// ════════════════════════════════════════════════════════════════
[System.Serializable]
public class LobbyChatPayload
{
    [JsonProperty("nickname")]  public string Nickname  { get; set; }
    [JsonProperty("message")]   public string Message   { get; set; }
    [JsonProperty("timestamp")] public long   Timestamp { get; set; }
}

public class LobbyChatBroadcast : Supabase.Realtime.Models.BaseBroadcast<LobbyChatPayload> { }

// ════════════════════════════════════════════════════════════════
//  Supabase Realtime Presence 페이로드 (lobby-chat 전용)
// ════════════════════════════════════════════════════════════════
public class LobbyPresence : Supabase.Realtime.Models.BasePresence
{
    [JsonProperty("nickname")] public string Nickname { get; set; }
}
