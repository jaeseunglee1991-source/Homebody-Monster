using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Newtonsoft.Json;

// ════════════════════════════════════════════════════════════════
//  DB 응답 역직렬화 DTO
// ════════════════════════════════════════════════════════════════

/// <summary>claim_daily_reward RPC 응답 DTO.</summary>
[Serializable]
public class DailyRewardResult
{
    [JsonProperty("already_claimed")] public bool AlreadyClaimed { get; set; }
    [JsonProperty("streak")]          public int  Streak         { get; set; }
    [JsonProperty("reward_pizza")]    public int  RewardPizza    { get; set; }
}

/// <summary>
/// fetch_today_login_status RPC 응답 DTO.
/// Streak = 0 은 RPC 실패 신호 (DB 반환 최솟값은 항상 1).
/// </summary>
[Serializable]
public class DailyLoginStatus
{
    [JsonProperty("streak")]  public int  Streak  { get; set; }
    [JsonProperty("claimed")] public bool Claimed { get; set; }
}

// ════════════════════════════════════════════════════════════════
//  DailyRewardSystem
// ════════════════════════════════════════════════════════════════

/// <summary>
/// 매일 첫 로그인 시 피자 보상을 지급하는 출석 체크 시스템.
///
/// ── 수정된 버그 전체 이력 ───────────────────────────────────────
///  #1  - DaySlotUI [Serializable] + MonoBehaviour 중복 제거
///  #2  - OnClaimClicked SupabaseManager.Instance null + IsInitialized 체크 누락
///  #3  - async/await·코루틴 복귀 후 this == null 방어 누락
///  #4  - PlayerPrefs 날짜 기준 UTC 통일 (DB CURRENT_DATE UTC와 일치)
///  #5  - RPC Content 파싱: 1차(unquote)→2차(직접) 폴백 + ParseRpcJson 공통 헬퍼
///  #6  - DailyLoginRecord.LoginDate DateTime → string (DATE 컬럼 파싱 실패 방지)
///  #7  - ShowPanelAsync 중복 호출 가드 + try-finally 플래그 누수 방지
///  #8  - FetchCurrentStreak(int) → FetchTodayStatus(streak+claimed) 교체
///  #9  - FetchTodayStatus 실패 시 defaultStatus.Streak=0 으로 캐시 덮어쓰기 방지
///  #10 - SQL 레이스 컨디션 완전 해결 (DB 마이그레이션으로 적용)
///        이전: ON CONFLICT DO UPDATE → 동시 수령 시 피자 이중 지급 가능
///        최종: INSERT DO NOTHING + ROW_COUNT → UNIQUE 제약이 원자적 잠금 수행
///  #11 - ShowPanelAsync 진입 시 rewardText 초기화 누락
///
/// ── Inspector 연결 체크리스트 ────────────────────────────────────
///  □ rewardPanel      : 패널 루트 GameObject
///  □ daySlots[7]      : DaySlotUI 컴포넌트가 붙은 슬롯 프리팹 7개
///  □ streakText       : "🔥 3일 연속 출석 중!" TMP
///  □ rewardText       : "🍕 20 피자 획득!" TMP
///  □ statusText       : 로딩·오류 안내 TMP
///  □ claimButton      : "보상 받기" 버튼
///  □ skipButton       : "닫기" 버튼
///  □ pizzaFlyAnimator : 피자 이펙트 Animator (선택)
///  □ claimSoundClip   : 수령 효과음 AudioClip (선택)
///
/// ── LobbyUIController 연동 (한 줄 추가) ─────────────────────────
///  LobbyUIController.Start() 맨 아래:
///    DailyRewardSystem.Instance?.TryClaimOnLobbyEnter();
/// </summary>
public class DailyRewardSystem : MonoBehaviour
{
    public static DailyRewardSystem Instance { get; private set; }

    // ── Inspector ───────────────────────────────────────────────
    [Header("보상 패널")]
    public GameObject rewardPanel;

    [Header("7일 칸 (반드시 7개 / DaySlotUI 컴포넌트 필수)")]
    public DaySlotUI[] daySlots;

    [Header("텍스트")]
    public TextMeshProUGUI streakText;
    public TextMeshProUGUI rewardText;
    public TextMeshProUGUI statusText;

    [Header("버튼")]
    public Button claimButton;
    public Button skipButton;

    [Header("수령 연출 (선택)")]
    public Animator  pizzaFlyAnimator;
    public AudioClip claimSoundClip;

    // ── 보상 테이블 (인덱스 0 = 1일차) ─────────────────────────
    private static readonly int[] PizzaRewardTable = { 10, 15, 20, 25, 30, 40, 60 };

    // ── PlayerPrefs 키 ───────────────────────────────────────────
    // [Bug Fix #4] DateTime.UtcNow — DB CURRENT_DATE(Supabase 기본 UTC)와 기준 통일
    private const string PrefKeyLastClaimDate = "DailyReward_LastClaimDate"; // "yyyy-MM-dd" UTC
    private const string PrefKeyStreak        = "DailyReward_Streak";

    // ── 내부 상태 ────────────────────────────────────────────────
    private bool        _isClaiming;
    private bool        _isShowingPanel; // [Bug Fix #7] 중복 호출 가드
    private AudioSource _audioSource;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance     = this;
        _audioSource = GetComponent<AudioSource>();
    }

    private void Start()
    {
        if (rewardPanel != null) rewardPanel.SetActive(false);
        claimButton?.onClick.AddListener(OnClaimClicked);
        skipButton?.onClick.AddListener(ClosePanel);
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 로비 진입 시 자동 호출합니다.
    /// LobbyUIController.Start() 맨 아래에 추가:
    ///   DailyRewardSystem.Instance?.TryClaimOnLobbyEnter();
    /// </summary>
    public void TryClaimOnLobbyEnter()
    {
        if (AlreadyClaimedTodayLocally())
        {
            Debug.Log("[DailyReward] 오늘 이미 수령 완료 (로컬 캐시).");
            return;
        }
        _ = ShowPanelAsync();
    }

    /// <summary>출석 버튼 등에서 수동으로 패널을 엽니다.</summary>
    public void OpenPanel() => _ = ShowPanelAsync();

    public void ClosePanel()
    {
        _isShowingPanel = false; // [Bug Fix #7]
        if (rewardPanel != null) rewardPanel.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  패널 표시
    // ════════════════════════════════════════════════════════════

    private async Task ShowPanelAsync()
    {
        // [Bug Fix #3] 씬 전환으로 파괴된 경우 조기 종료
        if (this == null || rewardPanel == null) return;

        // [Bug Fix #7] 이미 DB 조회 중이면 중복 실행 차단
        if (_isShowingPanel) return;
        _isShowingPanel = true;

        try
        {
            // [Bug Fix #11] 패널 열기 시 rewardText 초기화
            // 수령 후 닫고 재열기 시 이전 "🍕 N 피자 획득!" 문구 잔류 방지
            if (rewardText != null) rewardText.text = "";

            // 로컬 캐시로 패널 먼저 그리기 (빠른 표시 → DB 응답 후 보정)
            int  localStreak  = PlayerPrefs.GetInt(PrefKeyStreak, 1);
            bool alreadyLocal = AlreadyClaimedTodayLocally();

            DrawDaySlots(localStreak, alreadyLocal);
            rewardPanel.SetActive(true);
            SetStatus("");

            // [Bug Fix #2] null + IsInitialized 이중 체크
            // [Bug Fix #8] FetchTodayStatus로 streak + claimed 함께 수신
            if (SupabaseManager.Instance != null && SupabaseManager.Instance.IsInitialized)
            {
                var status = await SupabaseManager.Instance.FetchTodayStatus();

                // [Bug Fix #3] await 복귀 후 파괴 여부 재확인
                if (this == null) return;

                // [Bug Fix #9] Streak > 0: DB 성공. Streak = 0: DB 실패 → 로컬 캐시 유지
                if (status.Streak > 0)
                {
                    localStreak = status.Streak;

                    // [Bug Fix #8] 다른 기기 수령 감지 → 로컬 캐시 동기화
                    if (status.Claimed && !alreadyLocal)
                    {
                        SaveLocalClaim(status.Streak);
                        alreadyLocal = true;
                        Debug.Log("[DailyReward] 다른 기기 수령 감지 → 로컬 캐시 동기화.");
                    }

                    DrawDaySlots(localStreak, alreadyLocal);
                }
                // Streak == 0: DB 오류 → localStreak(캐시 값) 그대로, UI 변경 없음
            }

            // 최종 UI 상태 반영
            bool claimed = AlreadyClaimedTodayLocally();
            if (claimButton != null) claimButton.interactable = !claimed;
            if (streakText  != null)
                streakText.text = claimed
                    ? $"✅ {localStreak}일 연속 출석 완료!"
                    : $"🔥 {localStreak}일 연속 출석 중!";
        }
        finally
        {
            // [Bug Fix #7] 정상 완료·예외·return 어느 경로에서도 반드시 해제
            if (this != null) _isShowingPanel = false;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  보상 수령
    // ════════════════════════════════════════════════════════════

    private async void OnClaimClicked()
    {
        if (_isClaiming) return;

        // [C-14] 로컬 캐시는 UX 힌트에 불과하므로 서버 호출을 막지 않음.
        // 다른 기기 수령/시간대 변경 등 캐시가 오래된 경우에도 서버 응답으로 권위 판정.
        if (AlreadyClaimedTodayLocally())
            SetStatus("확인 중...");

        // [Bug Fix #2] null + IsInitialized 이중 체크
        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
        {
            SetStatus("⚠ 서버에 연결되지 않았습니다. 잠시 후 다시 시도해주세요.");
            return;
        }

        _isClaiming = true;
        if (claimButton != null) claimButton.interactable = false;
        SetStatus("서버에 요청 중...");

        try
        {
            var result = await SupabaseManager.Instance.ClaimDailyReward();

            // [Bug Fix #3] await 복귀 후 씬 전환 여부 재확인
            if (this == null) return;

            if (result == null)
            {
                SetStatus("⚠ 서버 오류. 잠시 후 다시 시도해주세요.");
                if (claimButton != null) claimButton.interactable = true;
                return;
            }

            if (result.AlreadyClaimed)
            {
                SaveLocalClaim(result.Streak);
                SetStatus("오늘 이미 수령하셨습니다.");
                DrawDaySlots(result.Streak, alreadyClaimed: true);
                if (streakText  != null) streakText.text = $"✅ {result.Streak}일 연속 출석 완료!";
                if (claimButton != null) claimButton.interactable = false;
                return;
            }

            // ── 수령 성공 ──────────────────────────────────────────
            SaveLocalClaim(result.Streak);
            DrawDaySlots(result.Streak, alreadyClaimed: true);

            if (rewardText != null) rewardText.text = $"🍕 {result.RewardPizza} 피자 획득!";
            if (streakText != null) streakText.text = $"✅ {result.Streak}일 연속 출석 완료!";
            SetStatus("");

            pizzaFlyAnimator?.SetTrigger("Play");
            if (claimSoundClip != null && _audioSource != null)
                _audioSource.PlayOneShot(claimSoundClip);

            // 피자 카운트 UI 갱신
            StartCoroutine(RefreshPizzaUIAfterDelay(0.5f));

            Debug.Log($"[DailyReward] ✅ 수령 완료 — streak={result.Streak}, pizza={result.RewardPizza}");
        }
        finally
        {
            // H-1: ensure flag is always restored
            if (this != null) _isClaiming = false;
        }
    }

    private IEnumerator RefreshPizzaUIAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        // [Bug Fix #3] 코루틴 복귀 후에도 확인
        if (this == null) yield break;
        var lobby = FindFirstObjectByType<LobbyUIController>();
        lobby?.RefreshUserProfileUI();
    }

    // ════════════════════════════════════════════════════════════
    //  7일 슬롯 UI
    // ════════════════════════════════════════════════════════════

    private void DrawDaySlots(int streak, bool alreadyClaimed)
    {
        if (daySlots == null) return;

        // streak 범위 방어: 0 이하·8 이상 모두 Clamp 처리
        int todayIndex = Mathf.Clamp(streak - 1, 0, 6);

        for (int i = 0; i < daySlots.Length && i < 7; i++)
        {
            if (daySlots[i] == null) continue;

            daySlots[i].SetDay(
                dayNumber:   i + 1,
                pizza:       PizzaRewardTable[i],
                isCompleted: i < todayIndex || (i == todayIndex && alreadyClaimed),
                isToday:     i == todayIndex && !alreadyClaimed,
                isFuture:    i > todayIndex
            );
        }
    }

    // ════════════════════════════════════════════════════════════
    //  로컬 캐시 — [Bug Fix #4] UTC 기준
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// DB CURRENT_DATE(Supabase 기본 UTC)와 동일한 UTC 기준으로 오늘 수령 여부를 확인합니다.
    /// DateTime.Now(로컬 시간) 사용 시 KST(UTC+9) 환경에서 자정 전후 날짜 불일치 발생.
    /// </summary>
    private static bool AlreadyClaimedTodayLocally()
    {
        string saved = PlayerPrefs.GetString(PrefKeyLastClaimDate, "");
        return saved == DateTime.UtcNow.ToString("yyyy-MM-dd");
    }

    private static void SaveLocalClaim(int streak)
    {
        PlayerPrefs.SetString(PrefKeyLastClaimDate, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        PlayerPrefs.SetInt(PrefKeyStreak, streak);
        PlayerPrefs.Save();
    }

    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }
}

// DaySlotUI 클래스는 DaySlotUI.cs 파일로 분리되었습니다.

// ════════════════════════════════════════════════════════════════
//  ORM 모델 — daily_logins 테이블
//
//  [Bug Fix #6] login_date → string LoginDateStr
//  Supabase DATE 컬럼은 "yyyy-MM-dd" 문자열로 내려옵니다.
//  C# DateTime 자동변환의 시간대 불명확 문제를 방지합니다.
//  FetchTodayStatus RPC 도입으로 직접 쿼리에 사용되지 않지만
//  관리자 도구·디버깅 용도로 보존합니다.
// ════════════════════════════════════════════════════════════════
[Supabase.Postgrest.Attributes.Table("daily_logins")]
public class DailyLoginRecord : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [JsonProperty("id")]              public long   Id           { get; set; }

    [Supabase.Postgrest.Attributes.Column("player_id")]
    [JsonProperty("player_id")]       public string PlayerId     { get; set; }

    // [Bug Fix #6] DateTime → string (DATE 컬럼 파싱 실패 방지)
    [Supabase.Postgrest.Attributes.Column("login_date")]
    [JsonProperty("login_date")]      public string LoginDateStr { get; set; }

    [Supabase.Postgrest.Attributes.Column("streak")]
    [JsonProperty("streak")]          public int    Streak       { get; set; }

    [Supabase.Postgrest.Attributes.Column("reward_pizza")]
    [JsonProperty("reward_pizza")]    public int    RewardPizza  { get; set; }

    [Supabase.Postgrest.Attributes.Column("claimed")]
    [JsonProperty("claimed")]         public bool   Claimed      { get; set; }
}
