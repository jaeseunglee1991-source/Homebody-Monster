using Fusion;
using UnityEngine;

/// <summary>
/// [Phase 2-A·2-D] 로비 → 매칭 런처.
/// Play 시 로비 UI(방 이름 + 인원 제한 + 매칭 시작)를 띄우고, 버튼을 누르면
/// 같은 SessionName 세션에 접속한다(AutoHostOrClient: 첫 피어=Host, 이후=Client).
///
/// 토폴로지 무관(설계 규칙 #2): GameMode는 이 한 곳에서만 결정.
///  • 기본: AutoHostOrClient (Host Mode 출시)
///  • 커맨드라인 -server : 로비 UI 없이 GameMode.Server로 즉시 시작 (추후 데디 전환용)
/// </summary>
public class FusionLauncher : MonoBehaviour
{
    [Tooltip("NetworkRunner + 콜백/씬매니저가 붙은 러너 프리팹 (FusionRunner.prefab)")]
    public NetworkRunner RunnerPrefab;

    [Tooltip("기본 게임모드. -server 인자 시 Server로 강제.")]
    public GameMode defaultMode = GameMode.AutoHostOrClient;

    [Tooltip("세션(룸) 이름 — 같은 이름끼리 매칭. 로비 UI에서 수정 가능.")]
    public string sessionName = "HBM_PoC";

    [Tooltip("세션 최대 인원 (가득 차면 추가 입장 거부)")]
    [Range(2, 8)] public int maxPlayers = 8;

    /// <summary>
    /// [3-B] 로비(MatchmakingManager.StartFusionMatch)에서 씬 전환 직전 true로 설정하는 핸드오프.
    /// true면 내부 로비 UI를 건너뛰고 즉시 매칭을 시작한다 (유저는 이미 로비에서 버튼을 눌렀음).
    /// </summary>
    public static bool AutoStartOnLoad = false;

    private enum State { Lobby, Connecting, Connected, Failed }
    private State  _state  = State.Lobby;
    private string _error  = "";

    private void Start()
    {
        // 데디케이티드 서버 인자: 로비 UI 없이 즉시 Server 모드로.
        foreach (var a in System.Environment.GetCommandLineArgs())
            if (a == "-server") { StartMatch(GameMode.Server); return; }

        // [3-B] 실제 로비에서 매칭 버튼으로 진입한 경우 — 즉시 자동 시작.
        if (AutoStartOnLoad)
        {
            AutoStartOnLoad = false; // 1회성 소비 (PoC 단독 실행 시 로비 UI 유지)
            StartMatch(defaultMode);
        }
    }

    private async void StartMatch(GameMode mode)
    {
        if (_state == State.Connecting || _state == State.Connected) return;
        if (RunnerPrefab == null)
        {
            _error = "RunnerPrefab 미지정";
            _state = State.Failed;
            Debug.LogError("[FusionLauncher] RunnerPrefab 미지정 — 연결 불가.");
            return;
        }

        _state = State.Connecting;

        var runner = Instantiate(RunnerPrefab);
        runner.name = "NetworkRunner (Auto)";
        runner.ProvideInput = true;

        if (runner.GetComponent<NetworkSceneManagerDefault>() == null)
            runner.gameObject.AddComponent<NetworkSceneManagerDefault>();
        if (runner.GetComponent<NetworkObjectProviderDefault>() == null)
            runner.gameObject.AddComponent<NetworkObjectProviderDefault>();

        var result = await runner.StartGame(new StartGameArgs
        {
            GameMode     = mode,
            SessionName  = sessionName,
            PlayerCount  = maxPlayers,
            SceneManager = runner.GetComponent<NetworkSceneManagerDefault>(),
        });

        if (result.Ok)
        {
            _state = State.Connected;

            // [3-D fix] save_match_result RPC의 p_room_id 용 방 식별자 설정.
            // NGO 경로의 "ip:port" 대응 — Fusion에선 세션 이름(전 피어 동일)을 사용.
            // 미설정 시 DB가 P0001 "p_room_id must not be empty"로 전적 저장을 거부한다.
            if (GameManager.Instance != null)
                GameManager.Instance.currentRoomId = $"fusion:{runner.SessionInfo?.Name ?? sessionName}";

            Debug.Log($"[FusionLauncher] ✅ 연결 완료 (mode={mode}, session={sessionName}, max={maxPlayers})");
        }
        else
        {
            _state = State.Failed;
            _error = result.ShutdownReason.ToString();
            Debug.LogError($"[FusionLauncher] StartGame 실패: {result.ShutdownReason}");
            if (runner != null) Destroy(runner.gameObject); // 재시도 가능하게 정리
        }
    }

    // ── 로비 UI (OnGUI, 해상도 비례) ─────────────────────────────
    private void OnGUI()
    {
        if (_state == State.Connected) return;

        int   fs = Mathf.Max(24, Screen.height / 30);
        float w  = Mathf.Min(Screen.width * 0.8f, fs * 16f);
        float x  = (Screen.width - w) / 2f;
        float y  = Screen.height * 0.28f;
        float lh = fs * 1.8f;

        var label = new GUIStyle(GUI.skin.label)
        {
            fontSize = fs, fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white }
        };
        var field = new GUIStyle(GUI.skin.textField) { fontSize = fs, alignment = TextAnchor.MiddleCenter };
        var btn   = new GUIStyle(GUI.skin.button)    { fontSize = fs };

        GUI.Label(new Rect(x, y, w, lh), "HomeBody Monster — 매칭", label);
        y += lh * 1.2f;

        // 방 이름
        GUI.Label(new Rect(x, y, w * 0.3f, lh), "방 이름", label);
        sessionName = GUI.TextField(new Rect(x + w * 0.32f, y, w * 0.68f, lh), sessionName, 24, field);
        y += lh * 1.15f;

        // 인원 제한
        GUI.Label(new Rect(x, y, w * 0.3f, lh), $"인원 {maxPlayers}", label);
        if (GUI.Button(new Rect(x + w * 0.32f, y, w * 0.32f, lh), "−", btn)) maxPlayers = Mathf.Max(2, maxPlayers - 1);
        if (GUI.Button(new Rect(x + w * 0.68f, y, w * 0.32f, lh), "+", btn)) maxPlayers = Mathf.Min(8, maxPlayers + 1);
        y += lh * 1.3f;

        switch (_state)
        {
            case State.Lobby:
            case State.Failed:
                if (GUI.Button(new Rect(x, y, w, lh * 1.2f), "매칭 시작", btn))
                    StartMatch(defaultMode);
                if (_state == State.Failed)
                {
                    y += lh * 1.5f;
                    var err = new GUIStyle(label) { normal = { textColor = new Color(1f, 0.4f, 0.4f) } };
                    GUI.Label(new Rect(x, y, w, lh), $"실패: {_error} — 다시 시도", err);
                }
                break;

            case State.Connecting:
                GUI.Label(new Rect(x, y, w, lh * 1.2f), "접속 중...", label);
                break;
        }
    }
}
