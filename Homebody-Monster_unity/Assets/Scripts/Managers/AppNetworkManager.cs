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
        if (ok) Debug.Log($"[AppNetworkManager] 🎮 게임 서버 접속 시작: {ip}:{port}");
        else    Debug.LogError("[AppNetworkManager] 클라이언트 시작 실패");
        return ok;
    }

    public void Disconnect()
    {
        // 로비 채팅 채널 정리 (씬 전환 시 구독 해제)
        DisconnectLobbyChat();

        if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsConnectedClient))
            NetworkManager.Singleton.Shutdown();
    }

    /// <summary>로비 채팅 Realtime 채널만 정리합니다 (NGO 연결과 무관).</summary>
    public async void DisconnectLobbyChat()
    {
        if (SupabaseManager.Instance != null)
        {
            SupabaseManager.Instance.OnLobbyChatReceived      -= HandleLobbyChatReceived;
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

        // [추가] 서버 전송보다 먼저 화면에 즉시 띄움 (로컬 에코 - 빠른 피드백)
        string formatted = $"[{nickname}]: {message}";
        OnChatReceived?.Invoke(formatted);

        if (SupabaseManager.Instance != null && SupabaseManager.Instance.IsLobbyChatSubscribed)
        {
            bool sent = await SupabaseManager.Instance.SendLobbyChatMessage(nickname, message);
            if (!sent)
            {
                // 전송 실패 시 로컬에만 안내 표시
                OnChatReceived?.Invoke("[시스템]: 메시지 전송에 실패했습니다. 잠시 후 다시 시도해주세요.");
            }
        }
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
