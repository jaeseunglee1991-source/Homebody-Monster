using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 인게임 전체를 관장하는 매니저.
/// 생존자 추적 → 최후 1인 판정 → 결과씬 전환을 처리합니다.
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
        gameStartTime = Time.time;
        if (timeLimitSeconds > 0f)
            StartCoroutine(TimeLimitRoutine());
    }

    private void Update()
    {
        if (!gameEnded)
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
        if (gameEnded) return;

        if (alivePlayers.Count <= 1)
        {
            gameEnded = true;
            PlayerController winner = alivePlayers.Count == 1 ? alivePlayers[0] : null;
            StartCoroutine(FinishGame(winner));
        }
    }

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
        // 로컬 플레이어 기준 결과 계산
        PlayerController localPlayer = GetLocalPlayer();
        bool isWinner = (winner != null && winner.IsLocalPlayer);
        int rank = CalculateLocalRank(localPlayer);
        int kills = localPlayer != null ? localPlayer.killCount : 0;
        float survived = Time.time - gameStartTime;

        // GameManager에 결과 저장
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

        // Supabase에 결과 저장 (전역 매니저 호출)
        if (SupabaseManager.Instance != null)
        {
            await SaveResultAsync(isWinner, rank, kills, survived);
        }

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
