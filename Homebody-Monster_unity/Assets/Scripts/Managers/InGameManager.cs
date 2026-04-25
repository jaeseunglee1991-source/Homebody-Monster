using UnityEngine;
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
    private bool  gameEnded    = false;
    private float gameStartTime;
    private float cleanupTimer = 0f;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    private void Start()
    {
        StartCoroutine(GameStartSequence());
    }

    private void Update()
    {
        if (isGameActive && !gameEnded)
        {
            ElapsedGameTime = Time.time - gameStartTime;

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

        foreach (var p in alivePlayers)
        {
            if (p != null && !p.IsDead)
            {
                p.SetMovementLocked(false);
                p.SetAttackLocked(false);
            }
        }

        isGameActive     = true;
        gameStartTime    = Time.time;
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
        PlayerController localPlayer = GetLocalPlayer();
        bool isWinner  = winner != null && winner.IsLocalPlayer;
        int  rank      = CalculateLocalRank(localPlayer);
        int  kills     = localPlayer != null ? localPlayer.killCount : 0;
        float survived = Time.time - gameStartTime;

        if (GameManager.Instance != null)
        {
            GameManager.Instance.lastMatchResult = new MatchResult
            {
                isWinner = isWinner, rank = rank, killCount = kills, survivedTime = survived
            };
        }

        if (InGameHUD.Instance != null)
            InGameHUD.Instance.ShowGameEndBanner(isWinner ? "최후의 1인! 승리!" : "탈락");

        yield return new WaitForSeconds(3f);

        if (SupabaseManager.Instance != null)
            _ = SaveResultAsync(isWinner, rank, kills, survived);

        GameManager.Instance?.LoadScene("ResultScene");
    }

    private async Task SaveResultAsync(bool win, int rank, int kills, float time)
    {
        try { await SupabaseManager.Instance.SaveMatchResult(win, rank, kills, time); }
        catch (System.Exception e) { Debug.LogError($"[InGameManager] 결과 저장 실패: {e.Message}"); }
    }

    private PlayerController GetLocalPlayer()
    {
        foreach (var p in FindObjectsOfType<PlayerController>())
            if (p.IsLocalPlayer) return p;
        return null;
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

    private int CalculateLocalRank(PlayerController localPlayer)
        => alivePlayers.Contains(localPlayer) ? 1 : alivePlayers.Count + 1;

    private void RefreshHUD()
    {
        if (InGameHUD.Instance != null)
            InGameHUD.Instance.UpdateSurvivorCount(alivePlayers.Count, maxPlayers);
    }
}
