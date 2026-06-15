using System.Collections.Generic;
using System.Threading.Tasks;
using Fusion;
using UnityEngine;

/// <summary>
/// [Phase 1-G / 3-C] Fusion 매치 흐름 관리자 (NGO InGameManager의 매치 루프 부분 대체).
/// 호스트가 runner.Spawn으로 1개 생성. StateAuthority가 생존자/사망 순위/생존시간을 집계하고
/// 마지막 1인이 남으면 매치를 종료, 각 클라이언트에 본인 결과를 전달한다.
///
/// [3-C 결과 저장 — 위임 패턴(기존 NotifyMatchResultClientRpc 포팅)]
/// 호스트는 다른 유저의 Supabase 인증이 없으므로, 결과를 [RpcTarget]으로 각 소유
/// 클라이언트에 보내고 클라이언트 본인이 SaveMatchResult/리더보드를 저장한다.
/// </summary>
public class NetMatch : NetworkBehaviour
{
    [Networked] public int                Phase          { get; set; } // 0=진행, 1=종료
    [Networked] public int                AliveCount     { get; set; }
    [Networked] public NetworkString<_32> WinnerName     { get; set; }
    [Networked] public NetworkBool        HasStarted     { get; set; } // 2인 이상 관측됨
    [Networked] private NetworkBool       RestartPending { get; set; }

    // ── [3-D] 부활권 매치 공유 카운터 ──
    [Networked] public int   ReviveUsedCount { get; set; }
    [Networked] public float MatchStartTime  { get; set; }

    // [3-C fix] 매치 고유 ID — save_match_result의 중복 방지(player+room_id 유니크)에 걸리지 않도록
    // 세션당 고정이 아니라 **매치마다** 호스트가 새로 생성해 전 피어에 공유한다.
    [Networked] public NetworkString<_64> MatchId { get; set; }

    // [3-E] 리롤 윈도우 — 매치 시작 직후 15초(CharacterRerollSystem.RerollWindowSecs) 준비 시간.
    // 이 동안 전투 잠금 + 피자 리롤 가능 (기존 BeginGameClientRpc → OpenRerollWindow 대응).
    [Networked] public TickTimer RerollWindow { get; set; }

    public bool  RerollOpen      => !RerollWindow.ExpiredOrNotRunning(Runner);
    public float RerollRemaining => RerollWindow.RemainingTime(Runner) ?? 0f;

    public static int MaxReviveCount => GameBalanceConfig.Get()?.ReviveMaxPerMatch ?? 3;
    /// <summary>매치 경과 시간(초) — 부활 가능 시간 판정용.</summary>
    public float Elapsed => HasStarted ? Runner.SimulationTime - MatchStartTime : 0f;

    /// <summary>전투 경과 시간(초) — 준비/리롤 윈도우(15초)를 제외. 타이머 표시·시간제한 판정용.
    /// 준비 시간 동안엔 0이므로 제한 타이머가 흐르지 않는다.</summary>
    public float CombatElapsed => Mathf.Max(0f, Elapsed - CharacterRerollSystem.RerollWindowSecs);

    [Tooltip("매치 제한 시간(초). 0이면 무제한(마지막 1인까지). 초과 시 최고 HP 생존자 승리.")]
    public float timeLimitSeconds = 120f;

    // ── 호스트 전용 집계 (결과 계산은 StateAuthority에서만 수행) ──
    private readonly Dictionary<PlayerRef, int>   _ranks          = new();
    private readonly Dictionary<PlayerRef, float> _deathTimes     = new();
    private readonly HashSet<NetPlayer>           _processedDeaths = new();

    // ── 클라 로컬: 수신한 내 결과 (종료 화면 표시용) ──
    private static string _localResultText = "";

    /// <summary>수신한 내 매치 결과 문자열(없으면 ""). NetHudBridge 종료 배너에서 사용.</summary>
    public static string LocalResultText => _localResultText;

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;

        // 재시작은 반드시 틱 안에서 처리해야 NetworkTransform이 위치 재배치를 반영한다.
        if (RestartPending)
        {
            RestartPending = false;
            Phase = 0; HasStarted = false; WinnerName = default;
            ReviveUsedCount = 0;
            MatchId = NewMatchId(); // 새 매치 = 새 ID (HasStarted 재설정 시에도 갱신됨)
            _ranks.Clear(); _deathTimes.Clear(); _processedDeaths.Clear();
            MatchStartTime = Runner.SimulationTime;
            foreach (var p in FindObjectsByType<NetPlayer>(FindObjectsSortMode.None))
                p.ReviveAt(NetSpawnPoints.Spawn());
            return;
        }

        var players = FindObjectsByType<NetPlayer>(FindObjectsSortMode.None);
        int alive = 0; NetPlayer last = null;
        bool revivePending = false;
        foreach (var p in players)
        {
            if (!p.IsDead) { alive++; last = p; }
            if (p.RevivePending) revivePending = true;
        }

        AliveCount = alive;
        if (!HasStarted && players.Length >= 2)
        {
            HasStarted     = true;
            MatchStartTime = Runner.SimulationTime;
            MatchId        = NewMatchId();
            // [3-E] 준비/리롤 윈도우 시작 (전투 잠금).
            RerollWindow   = TickTimer.CreateFromSeconds(Runner, CharacterRerollSystem.RerollWindowSecs);
        }

        // [3-D] 부활로 살아난 플레이어 — 사망 기록 롤백 (기존 OnPlayerRevived의 랭크 제거와 동일).
        foreach (var p in players)
        {
            if (p.IsDead || !_processedDeaths.Contains(p)) continue;
            _processedDeaths.Remove(p);
            var pr = p.Object.InputAuthority;
            _ranks.Remove(pr);
            _deathTimes.Remove(pr);
        }

        // [3-C] 사망 순위·시각 기록 (사망 직후 생존자 수 + 1 = 순위, 기존 OnPlayerDied 방식).
        foreach (var p in players)
        {
            if (!p.IsDead || _processedDeaths.Contains(p)) continue;
            _processedDeaths.Add(p);
            var pr = p.Object.InputAuthority;
            if (pr != PlayerRef.None)
            {
                _ranks[pr]      = alive + 1;
                _deathTimes[pr] = Runner.SimulationTime;
            }
        }

        // [3-D] 부활 결정 대기 중에는 매치 종료 보류 (부활하면 매치가 계속되므로).
        if (Phase == 0 && HasStarted && alive <= 1 && !revivePending)
        {
            Phase      = 1;
            WinnerName = (alive == 1 && last != null) ? last.Nickname : default;
            SendResults(players, last);
        }
        // [5-B] 시간 제한 초과 → 최고 HP 생존자 승리 (무한 매치 방지, 기존 InGameManager 규칙).
        // 준비/리롤 윈도우 제외한 전투 경과 기준(CombatElapsed) — 준비 15초는 제한에 포함 안 함.
        else if (Phase == 0 && HasStarted && timeLimitSeconds > 0f
                 && CombatElapsed >= timeLimitSeconds && !revivePending)
        {
            NetPlayer best = null; float bestHp = -1f;
            foreach (var p in players)
                if (!p.IsDead && p.Hp > bestHp) { bestHp = p.Hp; best = p; }
            Phase      = 1;
            WinnerName = best != null ? best.Nickname : default;
            SendResults(players, best);
        }
    }

    // ── [3-C] 매치 종료 — 각 클라이언트에 본인 결과 전달 (호스트 전용) ──
    private void SendResults(NetPlayer[] players, NetPlayer winner)
    {
        float total = Runner.SimulationTime - MatchStartTime;
        foreach (var p in players)
        {
            var pr = p.Object.InputAuthority;
            if (pr == PlayerRef.None) continue; // 봇/무소유 객체 제외

            bool  win      = p == winner && !p.IsDead;
            int   rank     = win ? 1 : (_ranks.TryGetValue(pr, out int r) ? r : 2);
            float survived = win || !_deathTimes.TryGetValue(pr, out float dt)
                ? total
                : dt - MatchStartTime;

            ResultRpc(pr, win, rank, p.KillCount, survived);
        }
    }

    /// <summary>
    /// 서버 → 해당 플레이어에게만: 본인 매치 결과 전달.
    /// 수신 클라이언트가 자신의 인증 세션으로 Supabase 전적/리더보드 저장 (위임 패턴).
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void ResultRpc([RpcTarget] PlayerRef player, NetworkBool isWinner, int rank, int kills, float survived)
    {
        _localResultText = $"{(isWinner ? "🏆 승리" : "패배")} · {rank}위 · 킬 {kills} · 생존 {survived:0}초";
        Debug.Log($"[NetMatch] 내 결과 수신: {_localResultText}");

        // 실 게임 흐름(GameManager/Supabase 존재 시)에만 저장 — PoC 단독 실행은 표시만.
        if (GameManager.Instance != null)
        {
            // [3-C fix] 매치 고유 ID를 room_id로 사용 — 세션당 고정 ID는 두 번째 매치부터
            // save_match_result의 중복 방지("Duplicate result for room")에 걸린다.
            if (!MatchId.ToString().Equals(string.Empty))
                GameManager.Instance.currentRoomId = MatchId.ToString();

            GameManager.Instance.lastMatchResult = new MatchResult
            {
                isWinner = isWinner, rank = rank, killCount = kills, survivedTime = survived
            };

            if (SupabaseManager.Instance != null)
                GameManager.Instance.MatchResultSaveTask = SaveResultAsync(isWinner, rank, kills, survived);
        }
        LeaderboardManager.Instance?.SubmitMatchResult(isWinner, rank, kills, survived);
    }

    private static async Task SaveResultAsync(bool win, int rank, int kills, float time)
    {
        try
        {
            // 성공/실패 로그는 SaveMatchResult 내부에서 출력됨("🏆 게임 결과 저장 완료" / "⚠️ 결과 저장 실패").
            // 예외를 내부에서 삼키므로 여기서 성공 로그를 찍으면 거짓 성공이 된다 — 출력하지 않음.
            await SupabaseManager.Instance.SaveMatchResult(win, rank, kills, time);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[NetMatch] 전적 저장 호출 실패: {e.Message}");
        }
    }

    /// <summary>매치 고유 room_id 생성 (호스트 전용) — "fusion:세션명:매치GUID8".</summary>
    private string NewMatchId()
    {
        string session = Runner.SessionInfo != null ? Runner.SessionInfo.Name : "unknown";
        string guid    = System.Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"fusion:{session}:{guid}";
    }

    /// <summary>호스트 OnGUI 버튼에서 호출 — 다음 틱에 FixedUpdateNetwork가 처리.</summary>
    public void Restart()
    {
        if (!HasStateAuthority) return;
        RestartPending = true;
    }

    // ── 간이 HUD (모든 피어 표시) ──────────────────────────────
    private void OnGUI()
    {
        if (!Object.IsValid) return;

        int   fs = Mathf.Max(18, Screen.height / 34); // 해상도 비례 (3-D: 축소)
        float w  = Screen.width;

        var top = new GUIStyle(GUI.skin.label)
        {
            fontSize  = fs,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = Color.white }
        };

        // [Pass B] 캔버스 HUD(InGameHUD)가 생존자/타이머/WINNER/결과를 표시하면 OnGUI 라벨은 생략(중복 방지).
        // 단, 호스트 Restart / 로비 나가기 버튼은 캔버스에 대응 UI가 없으므로 항상 OnGUI로 유지.
        bool drawLabels = !NetHudBridge.Active;

        if (drawLabels)
            GUI.Label(new Rect(0, fs * 0.4f, w, fs * 1.6f), $"생존: {AliveCount}", top);

        // [5-B] 남은 시간 (진행 중·시간제한 설정 시)
        if (drawLabels && Phase == 0 && HasStarted && timeLimitSeconds > 0f)
        {
            float rem = Mathf.Max(0f, timeLimitSeconds - Elapsed);
            GUI.Label(new Rect(0, fs * 1.9f, w, fs * 1.4f),
                $"⏱ {Mathf.FloorToInt(rem / 60f)}:{Mathf.FloorToInt(rem % 60f):00}", top);
        }

        if (Phase == 1)
        {
            if (drawLabels)
            {
                var win = new GUIStyle(top) { fontSize = (int)(fs * 1.25f) };
                win.normal.textColor = new Color(1f, 0.85f, 0.2f);
                string name = WinnerName.ToString();
                GUI.Label(new Rect(0, Screen.height / 2f - fs * 4.2f, w, fs * 1.8f),
                    string.IsNullOrEmpty(name) ? "매치 종료 (무승부)" : $"🏆 WINNER: {name}", win);

                // [3-C] 내 결과 (수신 시 표시)
                if (!string.IsNullOrEmpty(_localResultText))
                    GUI.Label(new Rect(0, Screen.height / 2f - fs * 2.1f, w, fs * 1.5f), _localResultText, top);
            }

            var btn = new GUIStyle(GUI.skin.button) { fontSize = (int)(fs * 0.95f) };
            float bw = w * 0.5f, bh = fs * 2.2f;
            float by = Screen.height / 2f + fs * 0.2f;

            if (HasStateAuthority)
            {
                if (GUI.Button(new Rect((w - bw) / 2f, by, bw, bh), "Restart (Host)", btn))
                    Restart();
                by += bh + fs * 0.4f;
            }

            // [3-B/5-A] 로비 복귀 — Runner.Shutdown()만 호출. 씬 복귀는 OnShutdown(PoCNetworkCallbacks)이
            // 중앙 처리하므로 호스트 이탈·클라 이탈·비정상 끊김이 모두 동일 경로로 graceful 복귀.
            if (GUI.Button(new Rect((w - bw) / 2f, by, bw, bh), "로비로 나가기", btn))
                Runner.Shutdown();
        }
    }
}
