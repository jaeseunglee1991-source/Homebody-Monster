using UnityEngine;
using Supabase;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Supabase.Realtime.PostgresChanges;
using static Supabase.Realtime.PostgresChanges.PostgresChangesOptions;

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

    [Supabase.Postgrest.Attributes.Column("tutorial_done")]
    [JsonProperty("tutorial_done")]       public bool   TutorialDone      { get; set; }

    [Supabase.Postgrest.Attributes.Column("fcm_token")]
    [JsonProperty("fcm_token")]           public string FcmToken          { get; set; }

    [Supabase.Postgrest.Attributes.Column("last_login_at")]
    [JsonProperty("last_login_at")]       public string LastLoginAt        { get; set; }
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

    // [SUPA-S2] URL/Key는 Assets/Resources/SupabaseConfig.asset(ScriptableObject)에서 로드.
    // 이전엔 Inspector 노출 필드(supabaseUrl / supabaseAnonKey)도 fallback으로 두었으나:
    //   • 씬 파일(.unity)에 anon key가 평문으로 commit될 위험
    //   • 두 곳의 값이 어긋날 때 어느 쪽이 우선인지 불명확
    //   • Resources 자산은 .gitignore 처리하기 쉬워 키 분리 관리에 유리
    // 위 이유로 Inspector fallback 제거. Resources/SupabaseConfig.asset이 없으면 명시적 실패.
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
        var config = Resources.Load<SupabaseConfig>("SupabaseConfig");
        if (config == null)
        {
            Debug.LogError("❌ SupabaseManager: Assets/Resources/SupabaseConfig.asset이 없습니다. " +
                           "Project 창에서 우클릭 → Create → Homebody → SupabaseConfig로 생성하세요.");
            return;
        }

        string url = config.SupabaseUrl;
        string key = config.SupabaseAnonKey;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
        {
            Debug.LogError("❌ SupabaseManager: SupabaseConfig.asset의 URL 또는 AnonKey가 비어 있습니다.");
            return;
        }
        Debug.Log("[Supabase] SupabaseConfig.asset 로드 완료");

        try
        {
            var options = new SupabaseOptions
            {
                AutoConnectRealtime = true,
                AutoRefreshToken    = true,
                // [SUPA-S1] PlayerPrefs 기반 SessionHandler 등록.
                // 이게 없으면 Gotrue가 세션을 영속 저장하지 않아 InitializeAsync 직후
                // CurrentUser가 항상 null이고, AutoRefreshToken=true 설정이 무의미해짐.
                // 동일 기기 재실행 시 익명 계정이 새로 생성되어 사용자 데이터(피자/부활권/
                // 닉네임/전적)가 모두 소실되는 치명적 버그를 차단.
                SessionHandler = new PlayerPrefsSessionPersistence(),
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
    public async Task<UserProfile> GetProfile(string userId)
    {
        if (!IsInitialized)
        {
            Debug.LogWarning("[Supabase] GetProfile failed: Client not initialized");
            return null;
        }

        try
        {
            Debug.Log($"[Supabase] Querying profiles table for ID: {userId}");
            // NEW-07: Single()은 0건/2건 이상 모두 예외를 던져 신규 유저 트리거 지연 시 불안정.
            // Limit(1).Get() + FirstOrDefault()로 0건도 null 반환되어 안전. 재시도 간격도 800ms로 확장.
            var response = await Client
                .From<UserProfile>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, userId)
                .Limit(1)
                .Get();

            return response?.Models != null && response.Models.Count > 0
                ? response.Models[0]
                : null;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Supabase] Profile query error: {e.Message}. Attempting retry...");
            await Task.Delay(800);
            try
            {
                var response = await Client
                    .From<UserProfile>()
                    .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, userId)
                    .Limit(1)
                    .Get();

                return response?.Models != null && response.Models.Count > 0
                    ? response.Models[0]
                    : null;
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

        // [Fix] null뿐 아니라 빈 문자열도 방어 — DB 함수가 P0001("p_room_id must not be empty")로 거부함.
        string roomId = GameManager.Instance?.currentRoomId;
        if (string.IsNullOrEmpty(roomId)) roomId = "unknown";

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
            {
                // H-5: tolerant parsing — supports raw "true", "\"true\"", and {"result":true} JSON shapes
                string raw = result.Content.Trim().Trim('"').Replace("\\\"", "\"");
                if (bool.TryParse(raw, out bool available)) return available;

                // {"result":true} 형태 폴백
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(raw);
                    var token = obj["result"] ?? obj["available"];
                    if (token != null) return (bool)token;
                }
                catch { /* ignore */ }
            }
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
    //                          p_ad_doubled bool DEFAULT false,
    //                          p_room_id    text DEFAULT NULL) → integer
    //
    //  보상 구조 (DB 기준):
    //    1위=100, 2위=60, 3~4위=30, 5위+=10 피자
    //    킬당 +5 (최대 +50)
    //    광고 시청 시 전체 2배
    //
    //  반환값: 실제 지급된 피자 수량 (결과창 UI 표시용)
    //          0 반환 = 이미 지급되었거나 실패 (DB UNIQUE 가드)
    //  호출 위치: ResultScene 또는 InGameManager.FinishGame()
    //
    //  ※ p_room_id 를 전달하면 (player_id, room_id, ad_doubled) PK 로
    //    DB 레벨에서 멱등성이 강제됩니다. 동일 매치 중복 호출은 자동 차단.
    // ════════════════════════════════════════════════════════════

    public async Task<int> GrantMatchRewards(int rank, int killCount, bool adDoubled = false, string roomId = null)
    {
        if (!IsInitialized || Client.Auth.CurrentUser == null) return 0;

        var param = new Dictionary<string, object>
        {
            { "p_rank",       rank      },
            { "p_kill_count", killCount },
            { "p_ad_doubled", adDoubled },
            { "p_room_id",    roomId    } // null 허용 (legacy 호출 호환)
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
    // H-8: nickname, message, senderUuid (UUID 기반 자기 메시지 필터링)
    public event System.Action<string, string, string> OnLobbyChatReceived;

    /// <summary>로비 접속자 목록 변경 시 발생하는 이벤트. (nicknames)</summary>
    public event System.Action<List<string>> OnLobbyPresenceUpdated;

    private Supabase.Realtime.RealtimeChannel _lobbyChatChannel;       // Presence(접속자 수) 전용
    private Supabase.Realtime.RealtimeChannel _lobbyChatDbChannel;     // [CHAT-FIX-V4] 채팅 DB INSERT 구독 전용
    private Supabase.Realtime.RealtimePresence<LobbyPresence> _lobbyPresence;
    private bool _isLobbyChannelSubscribed = false;
    private bool _lobbyHandlersRegistered = false;
    private bool _isLobbyChatDbSubscribed = false;
    private bool _lobbyChatDbHandlerRegistered = false;

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
            // BUG-15: 플래그가 true여도 실제 채널 상태가 Joined가 아니면(Realtime 재연결 직후)
            // 재구독을 진행해야 채팅 수신이 복구됨.
            if (_lobbyChatChannel.State == Supabase.Realtime.Constants.ChannelState.Joined)
            {
                Debug.Log("[Supabase] 로비 채팅 이미 구독 중");
                return;
            }
            Debug.LogWarning($"[Supabase] 채널 상태 불일치({_lobbyChatChannel.State}) — 재구독 진행");
            _isLobbyChannelSubscribed = false;
        }

        try
        {
            if (_lobbyChatChannel == null)
            {
                _lobbyChatChannel = Client.Realtime.Channel("lobby-chat");
                _lobbyHandlersRegistered = false;
            }

            if (!_lobbyHandlersRegistered)
            {
                // [CHAT-FIX-V4] 기존 Broadcast 기반 채팅 수신 핸들러는 SDK 7.0.2의 typed deserialization
                // 버그로 모든 메시지 payload가 null로 도착하여 작동 불가. 채팅은 PostgresChanges 기반의
                // SubscribeLobbyChatDb()로 분리 (실제 출시 게임 표준 패턴 — Discord 모델).
                // 이 채널(_lobbyChatChannel)은 이제 Presence(접속자 수) 전용으로 사용.

                // Presence 등록 — Subscribe 전에 Register 해야 함
                string presenceKey = GameManager.Instance?.currentPlayerId ?? System.Guid.NewGuid().ToString();
                _lobbyPresence = _lobbyChatChannel.Register<LobbyPresence>(presenceKey);

                // Presence 이벤트 핸들러 (Sync/Join/Leave 모두 감지)
                _lobbyPresence.AddPresenceEventHandler(Supabase.Realtime.Interfaces.IRealtimePresence.EventType.Sync, OnPresenceEvent);
                _lobbyPresence.AddPresenceEventHandler(Supabase.Realtime.Interfaces.IRealtimePresence.EventType.Join, OnPresenceEvent);
                _lobbyPresence.AddPresenceEventHandler(Supabase.Realtime.Interfaces.IRealtimePresence.EventType.Leave, OnPresenceEvent);
                _lobbyHandlersRegistered = true;
            }

            await _lobbyChatChannel.Subscribe();
            _isLobbyChannelSubscribed = true;
            Debug.Log("[Supabase] ✅ 로비 Presence 채널 구독 완료");

            // BUG-06: 구독 전에 TrackLobbyPresence가 호출되어 보류된 닉네임이 있다면 자동 등록.
            if (!string.IsNullOrEmpty(_pendingPresenceNickname))
            {
                string pending = _pendingPresenceNickname;
                _pendingPresenceNickname = null;
                TrackLobbyPresence(pending);
            }

            // [CHAT-FIX-V4] 채팅 DB 채널 구독 (Postgres INSERT 감지)
            // OnLobbyChatReady 이벤트는 AppNetworkManager.ConnectToLobby가 이 메서드 완료 후 발송함.
            await SubscribeLobbyChatDb();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Supabase] 로비 채팅 구독 실패: {e.Message}");
            _isLobbyChannelSubscribed = false;
        }
    }

    /// <summary>
    /// [CHAT-FIX-V4] lobby_chat_messages 테이블의 INSERT 이벤트를 구독.
    /// 신규 메시지가 DB에 들어올 때마다 OnLobbyChatReceived가 발생.
    /// </summary>
    private async Task SubscribeLobbyChatDb()
    {
        try
        {
            // [버그 수정 M-1] 채널이 이미 Joined 상태면 재구독 스킵.
            // ChatButtonTimeoutRoutine의 10초 재연결이 매번 Subscribe()를 중복 호출하여
            // Realtime 핸들러/연결 누적이 누적되던 문제 차단.
            if (_isLobbyChatDbSubscribed && _lobbyChatDbChannel != null &&
                _lobbyChatDbChannel.State == Supabase.Realtime.Constants.ChannelState.Joined)
            {
                Debug.Log("[Supabase] 로비 채팅 DB 채널 이미 구독 중 — 재구독 스킵");
                return;
            }

            if (_lobbyChatDbChannel == null)
            {
                _lobbyChatDbChannel = Client.Realtime.Channel("lobby-chat-db");
                _lobbyChatDbHandlerRegistered = false;
            }

            if (!_lobbyChatDbHandlerRegistered)
            {
                _lobbyChatDbChannel.Register(new PostgresChangesOptions(
                    "public",
                    "lobby_chat_messages"
                ));
                _lobbyChatDbChannel.AddPostgresChangeHandler(ListenType.Inserts, OnLobbyChatInserted);
                _lobbyChatDbHandlerRegistered = true;
            }

            await _lobbyChatDbChannel.Subscribe();
            _isLobbyChatDbSubscribed = true;
            Debug.Log("[Supabase] ✅ 로비 채팅 DB 채널 구독 완료");

            // 최근 메시지 50개 로드 → UI에 미리 채워주기 (재접속 UX)
            _ = LoadRecentLobbyChat(50);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Supabase] 채팅 DB 구독 실패: {e.Message}");
            _isLobbyChatDbSubscribed = false;
        }
    }

    private void OnLobbyChatInserted(Supabase.Realtime.Interfaces.IRealtimeChannel sender, PostgresChangesResponse change)
    {
        try
        {
            var row = change.Model<LobbyChatMessage>();
            if (row == null) return;
            string nick = row.Nickname ?? "알 수 없음";
            string msg  = row.Message  ?? "";
            string uuid = row.SenderUuid.ToString();
            MainThreadDispatcher.Enqueue(() =>
            {
                OnLobbyChatReceived?.Invoke(nick, msg, uuid);
            });
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Supabase] 채팅 INSERT 처리 오류: {e.Message}");
        }
    }

    /// <summary>최근 N개 채팅 메시지를 시간 오름차순으로 로드하여 UI에 재생.</summary>
    public async Task LoadRecentLobbyChat(int count = 50)
    {
        try
        {
            var result = await Client.From<LobbyChatMessage>()
                .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(count)
                .Get();
            if (result?.Models == null) return;

            // 최신 → 오래된 순으로 받았으니, UI에는 오래된 순서로 표시되도록 역순 재생
            var ordered = new List<LobbyChatMessage>(result.Models);
            ordered.Reverse();
            foreach (var row in ordered)
            {
                string nick = row.Nickname ?? "알 수 없음";
                string msg  = row.Message  ?? "";
                string uuid = row.SenderUuid.ToString();
                MainThreadDispatcher.Enqueue(() =>
                {
                    OnLobbyChatReceived?.Invoke(nick, msg, uuid);
                });
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Supabase] 최근 채팅 로드 실패(무시): {e.Message}");
        }
    }

    /// <summary>
    /// 로비 채팅 채널 구독을 해제합니다.
    /// 씬 전환(로비 → 인게임) 또는 앱 종료 시 호출하세요.
    /// </summary>
    public async Task UnsubscribeLobbyChat()
    {
        if (_lobbyChatChannel == null && _lobbyChatDbChannel == null) return;

        try
        {
            await Task.Run(() =>
            {
                try { _lobbyChatChannel?.Unsubscribe(); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Supabase] Presence 채널 해제 중 오류 (무시 가능): {e.Message}");
                }
                // [CHAT-FIX-V4] 채팅 DB 채널도 함께 해제
                try { _lobbyChatDbChannel?.Unsubscribe(); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Supabase] 채팅 DB 채널 해제 중 오류 (무시 가능): {e.Message}");
                }
            });
            // 서버 처리 시간 확보
            await Task.Delay(100);
            Debug.Log("[Supabase] 로비 채팅 채널 구독 해제");
        }
        finally
        {
            // [버그 수정 — 채널 평생 캐시]
            // Supabase SDK 의 Client.Realtime.Channel("lobby-chat") 은 내부적으로 같은
            // 인스턴스를 캐시한다. 우리가 _lobbyChatChannel = null 로 리셋해도 SDK 내부
            // 캐시는 남아있어, 다음 Subscribe 시 받은 채널은 이미 핸들러가 등록된 상태.
            // 그 상태에서 _lobbyHandlersRegistered=false 로 리셋하면 또 Register 시도하여
            //   "Register can only be called with broadcast options for a channel once"
            // 에러 발생 → 채널 영구 죽음 (접속자 0명 / 채팅 미수신).
            //
            // 해결: 채널 객체와 핸들러는 절대 null/false 로 리셋하지 않고 평생 유지.
            // Subscribe / Unsubscribe 토글만 플래그로 관리.
            _isLobbyChannelSubscribed = false;
            _isLobbyChatDbSubscribed  = false;
        }
    }

    /// <summary>
    /// 로비 채팅 메시지를 lobby_chat_messages 테이블에 INSERT합니다.
    /// [CHAT-FIX-V4] Realtime Broadcast 송신을 DB INSERT로 전환 — 실제 출시 게임 표준 패턴.
    /// 모든 클라이언트는 PostgresChanges INSERT 이벤트로 동일 데이터를 수신.
    /// RLS 정책으로 sender_uuid = auth.uid() 강제 (위장 방지).
    /// 스팸 방지(1초 쿨다운) 및 메시지 길이 제한이 적용됩니다.
    /// </summary>
    /// <returns>전송 성공 여부</returns>
    public async Task<bool> SendLobbyChatMessage(string nickname, string message)
    {
        if (!IsInitialized || Client?.Auth?.CurrentUser == null)
        {
            Debug.LogWarning("[Supabase] 채팅 전송 실패 — 미인증");
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

        // [BUG-12] 금칙어 필터링
        // [버그 수정 R2-2] Turkish locale 등 culture-sensitive 환경에서 ToLower 우회 차단.
        string lowerMsg = message.ToLowerInvariant();
        foreach (string bad in ForbiddenWords.List)
        {
            if (!string.IsNullOrEmpty(bad) && lowerMsg.Contains(bad.ToLowerInvariant()))
            {
                Debug.Log("[Supabase] 금칙어 포함 채팅 차단");
                return false;
            }
        }

        // 쿨다운 선점 (낙관적 잠금)
        _lastChatSendTime = Time.time;

        // 발신자 UUID 결정 — Supabase auth.uid()와 일치해야 RLS 통과
        string uidString = Client.Auth.CurrentUser.Id;
        if (!System.Guid.TryParse(uidString, out System.Guid senderUuid))
        {
            Debug.LogError($"[Supabase] 채팅 전송 실패 — UID가 GUID 형식이 아님: {uidString}");
            _lastChatSendTime = -999f;
            return false;
        }

        try
        {
            var row = new LobbyChatMessage
            {
                SenderUuid = senderUuid,
                Nickname   = nickname,
                Message    = message,
                // Id, CreatedAt은 DB default가 채워줌
            };
            await Client.From<LobbyChatMessage>().Insert(row);
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

    /// <summary>현재 로비 채팅 채널이 활성 상태인지 확인합니다.
    /// [CHAT-FIX-V4] Presence(접속자) 채널과 채팅 DB 채널이 모두 구독된 상태여야 true.</summary>
    public bool IsLobbyChatSubscribed => _isLobbyChannelSubscribed && _isLobbyChatDbSubscribed;

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
    // BUG-06: 채널 구독 전에 TrackLobbyPresence가 호출되면 닉네임을 임시 저장했다가
    // SubscribeLobbyChat 완료 직후 자동 등록한다.
    private string _pendingPresenceNickname = null;

    public void TrackLobbyPresence(string nickname)
    {
        if (_lobbyPresence == null || !_isLobbyChannelSubscribed)
        {
            _pendingPresenceNickname = nickname;
            Debug.LogWarning("[Supabase] Presence Track 실패 — 채널 미구독. 구독 완료 후 자동 등록 예정.");
            return;
        }

        try
        {
            _lobbyPresence.Track(new LobbyPresence { Nickname = nickname });
            // A-17: 성공 시에만 pending을 비움 — 실패 시엔 다음 SubscribeLobbyChat 완료 후 재시도되도록 보존.
            _pendingPresenceNickname = null;
            Debug.Log($"[Supabase] ✅ Presence 등록 완료: {nickname}");

            // Track 직후 즉시 리스트 업데이트 시도
            OnPresenceEvent(null, Supabase.Realtime.Interfaces.IRealtimePresence.EventType.Sync);
        }
        catch (System.Exception e)
        {
            // A-17: Track 실패 시 _pendingPresenceNickname을 유지하여 다음 SubscribeLobbyChat 진입 시 재시도.
            _pendingPresenceNickname = nickname;
            Debug.LogWarning($"[Supabase] Presence Track 실패: {e.Message} (재시도 대기)");
        }
    }

    /// <summary>로비 Presence를 해제합니다. DisconnectLobbyChat() 내에서 호출됩니다.</summary>
    public async Task UntrackLobbyPresence()
    {
        if (_lobbyPresence == null) return;

        await Task.Run(() =>
        {
            try { _lobbyPresence.Untrack(); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Supabase] Presence Untrack 실패 (무시 가능): {e.Message}");
            }
        });
        await Task.Delay(100);
        Debug.Log("[Supabase] Presence 해제 완료");
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
    // H-8: 송신자 UUID 포함 — 동일 닉네임 충돌 시에도 자기 메시지 정확히 필터링
    [JsonProperty("sender_uuid")] public string SenderUuid { get; set; }
}

public class LobbyChatBroadcast : Supabase.Realtime.Models.BaseBroadcast<LobbyChatPayload> { }

// ════════════════════════════════════════════════════════════════
//  로비 채팅 DB 모델 (lobby_chat_messages 테이블)
//  [CHAT-FIX-V4] Realtime Broadcast 대신 Postgres INSERT + PostgresChanges 구독으로 전환.
//  실제 출시 게임의 표준 패턴 (Discord 모델). SDK envelope quirks와 무관하게 안정 동작.
// ════════════════════════════════════════════════════════════════
[System.Serializable]
[Supabase.Postgrest.Attributes.Table("lobby_chat_messages")]
public class LobbyChatMessage : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [JsonProperty("id")]          public long   Id         { get; set; }

    [Supabase.Postgrest.Attributes.Column("sender_uuid")]
    [JsonProperty("sender_uuid")] public System.Guid SenderUuid { get; set; }

    [Supabase.Postgrest.Attributes.Column("nickname")]
    [JsonProperty("nickname")]    public string Nickname   { get; set; }

    [Supabase.Postgrest.Attributes.Column("message")]
    [JsonProperty("message")]     public string Message    { get; set; }

    [Supabase.Postgrest.Attributes.Column("created_at")]
    [JsonProperty("created_at")]  public System.DateTime CreatedAt { get; set; }
}

// ════════════════════════════════════════════════════════════════
//  Supabase Realtime Presence 페이로드 (lobby-chat 전용)
// ════════════════════════════════════════════════════════════════
public class LobbyPresence : Supabase.Realtime.Models.BasePresence
{
    [JsonProperty("nickname")] public string Nickname { get; set; }
}
