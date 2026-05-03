using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Supabase.Realtime.PostgresChanges;
using static Supabase.Realtime.PostgresChanges.PostgresChangesOptions;

/// <summary>
/// 방 코드 기반 프라이빗 매칭 시스템.
///
/// ─ 동작 흐름 ─────────────────────────────────────────────────
///  방장(Host):
///   1. CreateRoom() → create_private_room RPC → 6자리 코드 발급
///   2. 코드를 친구에게 공유
///   3. 모두 준비 완료 → StartMatch() → start_private_room RPC
///      → Realtime이 참가자에게 server_ip 전달 → 전원 접속
///
///  참가자(Guest):
///   1. JoinRoom(code) → join_private_room RPC
///   2. Realtime으로 room_status = "started" 감지 → 자동 접속
///
/// ─ Supabase 연동 ─────────────────────────────────────────────
///  모든 row 수정은 RPC로만 처리합니다.
///  SQL 전문 → SupabaseManager_Shop.cs 주석 참조
/// </summary>
public class NetworkRoomManager : MonoBehaviour
{
    public static NetworkRoomManager Instance { get; private set; }

    [Header("방 UI 패널")]
    public GameObject roomPanel;

    [Header("방장 UI")]
    public TextMeshProUGUI roomCodeText;
    public Button          copyCodeButton;
    public Button          startButton;

    [Header("참가자 UI")]
    public TMP_InputField  roomCodeInput;
    public Button          joinRoomButton;
    public Button          readyButton;
    public TextMeshProUGUI readyButtonText;

    [Header("공통 UI")]
    public Button          createRoomButton;
    public Button          leaveButton;
    public TextMeshProUGUI statusText;

    [Header("멤버 목록")]
    public Transform  memberListContent;
    public GameObject memberRowPrefab;

    // ── 내부 상태 ──────────────────────────────────────────────
    private string _currentRoomId   = null;
    private string _currentRoomCode = null;
    private string _hostId          = null;
    private bool   _isHost          = false;
    private bool   _isReady         = false;
    private bool   _isConnecting    = false;

    private Supabase.Realtime.RealtimeChannel _roomChannel;
    private Supabase.Realtime.RealtimeChannel _memberChannel;

    private readonly List<RoomMemberInfo> _members = new();

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        roomPanel?.SetActive(false);
        createRoomButton?.onClick.AddListener(OnClickCreateRoom);
        joinRoomButton?.onClick.AddListener(OnClickJoinRoom);
        startButton?.onClick.AddListener(OnClickStartMatch);
        readyButton?.onClick.AddListener(OnClickReady);
        leaveButton?.onClick.AddListener(OnClickLeave);
        copyCodeButton?.onClick.AddListener(OnClickCopyCode);
    }

    private void OnDestroy() => UnsubscribeAll();

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    public void OpenRoomPanel()
    {
        roomPanel?.SetActive(true);
        ResetUI();
    }

    public void CloseRoomPanel()
    {
        roomPanel?.SetActive(false);
        _ = LeaveRoom();
    }

    // ════════════════════════════════════════════════════════════
    //  방 생성 (방장)
    // ════════════════════════════════════════════════════════════

    private async void OnClickCreateRoom()
    {
        SetInputsInteractable(false);
        ShowStatus("방 생성 중...");

        string uid      = SupabaseManager.Instance?.Client?.Auth?.CurrentUser?.Id;
        string nickname = GameManager.Instance?.currentPlayerNickname ?? "Unknown";

        if (string.IsNullOrEmpty(uid))
        {
            ShowStatus("로그인이 필요합니다.");
            SetInputsInteractable(true);
            return;
        }

        try
        {
            string code = GenerateRoomCode();

            var param = new Dictionary<string, object>
            {
                { "p_code",          code     },
                { "p_host_nickname", nickname },
                { "p_max_players",   8        }
            };
            var rpcResult = await SupabaseManager.Instance.Client.Rpc("create_private_room", param);

            if (rpcResult?.Content == null)
                throw new Exception("create_private_room 응답이 비어 있습니다.");

            string roomId = rpcResult.Content.Trim('"');
            if (string.IsNullOrEmpty(roomId) || roomId == "null")
                throw new Exception($"유효하지 않은 room_id: {rpcResult.Content}");

            _currentRoomId   = roomId;
            _currentRoomCode = code;
            _hostId          = uid;
            _isHost          = true;

            await SubscribeRoomChannel(roomId);
            await SubscribeMemberChannel(roomId);
            await RefreshMembersAsync();

            if (roomCodeText != null) roomCodeText.text = $"방 코드: {code}";
            startButton?.gameObject.SetActive(true);
            readyButton?.gameObject.SetActive(false);
            createRoomButton?.gameObject.SetActive(false);
            joinRoomButton?.gameObject.SetActive(false);
            if (roomCodeInput != null) roomCodeInput.gameObject.SetActive(false);

            ShowStatus($"방 생성 완료! 코드: {code}");
            Debug.Log($"[Room] ✅ 방 생성: {code} / id={roomId}");
        }
        catch (Exception e)
        {
            ShowStatus("방 생성에 실패했습니다. 다시 시도해주세요.");
            Debug.LogError($"[Room] 방 생성 실패: {e.Message}");
            SetInputsInteractable(true);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  방 참가 (참가자)
    // ════════════════════════════════════════════════════════════

    private async void OnClickJoinRoom()
    {
        string code = roomCodeInput?.text?.Trim().ToUpper();
        if (string.IsNullOrEmpty(code) || code.Length != 6)
        {
            ShowStatus("6자리 방 코드를 입력해주세요.");
            return;
        }

        SetInputsInteractable(false);
        ShowStatus("방 참가 중...");

        string uid      = SupabaseManager.Instance?.Client?.Auth?.CurrentUser?.Id;
        string nickname = GameManager.Instance?.currentPlayerNickname ?? "Unknown";

        if (string.IsNullOrEmpty(uid))
        {
            ShowStatus("로그인이 필요합니다.");
            SetInputsInteractable(true);
            return;
        }

        try
        {
            var room = await SupabaseManager.Instance.Client
                .From<PrivateRoom>()
                .Filter("room_code",   Supabase.Postgrest.Constants.Operator.Equals, code)
                .Filter("room_status", Supabase.Postgrest.Constants.Operator.Equals, "waiting")
                .Single();

            _currentRoomId   = room.Id;
            _currentRoomCode = code;
            _hostId          = room.HostId;
            _isHost          = false;

            var joinParam = new Dictionary<string, object>
            {
                { "p_room_id",  room.Id  },
                { "p_nickname", nickname }
            };
            var joinResult = await SupabaseManager.Instance.Client.Rpc("join_private_room", joinParam);
            bool joined = joinResult?.Content != null
                          && bool.TryParse(joinResult.Content.Trim('"'), out bool jv) && jv;

            if (!joined)
            {
                ShowStatus("방이 가득 찼습니다.");
                _currentRoomId = null;
                SetInputsInteractable(true);
                return;
            }

            await SubscribeRoomChannel(room.Id);
            await SubscribeMemberChannel(room.Id);
            await RefreshMembersAsync();

            startButton?.gameObject.SetActive(false);
            readyButton?.gameObject.SetActive(true);
            createRoomButton?.gameObject.SetActive(false);
            joinRoomButton?.gameObject.SetActive(false);
            if (roomCodeInput != null) roomCodeInput.gameObject.SetActive(false);

            ShowStatus($"\"{room.HostNickname}\"의 방에 참가했습니다!");
            Debug.Log($"[Room] ✅ 방 참가: {code}");
        }
        catch (Exception e)
        {
            ShowStatus("방을 찾을 수 없거나 이미 시작된 방입니다.");
            Debug.LogWarning($"[Room] 방 참가 실패: {e.Message}");
            _currentRoomId = null;
            SetInputsInteractable(true);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  준비
    // ════════════════════════════════════════════════════════════

    private async void OnClickReady()
    {
        _isReady = !_isReady;
        if (readyButtonText != null)
            readyButtonText.text = _isReady ? "✅ 준비 완료" : "준비";

        await SupabaseManager.Instance.SetMemberReady(_currentRoomId, _isReady);
    }

    // ════════════════════════════════════════════════════════════
    //  게임 시작 (방장)
    // ════════════════════════════════════════════════════════════

    private async void OnClickStartMatch()
    {
        if (!_isHost || string.IsNullOrEmpty(_currentRoomId)) return;

        if (_members.Count < 2)
        {
            ShowStatus("최소 2명 이상이어야 시작할 수 있습니다.");
            return;
        }

        string myUid = SupabaseManager.Instance?.Client?.Auth?.CurrentUser?.Id;
        int notReady = 0;
        foreach (var m in _members)
            if (m.PlayerId != myUid && !m.IsReady)
                notReady++;

        if (notReady > 0)
        {
            ShowStatus($"아직 준비 중인 플레이어가 {notReady}명 있습니다.");
            return;
        }

        ShowStatus("게임 시작 중...");

        string serverIp   = GameManager.Instance?.gameServerIp ?? "127.0.0.1";
        ushort serverPort = (GameManager.Instance?.gameServerPort > 0)
                           ? GameManager.Instance.gameServerPort
                           : AppNetworkManager.DefaultPort;
        string endpoint   = $"{serverIp}:{serverPort}";

        bool ok = await SupabaseManager.Instance.StartPrivateRoom(_currentRoomId, endpoint);
        if (!ok)
        {
            ShowStatus("게임 시작에 실패했습니다. 다시 시도해주세요.");
            Debug.LogError("[Room] start_private_room RPC 실패");
            return;
        }

        ConnectToGameServer(serverIp, serverPort);
    }

    // ════════════════════════════════════════════════════════════
    //  Realtime 구독
    // ════════════════════════════════════════════════════════════

    private async Task SubscribeRoomChannel(string roomId)
    {
        if (_roomChannel != null)
        { try { _roomChannel.Unsubscribe(); } catch { } _roomChannel = null; }

        try
        {
            _roomChannel = SupabaseManager.Instance.Client.Realtime
                .Channel($"private_rooms:{roomId}");
            _roomChannel.Register(new PostgresChangesOptions("public", "private_rooms"));
            _roomChannel.AddPostgresChangeHandler(ListenType.Updates,
                (_, c) => MainThreadDispatcher.Enqueue(() => OnRoomUpdated(c, roomId)));
            await _roomChannel.Subscribe();
            Debug.Log("[Room] private_rooms Realtime 구독 완료");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Room] private_rooms 구독 실패: {e.Message}");
        }
    }

    private async Task SubscribeMemberChannel(string roomId)
    {
        if (_memberChannel != null)
        { try { _memberChannel.Unsubscribe(); } catch { } _memberChannel = null; }

        try
        {
            _memberChannel = SupabaseManager.Instance.Client.Realtime
                .Channel($"private_room_members:{roomId}");
            _memberChannel.Register(new PostgresChangesOptions("public", "private_room_members"));
            _memberChannel.AddPostgresChangeHandler(ListenType.Inserts,
                (_, _) => MainThreadDispatcher.Enqueue(() => _ = RefreshMembersAsync()));
            _memberChannel.AddPostgresChangeHandler(ListenType.Updates,
                (_, _) => MainThreadDispatcher.Enqueue(() => _ = RefreshMembersAsync()));
            _memberChannel.AddPostgresChangeHandler(ListenType.Deletes,
                (_, _) => MainThreadDispatcher.Enqueue(() => _ = RefreshMembersAsync()));
            await _memberChannel.Subscribe();
            Debug.Log("[Room] private_room_members Realtime 구독 완료");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Room] private_room_members 구독 실패: {e.Message}");
        }
    }

    private void UnsubscribeAll()
    {
        if (_roomChannel   != null) { try { _roomChannel.Unsubscribe();   } catch { } _roomChannel   = null; }
        if (_memberChannel != null) { try { _memberChannel.Unsubscribe(); } catch { } _memberChannel = null; }
    }

    // ════════════════════════════════════════════════════════════
    //  Realtime 이벤트 핸들러
    // ════════════════════════════════════════════════════════════

    private void OnRoomUpdated(PostgresChangesResponse change, string targetRoomId)
    {
        try
        {
            var room = change.Model<PrivateRoom>();
            if (room == null || room.Id != targetRoomId) return;

            if (room.RoomStatus == "started" && !_isHost && !_isConnecting)
                ParseAndConnect(room.ServerIp);
            else if (room.RoomStatus == "closed")
            {
                ShowStatus("방장이 방을 닫았습니다.");
                _ = LeaveRoom();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Room] OnRoomUpdated 처리 오류: {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  멤버 목록 갱신
    // ════════════════════════════════════════════════════════════

    private async Task RefreshMembersAsync()
    {
        if (string.IsNullOrEmpty(_currentRoomId)) return;
        var members = await FetchMembersFromDb(_currentRoomId);
        _members.Clear();
        _members.AddRange(members);
        RefreshMemberListUI();
        if (_isHost && startButton != null)
            startButton.interactable = _members.Count >= 2;
    }

    private void RefreshMemberListUI()
    {
        if (memberListContent == null || memberRowPrefab == null) return;
        foreach (Transform child in memberListContent) Destroy(child.gameObject);

        foreach (var m in _members)
        {
            var go  = Instantiate(memberRowPrefab, memberListContent);
            var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
            if (tmp == null) continue;
            string ready = m.IsReady ? "✅" : "⏳";
            string host  = m.PlayerId == _hostId ? " 👑" : "";
            tmp.text = $"{ready} {m.Nickname}{host}";
        }
        ShowStatus($"대기 중 ({_members.Count}명)");
    }

    // ════════════════════════════════════════════════════════════
    //  서버 접속
    // ════════════════════════════════════════════════════════════

    private void ParseAndConnect(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return;
        string ip   = endpoint;
        ushort port = AppNetworkManager.DefaultPort;
        int colon = endpoint.LastIndexOf(':');
        if (colon > 0 && ushort.TryParse(endpoint.Substring(colon + 1), out ushort p))
        { ip = endpoint.Substring(0, colon); port = p; }
        ConnectToGameServer(ip, port);
    }

    private void ConnectToGameServer(string ip, ushort port)
    {
        if (_isConnecting) return;
        _isConnecting = true;

        UnsubscribeAll();

        if (GameManager.Instance != null)
        {
            GameManager.Instance.currentRoomId  = $"{ip}:{port}";
            GameManager.Instance.gameServerIp   = ip;
            GameManager.Instance.gameServerPort = port;
            string nick = GameManager.Instance.currentPlayerNickname ?? "Unknown";
            if (GameManager.Instance.myCharacterData == null)
                GameManager.Instance.myCharacterData = StatCalculator.GenerateRandomCharacter(nick);
        }

        roomPanel?.SetActive(false);
        AppNetworkManager.Instance?.ConnectToGameServer(ip, port);
        Debug.Log($"[Room] 게임 서버 접속: {ip}:{port}");
    }

    // ════════════════════════════════════════════════════════════
    //  방 나가기
    // ════════════════════════════════════════════════════════════

    private async void OnClickLeave()
    {
        await LeaveRoom();
        roomPanel?.SetActive(false);
    }

    private async Task LeaveRoom()
    {
        if (string.IsNullOrEmpty(_currentRoomId)) return;

        string savedRoomId = _currentRoomId;
        bool   wasHost     = _isHost;

        _currentRoomId   = null;
        _currentRoomCode = null;
        _hostId          = null;
        _isHost          = false;
        _isReady         = false;
        _isConnecting    = false;
        _members.Clear();

        UnsubscribeAll();

        try
        {
            if (wasHost)
                await SupabaseManager.Instance.ClosePrivateRoom(savedRoomId);
            else
            {
                var param = new Dictionary<string, object> { { "p_room_id", savedRoomId } };
                await SupabaseManager.Instance.Client.Rpc("leave_private_room", param);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Room] LeaveRoom RPC 실패 (무시): {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Supabase SELECT (멤버 조회)
    // ════════════════════════════════════════════════════════════

    private async Task<List<RoomMemberInfo>> FetchMembersFromDb(string roomId)
    {
        var result = new List<RoomMemberInfo>();
        if (string.IsNullOrEmpty(roomId)) return result;
        try
        {
            var response = await SupabaseManager.Instance.Client
                .From<PrivateRoomMember>()
                .Filter("room_id", Supabase.Postgrest.Constants.Operator.Equals, roomId)
                .Order("joined_at", Supabase.Postgrest.Constants.Ordering.Ascending)
                .Get();
            if (response?.Models != null)
                foreach (var m in response.Models)
                    result.Add(new RoomMemberInfo { PlayerId = m.PlayerId, Nickname = m.Nickname, IsReady = m.IsReady });
        }
        catch (Exception e) { Debug.LogWarning($"[Room] FetchMembersFromDb 실패: {e.Message}"); }
        return result;
    }

    // ════════════════════════════════════════════════════════════
    //  방 코드 생성 (O·0·I·1 제외)
    // ════════════════════════════════════════════════════════════

    private static string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new System.Text.StringBuilder(6);
        for (int i = 0; i < 6; i++)
            sb.Append(chars[UnityEngine.Random.Range(0, chars.Length)]);
        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════
    //  UI 헬퍼
    // ════════════════════════════════════════════════════════════

    private async void OnClickCopyCode()
    {
        if (string.IsNullOrEmpty(_currentRoomCode)) return;
        GUIUtility.systemCopyBuffer = _currentRoomCode;
        ShowStatus($"코드 복사됨: {_currentRoomCode}");
        await Task.Delay(2000);
        if (this != null && !string.IsNullOrEmpty(_currentRoomCode))
            ShowStatus($"방 코드: {_currentRoomCode}");
    }

    private void ResetUI()
    {
        createRoomButton?.gameObject.SetActive(true);
        joinRoomButton?.gameObject.SetActive(true);
        startButton?.gameObject.SetActive(false);
        readyButton?.gameObject.SetActive(false);
        if (roomCodeInput != null) roomCodeInput.gameObject.SetActive(true);
        if (roomCodeText  != null) roomCodeText.text = "";
        SetInputsInteractable(true);
        if (startButton != null) startButton.interactable = false;
        if (memberListContent != null)
            foreach (Transform child in memberListContent) Destroy(child.gameObject);
        ShowStatus("방을 만들거나 코드로 참가하세요.");
    }

    private void SetInputsInteractable(bool v)
    {
        if (createRoomButton != null) createRoomButton.interactable = v;
        if (joinRoomButton   != null) joinRoomButton.interactable   = v;
        if (leaveButton      != null) leaveButton.interactable      = v;
    }

    private void ShowStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }
}

// ════════════════════════════════════════════════════════════════
//  Supabase ORM 모델 (SELECT 전용 — INSERT/UPDATE는 모두 RPC)
// ════════════════════════════════════════════════════════════════

[System.Serializable]
[Supabase.Postgrest.Attributes.Table("private_rooms")]
public class PrivateRoom : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [Newtonsoft.Json.JsonProperty("id")]            public string Id          { get; set; }
    [Supabase.Postgrest.Attributes.Column("room_code")]
    [Newtonsoft.Json.JsonProperty("room_code")]     public string RoomCode    { get; set; }
    [Supabase.Postgrest.Attributes.Column("host_id")]
    [Newtonsoft.Json.JsonProperty("host_id")]       public string HostId      { get; set; }
    [Supabase.Postgrest.Attributes.Column("host_nickname")]
    [Newtonsoft.Json.JsonProperty("host_nickname")] public string HostNickname{ get; set; }
    [Supabase.Postgrest.Attributes.Column("server_ip")]
    [Newtonsoft.Json.JsonProperty("server_ip")]     public string ServerIp    { get; set; }
    [Supabase.Postgrest.Attributes.Column("room_status")]
    [Newtonsoft.Json.JsonProperty("room_status")]   public string RoomStatus  { get; set; }
    [Supabase.Postgrest.Attributes.Column("max_players")]
    [Newtonsoft.Json.JsonProperty("max_players")]   public int    MaxPlayers  { get; set; }
}

[System.Serializable]
[Supabase.Postgrest.Attributes.Table("private_room_members")]
public class PrivateRoomMember : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [Newtonsoft.Json.JsonProperty("id")]            public string Id       { get; set; }
    [Supabase.Postgrest.Attributes.Column("room_id")]
    [Newtonsoft.Json.JsonProperty("room_id")]       public string RoomId   { get; set; }
    [Supabase.Postgrest.Attributes.Column("player_id")]
    [Newtonsoft.Json.JsonProperty("player_id")]     public string PlayerId { get; set; }
    [Supabase.Postgrest.Attributes.Column("nickname")]
    [Newtonsoft.Json.JsonProperty("nickname")]      public string Nickname { get; set; }
    [Supabase.Postgrest.Attributes.Column("is_ready")]
    [Newtonsoft.Json.JsonProperty("is_ready")]      public bool   IsReady  { get; set; }
    [Supabase.Postgrest.Attributes.Column("joined_at")]
    [Newtonsoft.Json.JsonProperty("joined_at")]     public string JoinedAt { get; set; }
}

public class RoomMemberInfo
{
    public string PlayerId;
    public string Nickname;
    public bool   IsReady;
}
