using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 리더보드 및 전적 조회 관리자.
/// - FetchLeaderboardAsync(): 60초 캐시, Supabase leaderboard_kills 뷰 조회
/// - SubmitMatchResult(): 인게임 종료 시 match_history INSERT
/// - ShowLeaderboard() / ShowMyHistory(): 팝업 UI 갱신
/// </summary>
public class LeaderboardManager : MonoBehaviour
{
    public static LeaderboardManager Instance { get; private set; }

    // ── Inspector ───────────────────────────────────────────────
    [Header("리더보드 팝업")]
    public GameObject leaderboardPanel;
    public Transform  leaderboardContent;          // ScrollView Content
    public GameObject leaderboardRowPrefab;         // TextMeshProUGUI 1줄 prefab
    public Button     closeLeaderboardButton;

    [Header("내 전적 팝업")]
    public GameObject historyPanel;
    public Transform  historyContent;
    public GameObject historyRowPrefab;
    public Button     closeHistoryButton;

    [Header("로딩 표시")]
    public GameObject loadingIndicator;

    // ── 캐시 ────────────────────────────────────────────────────
    private List<LeaderboardRecord> _cachedLeaderboard;
    private float                   _cacheTime = -999f;
    private const float             CacheTtl   = 60f;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        if (closeLeaderboardButton != null)
            closeLeaderboardButton.onClick.AddListener(() => leaderboardPanel?.SetActive(false));
        if (closeHistoryButton != null)
            closeHistoryButton.onClick.AddListener(() => historyPanel?.SetActive(false));
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 인게임 종료 시 NotifyMatchResultClientRpc에서 호출됩니다.
    ///
    /// ⚠️ DB 저장은 PlayerNetworkSync.SaveMatchResultAsync → SupabaseManager.SaveMatchResult(RPC)
    ///    경로에서 이미 처리됩니다. 이 메서드에서 InsertMatchHistory를 추가로 호출하면
    ///    이중 저장(Duplicate result for room 오류)이 발생하므로 DB 저장을 수행하지 않습니다.
    ///    리더보드 캐시만 무효화하여 다음 ShowLeaderboard() 호출 시 최신 데이터를 가져옵니다.
    /// </summary>
    public void SubmitMatchResult(bool isWinner, int rank, int kills, float survivalSecs)
    {
        // 리더보드 캐시 무효화 (다음 조회 시 DB에서 최신 데이터 로드)
        _cacheTime = -999f;
        Debug.Log($"[LeaderboardManager] 매치 종료 캐시 무효화: rank={rank}, kills={kills}");
    }

    /// <summary>
    /// 리더보드 팝업을 열고 최신 데이터를 표시합니다.
    /// </summary>
    public async void ShowLeaderboard()
    {
        leaderboardPanel?.SetActive(true);
        SetLoading(true);

        var records = await FetchLeaderboardAsync();
        SetLoading(false);
        RenderLeaderboard(records);
    }

    /// <summary>
    /// 내 전적 팝업을 열고 최신 데이터를 표시합니다.
    /// </summary>
    public async void ShowMyHistory()
    {
        historyPanel?.SetActive(true);
        SetLoading(true);

        string userId = SupabaseManager.Instance?.CurrentUserId;
        if (string.IsNullOrEmpty(userId))
        {
            SetLoading(false);
            AppendRow(historyContent, historyRowPrefab, "로그인이 필요합니다.", Color.red);
            return;
        }

        try
        {
            var records = await SupabaseManager.Instance.FetchMyMatchHistory(userId);
            SetLoading(false);
            RenderHistory(records);
        }
        catch (Exception e)
        {
            SetLoading(false);
            Debug.LogWarning($"[LeaderboardManager] 전적 조회 실패: {e.Message}");
            AppendRow(historyContent, historyRowPrefab, "전적을 불러오지 못했습니다.", Color.red);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  내부 메서드
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 60초 캐시를 활용한 리더보드 비동기 조회.
    ///
    /// [버그 수정] 캐시 타임스탬프 누락으로 조회 실패 후 캐시 무효화.
    /// 기존: 조회 성공 시에만 _cacheTime을 갱신하고,
    ///       조회 실패 시 이전값(-999f)이 유지돼 다음 호출에서 캐시 조건을 항상 위반.
    ///       → 한 번 실패하면 매번 DB 재조회 (네트워크 낭비 + UI 오류)
    /// 수정: 조회 시도 직후 (_cachedLeaderboard 결과와 무관하게) _cacheTime을 갱신.
    ///       성공/실패 모두 60초 캐시 유지 (실패 시 이전값/빈 리스트 캐시).
    /// </summary>
    public async Task<List<LeaderboardRecord>> FetchLeaderboardAsync()
    {
        if (_cachedLeaderboard != null && Time.realtimeSinceStartup - _cacheTime < CacheTtl)
            return _cachedLeaderboard;

        if (SupabaseManager.Instance == null) return new List<LeaderboardRecord>();

        // [버그 수정 X-G] 실패와 빈 결과 구분.
        // 실패 시 _cachedLeaderboard를 갱신하지 않고 캐시 타임스탬프도 미갱신
        // → 다음 호출에서 즉시 재시도 가능. 정상 빈 결과만 캐시.
        try
        {
            var fresh = await SupabaseManager.Instance.FetchLeaderboard();
            _cachedLeaderboard = fresh ?? new List<LeaderboardRecord>();
            _cacheTime = Time.realtimeSinceStartup;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LeaderboardManager] 리더보드 조회 실패: {e.Message}");
            // 실패 시: 캐시/타임스탬프 모두 유지 (재시도 허용)
            return _cachedLeaderboard ?? new List<LeaderboardRecord>();
        }

        return _cachedLeaderboard;
    }

    private void RenderLeaderboard(List<LeaderboardRecord> records)
    {
        ClearContent(leaderboardContent);
        if (records == null || records.Count == 0)
        {
            AppendRow(leaderboardContent, leaderboardRowPrefab, "데이터가 없습니다.", Color.gray);
            return;
        }

        string myId = SupabaseManager.Instance?.CurrentUserId;

        for (int i = 0; i < records.Count; i++)
        {
            var rec   = records[i];
            string line = $"{i + 1,3}위  {rec.Nickname,-12}  킬 {rec.TotalKills,4}  승 {rec.Wins,3}";
            bool   isMe = !string.IsNullOrEmpty(myId) && rec.UserId == myId;
            AppendRow(leaderboardContent, leaderboardRowPrefab, line,
                      isMe ? new Color(1f, 0.85f, 0f) : Color.white);  // 내 항목 금색 강조
        }
    }

    private void RenderHistory(List<MatchHistoryRecord> records)
    {
        ClearContent(historyContent);
        if (records == null || records.Count == 0)
        {
            AppendRow(historyContent, historyRowPrefab, "전적이 없습니다.", Color.gray);
            return;
        }

        foreach (var rec in records)
        {
            string result = rec.IsWin ? "<color=#FFD700>승리</color>" : "패배";
            string line   = $"{rec.PlayedAt:MM/dd HH:mm}  {result}  {rec.Rank}위  킬 {rec.Kills}  {rec.SurvivalSeconds / 60:F1}분";
            AppendRow(historyContent, historyRowPrefab, line, Color.white);
        }
    }

    private void ClearContent(Transform content)
    {
        if (content == null) return;
        // [NEW-09] 런타임 DestroyImmediate는 Unity 공식 비권장 (콜백 중 호출 시 크래시 위험).
        // Destroy() + Canvas.ForceUpdateCanvases()로 즉시 레이아웃 갱신을 보장.
        for (int i = content.childCount - 1; i >= 0; i--)
            Destroy(content.GetChild(i).gameObject);
        Canvas.ForceUpdateCanvases();
    }

    private void AppendRow(Transform content, GameObject prefab, string text, Color color)
    {
        if (content == null || prefab == null) return;
        var go  = Instantiate(prefab, content);
        var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
        if (tmp != null) { tmp.text = text; tmp.color = color; }
    }

    private void SetLoading(bool on)
    {
        loadingIndicator?.SetActive(on);
    }
}
