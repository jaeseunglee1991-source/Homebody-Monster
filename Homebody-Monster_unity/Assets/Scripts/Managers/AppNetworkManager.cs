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

    /// <summary>
    /// [NEW-04] NetworkManager.Shutdown() 후 내부 콜백 목록이 초기화되어
    /// OnEnable에서 한 번 구독한 콜백이 사라진다. 재접속 직전에 명시적으로 재구독.
    /// </summary>
    public void ResubscribeNetworkCallbacks()
    {
        if (NetworkManager.Singleton == null) return;
        NetworkManager.Singleton.OnClientConnectedCallback  -= HandleClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
        NetworkManager.Singleton.OnClientConnectedCallback  += HandleClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;
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

        var netMgr = NetworkManager.Singleton;
        if (netMgr == null) return;

        // [Fix] 데디케이티드 서버 자신(IsServer && !IsHost)은 Shutdown 대상에서 명시 제외.
        // (현재 아래 IsClient 분기로 자연 제외되지만 의도를 명시해 향후 회귀 방지)
        if (netMgr.IsServer && !netMgr.IsHost) return;

        if (netMgr.IsClient || netMgr.IsConnectedClient)
            netMgr.Shutdown();
    }

    /// <summary>
    /// 하위 호환용 동기 래퍼. fire-and-forget 이므로 Presence 해제 완료를
    /// 보장하지 않습니다. 가능하면 DisconnectAsync()를 사용하세요.
    /// </summary>
    public void Disconnect() => _ = DisconnectAsync();

    /// <summary>로비 채팅 Realtime 채널만 정리합니다 (NGO 연결과 무관).</summary>
    public async void DisconnectLobbyChat() => await DisconnectLobbyChatAsync();

    /// <summary>
    /// [#1 수정] 로비 채팅 정리의 awaitable 공개 버전.
    /// 매칭 시작·로그아웃 등 로비를 떠나기 직전 호출하면 Untrack 완료를 기다려
    /// 다른 플레이어 화면의 접속자 목록이 즉시 갱신되도록 보장한다.
    /// </summary>
    public Task DisconnectLobbyChatAwaitable() => DisconnectLobbyChatAsync();

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
        // H-22: try-finally로 예외 발생 시에도 OnLobbyChatReady가 반드시 호출되도록 보장
        bool readyInvoked = false;
        try
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
                readyInvoked = true;

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
        catch (System.Exception e)
        {
            Debug.LogError($"[AppNetworkManager] ConnectToLobby 예외: {e.Message}");
        }
        finally
        {
            // BUG-22: 구독 실패/Supabase 미초기화 시에도 OnLobbyChatReady를 발생시키면
            // sendChatButton.interactable=true로 활성화되지만 실제 전송 시 IsLobbyChatSubscribed=false로
            // 차단되어 "[시스템]: 채팅 서버에 연결되지 않았습니다." 표시 — UX 혼란.
            // 대신 신호를 발송하지 않으면 LobbyUIController.ChatButtonTimeoutRoutine이 10초 후
            // 자동으로 재연결을 시도한다.
            if (!readyInvoked)
                Debug.LogWarning("[AppNetworkManager] OnLobbyChatReady 미발송 — ChatButtonTimeoutRoutine이 재시도");
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
    private void HandleLobbyChatReceived(string nickname, string message, string senderUuid)
    {
        // H-8: 내가 보낸 메시지는 UUID로 정확히 식별 (동명이인 충돌 방지)
        // M-3: myUuid가 null(세션 만료/로그아웃 직후 등)일 때 senderUuid 가드를 통과해
        // 내 메시지가 로컬 에코 + 수신으로 2회 표시되던 버그. UUID가 있어도 myUuid 부재 시
        // 닉네임 폴백으로 자기 메시지 식별.
        string myUuid     = GameManager.Instance?.currentPlayerId;
        string myNickname = GameManager.Instance?.currentPlayerNickname;
        if (!string.IsNullOrEmpty(senderUuid))
        {
            if (!string.IsNullOrEmpty(myUuid))
            {
                if (senderUuid == myUuid) return;
            }
            else if (!string.IsNullOrEmpty(myNickname) && nickname == myNickname)
            {
                return;
            }
        }
        else
        {
            // UUID 누락(레거시 클라이언트) 시 닉네임 폴백
            if (!string.IsNullOrEmpty(myNickname) && nickname == myNickname) return;
        }

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
            nickname = "알 수 없음"; // H-7: UUID 노출 방지

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

        // H-9: 데디케이티드 서버(IsServer && !IsHost)는 다른 클라이언트 이탈을 OnClientDisconnected로 알릴 필요 없으나,
        // Listen-server(Host)는 자기 자신의 NGO 연결이 끊기는 경우 ReconnectManager 트리거가 필요.
        // 기존 IsServer 분기로는 호스트 환경 QA에서 재접속 로직이 절대 동작하지 않아 검증 불가능했음.
        var netMgr = NetworkManager.Singleton;
        bool isDedicatedServer = netMgr != null && netMgr.IsServer && !netMgr.IsHost;
        if (isDedicatedServer)
        {
            // ── 데디케이티드 서버: 이탈한 클라이언트는 NetworkSpawnManager.HandleClientDisconnected가
            //   사망 처리(OnPlayerDied) + alivePlayers 정리를 담당하므로 여기서는 로그만 남김. (BUG-01)
            Debug.LogWarning($"[AppNetworkManager] ⚠️ 클라이언트 이탈 감지 (clientId: {clientId})");
        }
        else
        {
            // ── 클라이언트 / 호스트: 본인 NGO 세션이 끊긴 경우 → 재접속 흐름 트리거 ──
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
