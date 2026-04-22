using System;
using System.Collections.Generic;
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
    public event Action<List<string>> OnPlayerListUpdated;
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
        if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsConnectedClient))
            NetworkManager.Singleton.Shutdown();
    }

    // ════════════════════════════════════════════════════════════
    //  로비 연결 (Supabase Realtime 기반, NGO 불필요)
    // ════════════════════════════════════════════════════════════

    public void ConnectToLobby()
    {
        // 현재: 로컬 플레이어만 노출 (향후 Supabase Presence로 확장)
        var players = new List<string> { GameManager.Instance?.currentPlayerId ?? "나" };
        OnPlayerListUpdated?.Invoke(players);
    }

    // ════════════════════════════════════════════════════════════
    //  채팅 (로비 & 결과 화면 공용)
    // ════════════════════════════════════════════════════════════

    public void SendChatMessage(string message)
    {
        string formatted = $"[{GameManager.Instance?.currentPlayerId ?? "?"}]: {message}";
        // TODO: Supabase Realtime 채널을 통해 브로드캐스트 구현
        OnChatReceived?.Invoke(formatted);
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
        if (NetworkManager.Singleton.IsServer) return;
        string reason = NetworkManager.Singleton.DisconnectReason;
        if (string.IsNullOrEmpty(reason)) reason = "서버 연결이 끊어졌습니다.";
        Debug.LogWarning($"[AppNetworkManager] ⚠️ 연결 해제: {reason}");
        OnClientDisconnected?.Invoke(reason);
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
