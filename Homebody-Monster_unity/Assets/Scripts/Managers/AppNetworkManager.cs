using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

/// <summary>
/// NGO 기반 네트워크 관리자.
/// 데디케이티드 서버(StartAsDedicatedServer)와 클라이언트(ConnectToGameServer) 양쪽을 지원합니다.
/// 로비 채팅은 Supabase Realtime 기반으로 NGO 없이 동작합니다.
/// </summary>
public class AppNetworkManager : MonoBehaviour
{
    public static AppNetworkManager Instance { get; private set; }

    public const ushort DefaultPort = 7777;

    // ── 이벤트 ─────────────────────────────────────────────────
    public event Action<string>       OnChatReceived;
    public event Action<List<string>> OnPlayerPresenceUpdated; // 상세 닉네임 목록 전달
    public event Action               OnClientConnected;
    public event Action<string>       OnClientDisconnected;  // 파라미터: 연결 해제 사유
    /// <summary>로비 채팅 채널 구독이 완료되어 전송 가능 상태가 됐을 때 발생합니다.</summary>
    public event Action               OnLobbyChatReady;

    private void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); }
    }

    private void OnEnable()
    {
        if (NetworkManager.Singleton == null) return;
        NetworkManager.Singleton.OnClientConnectedCallback   += HandleClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback  += HandleClientDisconnected;
    }

    private void OnDisable()
    {
        if (NetworkManager.Singleton == null) return;
        NetworkManager.Singleton.OnClientConnectedCallback   -= HandleClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback  -= HandleClientDisconnected;
    }

    // ════════════════════════════════════════════════════════════
    //  서버 모드
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 데디케이티드 서버 프로세스에서 NGO 서버를 엽니다.
    /// MatchmakingManager.StartServerMode()에서 호출됩니다.
    /// </summary>
    public bool StartAsDedicatedServer(ushort port = DefaultPort)
    {
        if (!ValidateNGO()) return false;

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData("0.0.0.0", port);

        bool ok = NetworkManager.Singleton.StartServer();
        if (ok) Debug.Log($"[AppNetworkManager] ☁️ 데디케이티드 서버 가동 완료 (port: {port})");
        else    Debug.LogError("[AppNetworkManager] 서버 시작 실패");
        return ok;
    }

    // ════════════════════════════════════════════════════════════
    //  클라이언트 모드
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 매칭 완료 후 서버에서 받은 IP:Port로 인게임 서버에 접속합니다.
    /// MatchmakingManager.HandleMatchSuccess()에서 호출됩니다.
    /// </summary>
    public bool ConnectToGameServer(string ip, ushort port = DefaultPort)
    {
        if (!ValidateNGO()) return false;

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData(ip, port);

        bool ok = NetworkManager.Singleton.StartClient();
        if (ok)
        {
            Debug.Log($"[AppNetworkManager] 🎮 게임 서버 접속 시작: {ip}:{port}");
            // [FEATURE] 재접속을 위해 서버 정보 저장
            ReconnectManager.Instance?.RegisterServer(ip, port);
        }
        else
            Debug.LogError("[AppNetworkManager] 클라이언트 시작 실패");
        return ok;
    }

    /// <summary>
    /// NGO 연결과 로비 채팅 채널을 모두 정리합니다.
    ///
    /// [버그 수정] 기존 Disconnect()는 async void DisconnectLobbyChat()을
    /// await 없이 fire-and-forget으로 호출한 뒤 즉시 NGO Shutdown()을 실행했습니다.
    /// DisconnectLobbyChat 내부의 UntrackLobbyPresence / UnsubscribeLobbyChat이
    /// 완료되기 전에 NGO가 종료되어 Supabase Presence가 해제되지 않고 남음.
    /// 결과: 상대방 로비 접속자 수가 줄어들지 않는 '유령 접속자' 버그.
    ///
    /// 수정: DisconnectAsync()를 async Task로 변경하고 DisconnectLobbyChat()을
    /// await로 완료 대기 후 NGO Shutdown()을 호출합니다.
    /// 호출부(GameManager.ResetForNewMatch 등)에서 _ = DisconnectAsync() 패턴 사용.
    /// </summary>
    public async Task DisconnectAsync()
    {
        // 1. Supabase Presence / 채팅 채널 완전 정리 후 NGO 종료
        await DisconnectLobbyChatAsync();

        if (NetworkManager.Singleton != null &&
            (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsConnectedClient))
            NetworkManager.Singleton.Shutdown();
    }

    /// <summary>
    /// 하위 호환용 동기 래퍼. fire-and-forget 이므로 Presence 해제 완료를
    /// 보장하지 않습니다. 가능하면 DisconnectAsync()를 사용하세요.
    /// </summary>
    public void Disconnect() => _ = DisconnectAsync();

    /// <summary>로비 채팅 Realtime 채널만 정리합니다 (NGO 연결과 무관).</summary>
    public async void DisconnectLobbyChat() => await DisconnectLobbyChatAsync();

    /// <summary>
    /// 로비 채팅 채널 정리 내부 구현 (awaitable).
    /// DisconnectAsync()와 DisconnectLobbyChat() 양쪽에서 호출됩니다.
    /// </summary>
    private async Task DisconnectLobbyChatAsync()
    {
        if (SupabaseManager.Instance != null)
        {
            SupabaseManager.Instance.OnLobbyChatReceived    -= HandleLobbyChatReceived;
            SupabaseManager.Instance.OnLobbyPresenceUpdated -= HandlePresenceUpdated;
            await SupabaseManager.Instance.UntrackLobbyPresence();
            await SupabaseManager.Instance.UnsubscribeLobbyChat();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  로비 연결 (Supabase Realtime 기반, NGO 불필요)
    // ════════════════════════════════════════════════════════════

    public async void ConnectToLobby()
    {
        if (SupabaseManager.Instance != null && SupabaseManager.Instance.IsInitialized)
        {
            SupabaseManager.Instance.OnLobbyChatReceived         -= HandleLobbyChatReceived;      // 중복 방지
            SupabaseManager.Instance.OnLobbyChatReceived         += HandleLobbyChatReceived;
            SupabaseManager.Instance.OnLobbyPresenceUpdated      -= HandlePresenceUpdated;   // 중복 방지
            SupabaseManager.Instance.OnLobbyPresenceUpdated      += HandlePresenceUpdated;
            await SupabaseManager.Instance.SubscribeLobbyChat();

            // 채팅 채널 구독 완료 → LobbyUIController에 전송 버튼 활성화 신호
            OnLobbyChatReady?.Invoke();

            // 채널 구독 완료 후 닉네임이 이미 로드된 경우 즉시 Track
            // (RefreshUserProfileUI가 먼저 끝난 경쟁 조건 방어)
            string nickname = GameManager.Instance?.currentPlayerNickname;
            if (!string.IsNullOrEmpty(nickname))
                SupabaseManager.Instance.TrackLobbyPresence(nickname);
        }
        else
        {
            Debug.LogWarning("[AppNetworkManager] Supabase 미초기화 — 로비 오프라인 모드");
            OnPlayerPresenceUpdated?.Invoke(new List<string>()); // 오프라인 폴백: 빈 리스트
        }
    }

    /// <summary>
    /// 닉네임 로드 완료 후 LobbyUIController.RefreshUserProfileUI()에서 호출합니다.
    /// Supabase Presence에 현재 유저를 등록하여 접속자 수를 실시간 동기화합니다.
    /// </summary>
    public void TrackLobbyPresence(string nickname)
    {
        SupabaseManager.Instance?.TrackLobbyPresence(nickname);
    }

    private void HandlePresenceUpdated(List<string> nicknames)
    {
        OnPlayerPresenceUpdated?.Invoke(nicknames);
    }

    /// <summary>Supabase Realtime에서 수신한 채팅 메시지를 LobbyUIController로 전달합니다.</summary>
    private void HandleLobbyChatReceived(string nickname, string message)
    {
        // 내가 보낸 메시지는 이미 SendChatMessage에서 즉시 그렸으므로 무시 (중복 방지)
        string myNickname = GameManager.Instance?.currentPlayerNickname;
        if (!string.IsNullOrEmpty(myNickname) && nickname == myNickname) return;

        string formatted = $"[{nickname}]: {message}";
        OnChatReceived?.Invoke(formatted);
    }

    // ════════════════════════════════════════════════════════════
    //  채팅 전송 (로비 전용 — Supabase Realtime Broadcast)
    // ════════════════════════════════════════════════════════════

    public async void SendChatMessage(string message)
    {
        // UID 대신 실제 닉네임 사용
        string nickname = GameManager.Instance?.currentPlayerNickname;
        if (string.IsNullOrEmpty(nickname))
            nickname = GameManager.Instance?.currentPlayerId ?? "???"; // 닉네임 없으면 UID로 폴백

        string formatted = $"[{nickname}]: {message}";

        if (SupabaseManager.Instance != null && SupabaseManager.Instance.IsLobbyChatSubscribed)
        {
            bool sent = await SupabaseManager.Instance.SendLobbyChatMessage(nickname, message);
            if (sent)
            {
                // 전송 성공 시에만 로컬 에코 표시
                // (Realtime Broadcast는 송신자에게 돌아오지 않으므로 직접 표시)
                OnChatReceived?.Invoke(formatted);
            }
            // 쿨다운 실패: 조용히 무시 (스팸 방지 — 의도된 동작)
        }
        else if (SupabaseManager.Instance == null)
        {
            OnChatReceived?.Invoke("[시스템]: 채팅 서버에 연결되지 않았습니다.");
        }
        // IsLobbyChatSubscribed == false: 버튼 자체가 비활성화돼 있으므로 여기 진입 안 함
    }

    // ════════════════════════════════════════════════════════════
    //  NGO 콜백
    // ════════════════════════════════════════════════════════════

    private void HandleClientConnected(ulong clientId)
    {
        if (NetworkManager.Singleton.IsServer) return; // 서버는 다른 클라이언트 접속 이벤트 무시
        Debug.Log($"[AppNetworkManager] ✅ 서버 연결 성공 (clientId: {clientId})");
        OnClientConnected?.Invoke();
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        string reason = NetworkManager.Singleton != null && !string.IsNullOrEmpty(NetworkManager.Singleton.DisconnectReason)
            ? NetworkManager.Singleton.DisconnectReason
            : "연결이 끊어졌습니다.";

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            // ── 서버: 이탈한 클라이언트의 PlayerController를 찾아 InGameManager에 알림 ──
            Debug.LogWarning($"[AppNetworkManager] ⚠️ 클라이언트 이탈 감지 (clientId: {clientId})");
            PlayerController disconnected = FindPlayerByClientId(clientId);
            if (disconnected != null)
                InGameManager.Instance?.OnPlayerDisconnected(disconnected);
        }
        else
        {
            // ── 클라이언트: 로컬 유저가 서버와 연결이 끊긴 경우 ──
            Debug.LogWarning($"[AppNetworkManager] ⚠️ 서버 연결 해제: {reason}");
            OnClientDisconnected?.Invoke(reason);
        }
    }

    /// <summary>
    /// NGO NetworkObject를 순회하여 clientId에 해당하는 PlayerController를 반환합니다.
    /// </summary>
    private static PlayerController FindPlayerByClientId(ulong clientId)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
        {
            var netObj = obj.GetComponent<Unity.Netcode.NetworkObject>();
            if (netObj != null && netObj.OwnerClientId == clientId)
                return obj;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════
    //  내부 유틸
    // ════════════════════════════════════════════════════════════

    private bool ValidateNGO()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[AppNetworkManager] NetworkManager.Singleton이 없습니다. 씬에 NGO NetworkManager 컴포넌트를 추가하세요.");
            return false;
        }
        if (NetworkManager.Singleton.GetComponent<UnityTransport>() == null)
        {
            Debug.LogError("[AppNetworkManager] UnityTransport 컴포넌트를 찾을 수 없습니다. NGO NetworkManager 오브젝트에 추가하세요.");
            return false;
        }
        return true;
    }
}
