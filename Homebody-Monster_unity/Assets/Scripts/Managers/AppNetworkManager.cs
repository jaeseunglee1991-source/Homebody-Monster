using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 로비 매니저 — Supabase Realtime 기반 로비 채팅 / Presence(접속자 목록) 전용.
///
/// [Pass C] NGO 연결부(StartAsDedicatedServer / ConnectToGameServer / NGO 콜백 / 재접속 트리거)는
/// 제거됨 — 인게임 네트워킹은 Fusion(FusionLauncher/NetworkRunner)이 담당한다.
/// 로비 채팅·접속자 목록은 NGO와 무관한 Supabase 기능이라 그대로 유지.
/// </summary>
public class AppNetworkManager : MonoBehaviour
{
    public static AppNetworkManager Instance { get; private set; }

    // [Pass C] 레거시 포트 상수 — 일부 매칭 코드가 아직 참조하나 NGO 연결 제거로 실제 미사용.
    public const ushort DefaultPort = 7777;

    // ── 이벤트 (로비 UI 구독) ───────────────────────────────────
    public event Action<string>       OnChatReceived;
    public event Action<List<string>> OnPlayerPresenceUpdated; // 상세 닉네임 목록
    /// <summary>로비 채팅 채널 구독 완료 → 전송 버튼 활성화 신호.</summary>
    public event Action               OnLobbyChatReady;

    private void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); }
    }

    // ════════════════════════════════════════════════════════════
    //  로비 연결 (Supabase Realtime — NGO 불필요)
    // ════════════════════════════════════════════════════════════

    public async void ConnectToLobby()
    {
        bool readyInvoked = false;
        try
        {
            if (SupabaseManager.Instance != null && SupabaseManager.Instance.IsInitialized)
            {
                SupabaseManager.Instance.OnLobbyChatReceived    -= HandleLobbyChatReceived; // 중복 방지
                SupabaseManager.Instance.OnLobbyChatReceived    += HandleLobbyChatReceived;
                SupabaseManager.Instance.OnLobbyPresenceUpdated -= HandlePresenceUpdated;
                SupabaseManager.Instance.OnLobbyPresenceUpdated += HandlePresenceUpdated;
                await SupabaseManager.Instance.SubscribeLobbyChat();

                OnLobbyChatReady?.Invoke();
                readyInvoked = true;

                string nickname = GameManager.Instance?.currentPlayerNickname;
                if (!string.IsNullOrEmpty(nickname))
                    SupabaseManager.Instance.TrackLobbyPresence(nickname);
            }
            else
            {
                Debug.LogWarning("[AppNetworkManager] Supabase 미초기화 — 로비 오프라인 모드");
                OnPlayerPresenceUpdated?.Invoke(new List<string>());
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[AppNetworkManager] ConnectToLobby 예외: {e.Message}");
        }
        finally
        {
            if (!readyInvoked)
                Debug.LogWarning("[AppNetworkManager] OnLobbyChatReady 미발송 — ChatButtonTimeoutRoutine이 재시도");
        }
    }

    /// <summary>닉네임 로드 완료 후 호출 — Supabase Presence에 등록(접속자 수 동기화).</summary>
    public void TrackLobbyPresence(string nickname)
    {
        SupabaseManager.Instance?.TrackLobbyPresence(nickname);
    }

    private void HandlePresenceUpdated(List<string> nicknames)
    {
        OnPlayerPresenceUpdated?.Invoke(nicknames);
    }

    /// <summary>Supabase Realtime에서 수신한 채팅을 로비 UI로 전달(내 메시지 중복 제거).</summary>
    private void HandleLobbyChatReceived(string nickname, string message, string senderUuid)
    {
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
            if (!string.IsNullOrEmpty(myNickname) && nickname == myNickname) return;
        }

        OnChatReceived?.Invoke($"[{nickname}]: {message}");
    }

    // ════════════════════════════════════════════════════════════
    //  채팅 전송 (로비 — Supabase Realtime Broadcast)
    // ════════════════════════════════════════════════════════════

    public async void SendChatMessage(string message)
    {
        string nickname = GameManager.Instance?.currentPlayerNickname;
        if (string.IsNullOrEmpty(nickname)) nickname = "알 수 없음";

        string formatted = $"[{nickname}]: {message}";

        if (SupabaseManager.Instance != null && SupabaseManager.Instance.IsLobbyChatSubscribed)
        {
            bool sent = await SupabaseManager.Instance.SendLobbyChatMessage(nickname, message);
            if (sent) OnChatReceived?.Invoke(formatted); // Broadcast는 송신자에게 안 돌아오므로 로컬 에코
        }
        else if (SupabaseManager.Instance == null)
        {
            OnChatReceived?.Invoke("[시스템]: 채팅 서버에 연결되지 않았습니다.");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  정리 (로비 떠나기/로그아웃)
    // ════════════════════════════════════════════════════════════

    /// <summary>로비 채팅/Presence 정리. (NGO Shutdown은 Pass C에서 제거 — Fusion은 Runner.Shutdown 별도)</summary>
    public Task DisconnectAsync() => DisconnectLobbyChatAsync();

    /// <summary>하위 호환 동기 래퍼.</summary>
    public void Disconnect() => _ = DisconnectLobbyChatAsync();

    public async void DisconnectLobbyChat()
    {
        try { await DisconnectLobbyChatAsync(); }
        catch (System.Exception e) { Debug.LogWarning($"[AppNet] DisconnectLobbyChat 정리 중 예외 무시: {e.Message}"); }
    }

    /// <summary>로비를 떠나기 직전 호출 — Untrack 완료를 기다려 접속자 목록 즉시 갱신.</summary>
    public Task DisconnectLobbyChatAwaitable() => DisconnectLobbyChatAsync();

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
}
