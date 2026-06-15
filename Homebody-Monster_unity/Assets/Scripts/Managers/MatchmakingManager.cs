using UnityEngine;
using System;
using System.Threading.Tasks;

/// <summary>
/// 자동 매칭 — Photon Fusion 세션 매칭 진입.
///
/// [Pass C 정리] NGO 시절의 Supabase-큐 매칭 + 데디케이티드 서버 모드(RunServerLoop/ExecuteServerMatch/
/// SubscribeToQueue 등 ~400줄)는 전부 제거됨 — NGO 연결이 사라져 동작 불가했음.
/// 이제 StartSearch는 곧바로 Photon Fusion 세션 매칭(StartFusionMatch)으로 진입하고, 매칭 자체
/// (첫 피어=Host, 이후=Client)는 FusionLauncher가 Photon으로 처리한다.
///
/// 공개 API/이벤트/Inspector 필드는 로비 UI(LobbyUIController/MatchmakingUX/LobbySettingsPanel)
/// 호환을 위해 그대로 유지(Fusion은 즉시 씬 진입이라 일부 이벤트는 더 이상 발생하지 않음).
/// </summary>
public class MatchmakingManager : MonoBehaviour
{
    public static MatchmakingManager Instance { get; private set; }

    [Header("Matchmaking Settings")]
    public int   maxPlayers     = 8;
    public int   minPlayers     = 1;
    public float maxWaitSeconds = 5f;

    [Header("Fusion 매칭")]
    [Tooltip("레거시 플래그 — 현재는 항상 Fusion 세션 매칭을 사용한다.")]
    public bool   useFusionMatchmaking = true;
    [Tooltip("Fusion 매칭 진입 씬 이름.")]
    public string fusionGameSceneName  = "InGameScene";

    // [Pass C] 데디서버 모드는 제거됐으나 LobbyUIController가 참조하므로 필드만 유지(항상 false).
    public bool isDedicatedServerMode = false;

    // ── 이벤트 (로비 UI 호환 — Fusion 즉시 진입이라 대부분 미발생) ──
#pragma warning disable 67 // 이벤트는 UI 구독 호환용으로 선언만 유지(내부 미발생 가능)
    public event Action<int, int>       OnQueueCountChanged;
    public event Action<float>          OnTimerUpdated;
    public event Action<string, ushort> OnMatchFound;
    public event Action<string>         OnMatchFailed;
    public event Action                 OnMatchmakingFailed;
#pragma warning restore 67
    public event Action<string>         OnStatusMessageChanged;

    private bool isSearching = false;
    /// <summary>매칭 탐색 중 여부(외부 읽기 전용). Fusion 즉시 진입이라 사실상 항상 false.</summary>
    public bool IsSearching => isSearching;

    private void Awake()
    {
        if (Instance == null) { Instance = this; DontDestroyOnLoad(gameObject); }
        else { Destroy(gameObject); }
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API (로비 UI 호출용)
    // ════════════════════════════════════════════════════════════

    /// <summary>매칭 시작 — 곧바로 Photon Fusion 세션 매칭으로 진입한다.</summary>
    public void StartSearch()
    {
        if (isSearching) return;
        StartFusionMatch();
    }

    /// <summary>매칭 취소 — Fusion 즉시 진입이라 정리할 대기 큐가 없다. 상태만 리셋.</summary>
    public void CancelSearch()
    {
        isSearching = false;
        NotifyStatus("매칭이 취소되었습니다.");
    }

    public void CancelMatchmaking() => CancelSearch();

    /// <summary>로그아웃 흐름 호환용 awaitable 취소(즉시 완료).</summary>
    public Task CancelMatchmakingAsync()
    {
        CancelSearch();
        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════
    //  Fusion 매칭 진입
    // ════════════════════════════════════════════════════════════

    private void StartFusionMatch()
    {
        string nickname = GameManager.Instance?.currentPlayerNickname ?? "Unknown";

        // 매치용 캐릭터 랜덤 롤(로그라이트 운빨 배정) — NetPlayer.Spawned가 GameManager.myCharacterData를 제출.
        if (GameManager.Instance != null)
            GameManager.Instance.myCharacterData = StatCalculator.GenerateRandomCharacter(nickname);

        NotifyStatus("매칭 서버(Photon)로 접속합니다...");

        // 로비 채팅/Presence 정리 — 완료를 기다리지 않음(씬 전환 차단 방지).
        _ = AppNetworkManager.Instance?.DisconnectLobbyChatAwaitable();

        FusionLauncher.AutoStartOnLoad = true;
        UnityEngine.SceneManagement.SceneManager.LoadScene(fusionGameSceneName);
    }

    private void NotifyStatus(string msg) => OnStatusMessageChanged?.Invoke(msg);
}
