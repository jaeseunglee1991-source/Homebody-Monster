using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 인게임 전체를 관장하는 매니저.
///
/// 변경 사항:
///  - ElapsedGameTime 프로퍼티 추가: 게임 경과 시간 (PlayerNetworkSync 판정용)
///  - AliveCount 프로퍼티 추가: 현재 생존자 수 (부활권 조건 판정용)
///  - MatchReviveUsedCount / MaxMatchReviveCount 추가: 매치 전체 부활 횟수 공유 카운터
///  - OnReviveTicketUsed() 추가: 부활 확정 시 서버에서 호출
///  - OnPlayerRevived() 유지: 부활한 플레이어를 alivePlayers에 재등록
/// </summary>
public class InGameManager : MonoBehaviour
{
    public static InGameManager Instance { get; private set; }

    // ── 매치 전체에서 공유되는 부활권 제한 ────────────────────
    /// <summary>한 매치에서 전체 플레이어가 공유하는 최대 부활 횟수</summary>
    public const int MaxMatchReviveCount = 3;

    [Header("Game Settings")]
    public int minPlayers = 2;
    public int maxPlayers = 8;

    [Header("Spawn Points")]
    public Transform[] spawnPoints;

    [Header("Game Timer")]
    [Tooltip("0이면 무제한 (마지막 1명 남을 때까지), 0보다 크면 시간 제한 (최고 HP 플레이어 승리)")]
    public float timeLimitSeconds = 0f;

    public bool isGameActive { get; private set; } = false;

    // ── 부활권 관련 상태 ────────────────────────────────────────
    /// <summary>이번 매치에서 지금까지 사용된 부활 횟수 (전체 플레이어 합산, 최대 3회)</summary>
    public int MatchReviveUsedCount { get; private set; } = 0;

    /// <summary>게임 시작 후 경과 시간 (초). 부활 가능 시간 60초 판정에 사용.</summary>
    public float ElapsedGameTime { get; private set; } = 0f;

    /// <summary>현재 생존 중인 플레이어 수. 2명 이하이면 부활권 사용 불가.</summary>
    public int AliveCount => alivePlayers.Count;

    private readonly List<PlayerController> alivePlayers = new List<PlayerController>();
    // 클라이언트ID별 최종 순위 — OnPlayerDied 시점에 기록 (FinishGame 호출 시 alivePlayers.Count=1로 역산 불가)
    private readonly Dictionary<ulong, int>   _playerFinalRanks = new Dictionary<ulong, int>();
    /// <summary>
    /// 클라이언트ID별 사망 시각 (GetSyncedTime() 기준).
    ///
    /// [이전 버그]
    /// FinishGame()에서 survived = GetSyncedTime() - gameStartTime 을 단일값으로 계산해
    /// 모든 플레이어에게 동일하게 전달했음.
    /// 결과적으로 1분에 죽은 플레이어도, 4분에 죽은 플레이어도 똑같이
    /// 게임 전체 시간(예: 5분)이 survived_time으로 DB에 저장됨 → 전적 데이터 오염.
    ///
    /// [수정]
    /// OnPlayerDied()에서 사망 시각을 기록하고, FinishGame()에서 개인별 경과 시간을 계산.
    /// </summary>
    private readonly Dictionary<ulong, float> _playerDeathTimes = new Dictionary<ulong, float>();
    private bool  gameEnded      = false;
    private float gameStartTime  = -1f; // NetworkManager.ServerTime 기준, -1 = 미설정
    private float cleanupTimer   = 0f;
    // 하위 호환: 로컬(호스트) 플레이어 순위 캐시 (listen-server 환경에서 폴백용)
    private int _localPlayerFinalRank = 0;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    private void Start()
    {
        // 게임 시작 카운트다운은 서버가 단독으로 주도합니다.
        // 순수 클라이언트는 NetworkSpawnManager.NotifyGameStartedClientRpc 수신 후 시작 상태로 전환됩니다.
        var netMgr = Unity.Netcode.NetworkManager.Singleton;
        if (netMgr != null && netMgr.IsListening && !netMgr.IsServer) return;
        StartCoroutine(GameStartSequence());
    }

    // NetworkManager.ServerTime 기반 시간 (단일/멀티 공통 사용)
    private float GetSyncedTime()
    {
        if (Unity.Netcode.NetworkManager.Singleton != null &&
            Unity.Netcode.NetworkManager.Singleton.IsListening)
            return (float)Unity.Netcode.NetworkManager.Singleton.ServerTime.TimeAsFloat;
        return Time.time;
    }

    private void Update()
    {
        if (isGameActive && !gameEnded)
        {
            ElapsedGameTime = gameStartTime >= 0f
                ? GetSyncedTime() - gameStartTime
                : 0f;

            if (InGameHUD.Instance != null)
                InGameHUD.Instance.UpdateTimer(ElapsedGameTime);

            cleanupTimer += Time.deltaTime;
            if (cleanupTimer >= 1f)
            {
                cleanupTimer = 0f;
                CleanUpDisconnectedPlayers();
            }
        }
    }

    public void RegisterPlayer(PlayerController player)
    {
        if (player == null || alivePlayers.Contains(player)) return;
        alivePlayers.Add(player);

        if (!isGameActive)
        {
            player.SetMovementLocked(true);
            player.SetAttackLocked(true);
        }

        RefreshHUD();
    }

    public void OnPlayerDied(PlayerController deadPlayer)
    {
        if (!alivePlayers.Contains(deadPlayer)) return;

        alivePlayers.Remove(deadPlayer);

        // 사망 직후 생존자 수로 순위 즉시 기록 (alivePlayers.Count + 1)
        // 예: 8명 중 6번째 사망 → 제거 후 생존 2명 → 순위 3위
        int rank = alivePlayers.Count + 1;
        if (deadPlayer.networkSync != null)
        {
            ulong clientId = deadPlayer.networkSync.OwnerClientId;
            _playerFinalRanks[clientId] = rank;
            // 사망 시각 기록 — FinishGame에서 개인별 survived_time 계산에 사용
            _playerDeathTimes[clientId] = GetSyncedTime();
        }

        if (deadPlayer.IsLocalPlayer)
            _localPlayerFinalRank = rank;

        RefreshHUD();
        CheckWinCondition();
    }

    /// <summary>
    /// 부활한 플레이어를 alivePlayers에 재등록합니다.
    /// PlayerNetworkSync.RequestReviveServerRpc()에서 호출됩니다.
    /// </summary>
    public void OnPlayerRevived(PlayerController revivedPlayer)
    {
        if (revivedPlayer == null) return;

        if (!alivePlayers.Contains(revivedPlayer))
            alivePlayers.Add(revivedPlayer);

        // [버그 수정] 부활 시 _playerFinalRanks·_playerDeathTimes에서 이전 사망 기록 제거.
        // OnPlayerDied()에서 사망 시점에 순위와 사망 시각을 즉시 기록하는데,
        // 삭제하지 않으면 부활 후 최종 1등을 해도 FinishGame()이
        // _playerFinalRanks의 이전 사망 순위를 그대로 사용해 Supabase에 잘못된 순위가 저장됨.
        // _playerDeathTimes도 제거해야 FinishGame()에서 survived_time이 올바르게 계산됨
        // (부활자가 최종 생존하면 totalGameTime 폴백 경로를 타야 함).
        if (revivedPlayer.networkSync != null)
        {
            ulong clientId = revivedPlayer.networkSync.OwnerClientId;
            _playerFinalRanks.Remove(clientId);
            _playerDeathTimes.Remove(clientId);
        }

        RefreshHUD();
        Debug.Log($"[InGameManager] {revivedPlayer.myData?.playerName} 부활 → 생존자 {alivePlayers.Count}명");
    }

    /// <summary>
    /// 부활권이 실제로 사용될 때 서버에서 호출됩니다.
    /// 매치 전체 공유 카운터(MatchReviveUsedCount)를 1 증가시킵니다.
    /// PlayerNetworkSync.RequestReviveServerRpc()에서 호출됩니다.
    /// </summary>
    public void OnReviveTicketUsed()
    {
        MatchReviveUsedCount++;
        Debug.Log($"[InGameManager] 매치 부활권 사용: {MatchReviveUsedCount}/{MaxMatchReviveCount}");
    }

    public void OnPlayerDisconnected(PlayerController disconnectedPlayer)
    {
        if (disconnectedPlayer == null || !alivePlayers.Contains(disconnectedPlayer)) return;

        Debug.Log($"[InGameManager] 플레이어 연결 끊김: {disconnectedPlayer.myData?.playerName}");
        alivePlayers.Remove(disconnectedPlayer);

        if (disconnectedPlayer.gameObject != null)
            Destroy(disconnectedPlayer.gameObject);

        RefreshHUD();
        CheckWinCondition();
    }

    private void CleanUpDisconnectedPlayers()
    {
        int removed = alivePlayers.RemoveAll(p => p == null || !p.gameObject.activeInHierarchy);
        if (removed > 0)
        {
            Debug.Log($"[InGameManager] 비정상 이탈 {removed}명 정리.");
            RefreshHUD();
            CheckWinCondition();
        }
    }

    private void CheckWinCondition()
    {
        if (gameEnded || !isGameActive) return;

        if (alivePlayers.Count <= 1)
        {
            gameEnded = true;
            PlayerController winner = alivePlayers.Count == 1 ? alivePlayers[0] : null;
            StartCoroutine(FinishGame(winner));
        }
    }

    // ════════════════════════════════════════════════════════════
    //  게임 시작 시퀀스 (서버 전용)
    // ════════════════════════════════════════════════════════════

    private IEnumerator GameStartSequence()
    {
        // 접속 대기 — 매 1초마다 메시지 갱신 (RPC 과부하 방지)
        float waitTime = 0f;
        while (alivePlayers.Count < minPlayers && waitTime < 15f)
        {
            BroadcastOrShowMessage($"다른 생존자 접속 대기 중... ({alivePlayers.Count}/{maxPlayers})");
            yield return new WaitForSeconds(1f);
            waitTime += 1f;
        }

        float extraWait = 0f;
        while (alivePlayers.Count < maxPlayers && extraWait < 3f)
        {
            BroadcastOrShowMessage($"게임 준비 중... ({alivePlayers.Count}/{maxPlayers})");
            yield return new WaitForSeconds(1f);
            extraWait += 1f;
        }

        for (int i = 5; i > 0; i--)
        {
            BroadcastOrShowMessage($"게임 시작 {i}초 전!");
            yield return new WaitForSeconds(1f);
        }
        BroadcastOrShowMessage("START!");

        // 서버(= listen-server 호스트 포함)의 플레이어 잠금 해제
        foreach (var p in alivePlayers)
        {
            if (p != null && !p.IsDead)
            {
                p.SetMovementLocked(false);
                p.SetAttackLocked(false);
            }
        }

        isGameActive     = true;
        gameStartTime    = GetSyncedTime();
        ElapsedGameTime  = 0f;
        MatchReviveUsedCount = 0;

        // ── 서버에서 부활권 NetworkVariable 초기화 ──────────────────
        foreach (var player in alivePlayers)
        {
            if (player?.networkSync != null)
                player.networkSync.ResetReviveStateForNewMatch();
        }

        // ── 모든 클라이언트에 서버 기준 시작 시간 전달 ──────────────
        if (NetworkSpawnManager.Instance != null)
            NetworkSpawnManager.Instance.NotifyGameStartedClientRpc(gameStartTime);

        yield return new WaitForSeconds(1f);

        if (NetworkSpawnManager.Instance != null)
            NetworkSpawnManager.Instance.HideCountdownClientRpc();
        else
            HideStatusMessage();

        if (timeLimitSeconds > 0f)
            StartCoroutine(TimeLimitRoutine());
    }

    /// <summary>
    /// 서버의 NetworkSpawnManager.NotifyGameStartedClientRpc에서 호출됩니다.
    /// 순수 클라이언트가 서버와 게임 시작 상태를 동기화합니다.
    /// </summary>
    public void ClientReceiveGameStart(float serverStartTime)
    {
        // listen-server 호스트는 GameStartSequence에서 이미 처리했으므로 건너뜀
        var netMgr = Unity.Netcode.NetworkManager.Singleton;
        if (netMgr != null && netMgr.IsServer) return;

        isGameActive     = true;
        gameStartTime    = serverStartTime;
        ElapsedGameTime  = 0f;
        MatchReviveUsedCount = 0;

        // 로컬 소유 플레이어 잠금 해제
        foreach (var p in alivePlayers)
        {
            if (p != null && !p.IsDead)
            {
                p.SetMovementLocked(false);
                p.SetAttackLocked(false);
            }
        }
    }

    /// <summary>멀티: NetworkSpawnManager ClientRpc, 오프라인 폴백: 로컬 HUD 직접 출력</summary>
    private void BroadcastOrShowMessage(string message)
    {
        if (NetworkSpawnManager.Instance != null)
            NetworkSpawnManager.Instance.ShowCountdownClientRpc(message);
        else
            ShowStatusMessage(message);
    }

    private void ShowStatusMessage(string message)
    {
        if (InGameHUD.Instance == null) return;
        InGameHUD.Instance.ShowGameEndBanner(message);
    }

    private void HideStatusMessage()
    {
        if (InGameHUD.Instance == null) return;
        if (InGameHUD.Instance.endBannerPanel != null)
            InGameHUD.Instance.endBannerPanel.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  게임 종료
    // ════════════════════════════════════════════════════════════

    private IEnumerator TimeLimitRoutine()
    {
        yield return new WaitForSeconds(timeLimitSeconds);
        if (!gameEnded)
        {
            gameEnded = true;
            StartCoroutine(FinishGame(GetHighestHpPlayer()));
        }
    }

    private IEnumerator FinishGame(PlayerController winner)
    {
        float gameEndTime   = GetSyncedTime();
        float totalGameTime = gameEndTime - gameStartTime;

        // HUD 배너 표시 (listen-server 환경에서 호스트 화면용; 데디케이티드 서버는 HUD 없음)
        if (InGameHUD.Instance != null)
        {
            bool hostWins = winner != null && winner.IsLocalPlayer;
            InGameHUD.Instance.ShowGameEndBanner(hostWins ? "최후의 1인! 승리!" : "탈락");
        }

        yield return new WaitForSeconds(3f);

        // ── 각 클라이언트에게 본인의 결과 전달 ──────────────────────
        // • 데디케이티드 서버: IsOwner 플레이어 없음 → 서버에서 계산 불가
        //   → ClientRpc로 각 Owner 클라이언트에게 전달, 수신 측에서 GameManager 저장 + Supabase 저장
        // • listen-server: 호스트도 ClientRpc 수신 → 동일 경로로 처리
        foreach (var p in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
        {
            if (p == null || p.networkSync == null) continue;

            bool  pIsWinner = winner != null && p == winner;
            ulong clientId  = p.networkSync.OwnerClientId;

            int pRank = pIsWinner ? 1
                : _playerFinalRanks.TryGetValue(clientId, out int r) ? r
                : alivePlayers.Count + 1;

            // [Fix] 개인별 survived_time 계산.
            // 이전: 모든 플레이어에게 totalGameTime(게임 전체 경과)을 동일하게 전달
            //       → 1분 만에 죽은 플레이어도 5분짜리 게임이면 survived_time = 5분으로 저장
            // 수정: 생존자(winner)는 totalGameTime, 사망자는 사망 시각까지의 경과 사용
            float pSurvived;
            if (pIsWinner || !_playerDeathTimes.TryGetValue(clientId, out float deathTime))
                pSurvived = totalGameTime;
            else
                pSurvived = deathTime - gameStartTime;

            var rpcParams = new Unity.Netcode.ClientRpcParams
            {
                Send = new Unity.Netcode.ClientRpcSendParams
                    { TargetClientIds = new[] { clientId } }
            };
            // [Fix] p.killCount는 클라이언트 로컬 필드로 서버에서 HandleKillCountChanged 콜백이
            // 호출되지 않아 항상 0. 서버에서 직접 쓰이는 NetworkKillCount.Value를 사용해야 함.
            int pKills = p.networkSync.NetworkKillCount.Value;
            p.networkSync.NotifyMatchResultClientRpc(pIsWinner, pRank, pKills, pSurvived, rpcParams);
        }

        // [FIX] SaveMatchResult 완료 전 씬 전환으로 DB 저장 누락 버그.
        // NotifyMatchResultClientRpc 수신 측에서 _ = SaveMatchResultAsync()를 실행하지만
        // 서버가 즉시 LoadScene을 호출하면 PlayerNetworkSync(destroyWithScene:true)가
        // Destroy되면서 진행 중인 Task가 고아가 되어 DB 저장이 완료되지 않음.
        // → Supabase 네트워크 왕복 충분히 대기 후 씬 전환 (2초 추가).
        yield return new WaitForSeconds(2f);

        // ── NGO SceneManager로 전체 씬 전환 ────────────────────────
        var netMgr = Unity.Netcode.NetworkManager.Singleton;
        if (netMgr != null && netMgr.IsServer)
            netMgr.SceneManager.LoadScene(GameManager.SceneResult, LoadSceneMode.Single);
    }

    private PlayerController GetHighestHpPlayer()
    {
        // [Fix] p.myData.currentHp 대신 NetworkHp.Value를 기준으로 비교.
        // myData.currentHp는 클라이언트 로컬 캐시이며, _serverData와 별개 객체일 수 있음.
        // NetworkHp.Value는 서버가 직접 갱신하는 단일 진실 공급원으로 더 정확함.
        PlayerController best = null; float maxHp = -1f;
        foreach (var p in alivePlayers)
        {
            if (p.networkSync == null) continue;
            float hp = p.networkSync.NetworkHp.Value;
            if (hp > maxHp) { maxHp = hp; best = p; }
        }
        return best;
    }

    private void RefreshHUD()
    {
        if (InGameHUD.Instance != null)
            InGameHUD.Instance.UpdateSurvivorCount(alivePlayers.Count, maxPlayers);
    }
}
