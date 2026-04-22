using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 인게임 전체를 관장하는 매니저.
/// 생존자 추적 → 로딩 동기화 및 5초 카운트다운 → 최후 1인 판정 → 결과씬 전환을 처리합니다.
/// </summary>
public class InGameManager : MonoBehaviour
{
    public static InGameManager Instance { get; private set; }

    [Header("Game Settings")]
    public int minPlayers = 2;
    public int maxPlayers = 8;

    [Header("Spawn Points")]
    public Transform[] spawnPoints;

    [Header("Game Timer")]
    [Tooltip("0이면 무제한 (마지막 1명 남을 때까지), 0보다 크면 시간 제한 (최고 HP 플레이어 승리)")]
    public float timeLimitSeconds = 0f;

    /// <summary>카운트다운이 끝나고 실제 전투가 시작된 상태인지 여부</summary>
    public bool isGameActive { get; private set; } = false;

    private readonly List<PlayerController> alivePlayers = new List<PlayerController>();
    private bool gameEnded = false;
    private float gameStartTime;
    private float elapsedTime;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    private void Start()
    {
        // 씬이 열리면 타이머를 즉시 시작하지 않고 로딩 동기화 시퀀스로 진입
        StartCoroutine(GameStartSequence());
    }

    private void Update()
    {
        // 게임이 공식 시작된 이후에만 타이머를 업데이트
        if (isGameActive && !gameEnded)
        {
            elapsedTime = Time.time - gameStartTime;
            if (InGameHUD.Instance != null)
                InGameHUD.Instance.UpdateTimer(elapsedTime);
        }
    }

    public void RegisterPlayer(PlayerController player)
    {
        if (player == null || alivePlayers.Contains(player)) return;
        alivePlayers.Add(player);

        // 게임이 아직 시작 전이면 새로 접속한 플레이어 조작 즉시 잠금
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

    private void CheckWinCondition()
    {
        // 게임이 시작 전(대기 중)이거나 이미 종료됐으면 승리 판정 무시
        // → 카운트다운 중 1명만 남아 있어도 즉시 승리 처리되는 버그 방지
        if (gameEnded || !isGameActive) return;

        if (alivePlayers.Count <= 1)
        {
            gameEnded = true;
            PlayerController winner = alivePlayers.Count == 1 ? alivePlayers[0] : null;
            StartCoroutine(FinishGame(winner));
        }
    }

    // ════════════════════════════════════════════════════════════
    //  로딩 동기화 및 카운트다운
    // ════════════════════════════════════════════════════════════

    private IEnumerator GameStartSequence()
    {
        // 1. 최소 인원 대기 (최대 15초 타임아웃)
        float waitTime = 0f;
        while (alivePlayers.Count < minPlayers && waitTime < 15f)
        {
            waitTime += Time.deltaTime;
            ShowStatusMessage($"다른 생존자 접속 대기 중... ({alivePlayers.Count}/{maxPlayers})");
            yield return null;
        }

        // 2. 최대 인원 대기 유예 시간 (늦게 로딩되는 유저를 위해 3초 추가 대기)
        float extraWait = 0f;
        while (alivePlayers.Count < maxPlayers && extraWait < 3f)
        {
            extraWait += Time.deltaTime;
            ShowStatusMessage($"게임 준비 중... ({alivePlayers.Count}/{maxPlayers})");
            yield return null;
        }

        // 3. 5초 카운트다운 (기기 성능/네트워크 차이에 상관없이 동시 시작)
        for (int i = 5; i > 0; i--)
        {
            ShowStatusMessage($"게임 시작 {i}초 전!");
            yield return new WaitForSeconds(1f);
        }
        ShowStatusMessage("START!");

        // 4. 모든 플레이어 조작 잠금 동시 해제
        foreach (var p in alivePlayers)
        {
            if (p != null && !p.IsDead)
            {
                p.SetMovementLocked(false);
                p.SetAttackLocked(false);
            }
        }

        isGameActive = true;
        gameStartTime = Time.time;

        // 1초 후 상태 메시지 숨김
        yield return new WaitForSeconds(1f);
        HideStatusMessage();

        // 제한 시간 시스템 가동 (timeLimitSeconds > 0 일 때만)
        if (timeLimitSeconds > 0f)
            StartCoroutine(TimeLimitRoutine());
    }

    // ════════════════════════════════════════════════════════════
    //  HUD 헬퍼 — 승리/패배 배너와 분리된 상태 메시지
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 카운트다운·대기 중 상태를 표시합니다.
    /// ShowGameEndBanner(승리/패배)와 충돌하지 않도록 별도 처리합니다.
    /// </summary>
    private void ShowStatusMessage(string message)
    {
        if (InGameHUD.Instance == null) return;
        // endBannerPanel을 공유하되, 게임 종료 배너와 타이밍이 겹치지 않으므로 안전
        InGameHUD.Instance.ShowGameEndBanner(message);
    }

    private void HideStatusMessage()
    {
        if (InGameHUD.Instance == null) return;
        if (InGameHUD.Instance.endBannerPanel != null)
            InGameHUD.Instance.endBannerPanel.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  기존 게임 진행 로직 (변경 없음)
    // ════════════════════════════════════════════════════════════

    private IEnumerator TimeLimitRoutine()
    {
        yield return new WaitForSeconds(timeLimitSeconds);
        if (!gameEnded)
        {
            gameEnded = true;
            PlayerController winner = GetHighestHpPlayer();
            StartCoroutine(FinishGame(winner));
        }
    }

    private IEnumerator FinishGame(PlayerController winner)
    {
        PlayerController localPlayer = GetLocalPlayer();
        bool isWinner = (winner != null && winner.IsLocalPlayer);
        int rank = CalculateLocalRank(localPlayer);
        int kills = localPlayer != null ? localPlayer.killCount : 0;
        float survived = Time.time - gameStartTime;

        if (GameManager.Instance != null)
        {
            GameManager.Instance.lastMatchResult = new MatchResult
            {
                isWinner = isWinner,
                rank = rank,
                killCount = kills,
                survivedTime = survived
            };
        }

        if (InGameHUD.Instance != null)
            InGameHUD.Instance.ShowGameEndBanner(isWinner ? "최후의 1인! 승리!" : "탈락");

        yield return new WaitForSeconds(3f);

        if (SupabaseManager.Instance != null)
            await SaveResultAsync(isWinner, rank, kills, survived);

        GameManager.Instance?.LoadScene("ResultScene");
    }

    private async Task SaveResultAsync(bool win, int rank, int kills, float time)
    {
        try
        {
            await SupabaseManager.Instance.SaveMatchResult(win, rank, kills, time);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[InGameManager] 결과 저장 실패: {e.Message}");
        }
    }

    private PlayerController GetLocalPlayer()
    {
        foreach (var p in FindObjectsOfType<PlayerController>())
            if (p.IsLocalPlayer) return p;
        return null;
    }

    private PlayerController GetHighestHpPlayer()
    {
        PlayerController best = null;
        float maxHp = -1f;
        foreach (var p in alivePlayers)
        {
            if (p.myData != null && p.myData.currentHp > maxHp)
            {
                maxHp = p.myData.currentHp;
                best = p;
            }
        }
        return best;
    }

    private int CalculateLocalRank(PlayerController localPlayer)
    {
        return alivePlayers.Contains(localPlayer) ? 1 : alivePlayers.Count + 1;
    }

    private void RefreshHUD()
    {
        if (InGameHUD.Instance != null)
            InGameHUD.Instance.UpdateSurvivorCount(alivePlayers.Count, maxPlayers);
    }
}
