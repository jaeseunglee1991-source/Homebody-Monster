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
    private readonly Dictionary<ulong, int> _playerFinalRanks = new Dictionary<ulong, int>();
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
            _playerFinalRanks[deadPlayer.networkSync.OwnerClientId] = rank;

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
    //  게임 시작 시퀀스
    // ════════════════════════════════════════════════════════════

    private IEnumerator GameStartSequence()
    {
        float waitTime = 0f;
        while (alivePlayers.Count < minPlayers && waitTime < 15f)
        {
            waitTime += Time.deltaTime;
            ShowStatusMessage($"다른 생존자 접속 대기 중... ({alivePlayers.Count}/{maxPlayers})");
            yield return null;
        }

        float extraWait = 0f;
        while (alivePlayers.Count < maxPlayers && extraWait < 3f)
        {
            extraWait += Time.deltaTime;
            ShowStatusMessage($"게임 준비 중... ({alivePlayers.Count}/{maxPlayers})");
            yield return null;
        }

        for (int i = 5; i > 0; i--)
        {
            ShowStatusMessage($"게임 시작 {i}초 전!");
            yield return new WaitForSeconds(1f);
        }
        ShowStatusMessage("START!");

        // 스폰 위치 배정은 NetworkSpawnManager가 서버 권한으로 단독 처리.
        // 여기서 transform.position을 덮어쓰면 NGO 권한 모델과 충돌하고
        // 클라이언트 측 실행 시 인원 수 불일치로 (0,0,0) 워프가 발생할 수 있음.

        foreach (var p in alivePlayers)
        {
            if (p != null && !p.IsDead)
            {
                p.SetMovementLocked(false);
                p.SetAttackLocked(false);
            }
        }

        isGameActive     = true;
        gameStartTime    = GetSyncedTime(); // 모든 클라이언트가 동일한 네트워크 기준 시간 사용
        ElapsedGameTime  = 0f;
        MatchReviveUsedCount = 0; // 게임 시작 시 카운터 초기화

        // ── 모든 플레이어의 부활권 상태 초기화 ──────────────────────
        foreach (var player in alivePlayers)
        {
            if (player?.networkSync != null)
            {
                player.networkSync.ResetReviveStateForNewMatch();
            }
        }

        yield return new WaitForSeconds(1f);
        HideStatusMessage();

        if (timeLimitSeconds > 0f)
            StartCoroutine(TimeLimitRoutine());
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
        float survived = GetSyncedTime() - gameStartTime;

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
            bool pIsWinner = winner != null && p == winner;
            int  pRank = pIsWinner ? 1
                : _playerFinalRanks.TryGetValue(p.networkSync.OwnerClientId, out int r) ? r
                : alivePlayers.Count + 1; // 폴백: 아직 살아있다면 1위 근처
            var rpcParams = new Unity.Netcode.ClientRpcParams
            {
                Send = new Unity.Netcode.ClientRpcSendParams
                    { TargetClientIds = new[] { p.networkSync.OwnerClientId } }
            };
            p.networkSync.NotifyMatchResultClientRpc(pIsWinner, pRank, p.killCount, survived, rpcParams);
        }

        // ── NGO SceneManager로 전체 씬 전환 ────────────────────────
        // GameManager.LoadScene(로컬) 대신 NGO SceneManager 사용:
        // 서버가 호출하면 연결된 모든 클라이언트가 동시에 씬을 전환합니다.
        var netMgr = Unity.Netcode.NetworkManager.Singleton;
        if (netMgr != null && netMgr.IsServer)
            netMgr.SceneManager.LoadScene("ResultScene", LoadSceneMode.Single);
    }

    private PlayerController GetHighestHpPlayer()
    {
        PlayerController best = null; float maxHp = -1f;
        foreach (var p in alivePlayers)
        {
            if (p.myData != null && p.myData.currentHp > maxHp)
            { maxHp = p.myData.currentHp; best = p; }
        }
        return best;
    }

    private void RefreshHUD()
    {
        if (InGameHUD.Instance != null)
            InGameHUD.Instance.UpdateSurvivorCount(alivePlayers.Count, maxPlayers);
    }
}
