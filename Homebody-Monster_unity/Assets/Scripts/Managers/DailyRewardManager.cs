using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Newtonsoft.Json;

// ════════════════════════════════════════════════════════════════
//  Supabase SQL — 한 번만 실행하세요 (Supabase SQL Editor)
//
//  1. daily_logins 테이블
// ────────────────────────────────────────────────────────────────
//  CREATE TABLE daily_logins (
//    id              BIGSERIAL PRIMARY KEY,
//    player_id       UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
//    login_date      DATE NOT NULL DEFAULT CURRENT_DATE,
//    streak          INT  NOT NULL DEFAULT 1,
//    reward_pizza    INT  NOT NULL DEFAULT 0,
//    claimed         BOOLEAN NOT NULL DEFAULT FALSE,
//    created_at      TIMESTAMPTZ DEFAULT NOW(),
//    UNIQUE(player_id, login_date)
//  );
//
//  ALTER TABLE daily_logins ENABLE ROW LEVEL SECURITY;
//  CREATE POLICY "own_rows" ON daily_logins
//    FOR ALL TO authenticated USING (auth.uid() = player_id);
//
// ────────────────────────────────────────────────────────────────
//  2. claim_daily_reward() RPC  (기존 함수 교체)
//     SECURITY DEFINER — 중복 방지·streak·피자 지급 원자 처리
//     CURRENT_DATE = Supabase PostgreSQL 기본 UTC 기준
// ────────────────────────────────────────────────────────────────
//  CREATE OR REPLACE FUNCTION claim_daily_reward()
//  RETURNS JSON LANGUAGE plpgsql SECURITY DEFINER AS $$
//  DECLARE
//    v_uid         UUID := auth.uid();
//    v_today       DATE := CURRENT_DATE;
//    v_yesterday   DATE := CURRENT_DATE - 1;
//    v_streak      INT  := 1;
//    v_reward      INT;
//    v_prev_streak INT;
//  BEGIN
//    IF EXISTS (
//      SELECT 1 FROM daily_logins
//       WHERE player_id = v_uid AND login_date = v_today AND claimed = TRUE
//    ) THEN
//      SELECT streak INTO v_streak FROM daily_logins
//       WHERE player_id = v_uid AND login_date = v_today;
//      RETURN json_build_object('already_claimed', TRUE, 'streak', v_streak, 'reward_pizza', 0);
//    END IF;
//
//    SELECT streak INTO v_prev_streak FROM daily_logins
//     WHERE player_id = v_uid AND login_date = v_yesterday;
//    IF v_prev_streak IS NOT NULL THEN
//      v_streak := (v_prev_streak % 7) + 1;
//    END IF;
//
//    v_reward := CASE v_streak
//      WHEN 1 THEN 10  WHEN 2 THEN 15  WHEN 3 THEN 20  WHEN 4 THEN 25
//      WHEN 5 THEN 30  WHEN 6 THEN 40  WHEN 7 THEN 60  ELSE 10
//    END;
//
//    INSERT INTO daily_logins(player_id, login_date, streak, reward_pizza, claimed)
//    VALUES (v_uid, v_today, v_streak, v_reward, TRUE)
//    ON CONFLICT (player_id, login_date)
//    DO UPDATE SET streak       = EXCLUDED.streak,
//                  reward_pizza = EXCLUDED.reward_pizza,
//                  claimed      = TRUE;
//
//    UPDATE profiles SET pizza_count = pizza_count + v_reward WHERE id = v_uid;
//
//    RETURN json_build_object(
//      'already_claimed', FALSE,
//      'streak',          v_streak,
//      'reward_pizza',    v_reward
//    );
//  END $$;
//
// ────────────────────────────────────────────────────────────────
//  3. fetch_today_login_status() RPC
// ────────────────────────────────────────────────────────────────
//  CREATE OR REPLACE FUNCTION fetch_today_login_status()
//  RETURNS JSON LANGUAGE plpgsql SECURITY DEFINER AS $$
//  DECLARE
//    v_uid       UUID := auth.uid();
//    v_today     DATE := CURRENT_DATE;
//    v_yesterday DATE := CURRENT_DATE - 1;
//    v_streak    INT;
//    v_rec       daily_logins%ROWTYPE;
//  BEGIN
//    SELECT * INTO v_rec FROM daily_logins
//     WHERE player_id = v_uid AND login_date = v_today;
//    IF FOUND THEN
//      RETURN json_build_object('streak', v_rec.streak, 'claimed', v_rec.claimed);
//    END IF;
//
//    SELECT streak INTO v_streak FROM daily_logins
//     WHERE player_id = v_uid AND login_date = v_yesterday;
//
//    IF v_streak IS NOT NULL THEN
//      v_streak := (v_streak % 7) + 1;
//    ELSE
//      v_streak := 1;
//    END IF;
//
//    RETURN json_build_object('streak', v_streak, 'claimed', FALSE);
//  END $$;
// ════════════════════════════════════════════════════════════════

// ── DB 응답 역직렬화 DTO ───────────────────────────────────────

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
/// Streak = 0은 RPC 실패 신호 (DB 반환 최솟값은 1).
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
/// ── 버그 수정 이력 ─────────────────────────────────────────────
///  #1 - DaySlotUI [Serializable] + MonoBehaviour 중복 제거
///  #2 - SupabaseManager null + IsInitialized 체크 누락
///  #3 - async/await·코루틴 복귀 후 this == null 방어 누락
///  #4 - PlayerPrefs 날짜 기준을 UTC로 통일 (DB CURRENT_DATE UTC와 일치)
///  #5 - RPC Content 파싱: 1차(unquote) → 2차(직접) 폴백 + ParseRpcJson 공통 헬퍼
///  #6 - DailyLoginRecord.LoginDate DateTime → string (DATE 컬럼 파싱 실패 방지)
///  #7 - ShowPanelAsync 중복 호출 가드 + try-finally로 플래그 누수 방지
///  #8 - FetchCurrentStreak(int) → FetchTodayStatus(streak+claimed) 교체
///  #9 - FetchTodayStatus 실패 시 defaultStatus.Streak=0으로 로컬 캐시 보호
///
/// ── Inspector 연결 체크리스트 ────────────────────────────────────
///  □ rewardPanel      : 패널 루트 GameObject
///  □ daySlots[7]      : DaySlotUI 컴포넌트가 붙은 슬롯 7개
///  □ streakText       : "🔥 3일 연속 출석 중!" TMP
///  □ rewardText       : "🍕 20 피자 획득!" TMP
///  □ statusText       : 로딩·오류 안내 TMP
///  □ claimButton      : "보상 받기" 버튼
///  □ skipButton       : "닫기" 버튼
///  □ pizzaFlyAnimator : 피자 이펙트 Animator (선택)
///  □ claimSoundClip   : 수령 효과음 AudioClip (선택 — 미설정 시 AudioManager 폴백)
///
/// ── LobbyUIController 연동 ───────────────────────────────────────
///  LobbyUIController.Start()에서 호출:
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
    public AudioClip claimSoundClip; // 미설정 시 AudioManager.PlayDailyReward() 폴백

    // ── 보상 테이블 (인덱스 0 = 1일차) ─────────────────────────
    private static readonly int[] PizzaRewardTable = { 10, 15, 20, 25, 30, 40, 60 };

    // ── PlayerPrefs 키 ───────────────────────────────────────────
    // [Bug Fix #4] DateTime.UtcNow 기준 — DB CURRENT_DATE(Supabase 기본 UTC)와 통일
    private const string PrefKeyLastClaimDate = "DailyReward_LastClaimDate";
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
    /// 로컬 캐시로 오늘 수령 여부를 먼저 확인하므로 DB 조회를 최소화합니다.
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
        // [Bug Fix #3]
        if (this == null || rewardPanel == null) return;

        // [Bug Fix #7] 중복 실행 차단
        if (_isShowingPanel) return;
        _isShowingPanel = true;

        try
        {
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

                // [Bug Fix #3] await 복귀 후 재확인
                if (this == null) return;

                // [Bug Fix #9] Streak > 0 조건으로 DB 실패(=0)와 성공(>=1)을 구분
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
            }

            bool claimed = AlreadyClaimedTodayLocally();
            if (claimButton != null) claimButton.interactable = !claimed;
            if (streakText  != null)
                streakText.text = claimed
                    ? $"✅ {localStreak}일 연속 출석 완료!"
                    : $"🔥 {localStreak}일 연속 출석 중!";
        }
        finally
        {
            // [Bug Fix #7] 어떤 경로로 종료해도 플래그 해제 보장
            if (this != null) _isShowingPanel = false;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  보상 수령
    // ════════════════════════════════════════════════════════════

    private async void OnClaimClicked()
    {
        if (_isClaiming) return;

        if (AlreadyClaimedTodayLocally())
        {
            SetStatus("오늘 이미 수령하셨습니다.");
            if (claimButton != null) claimButton.interactable = false;
            return;
        }

        // [Bug Fix #2]
        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
        {
            SetStatus("⚠ 서버에 연결되지 않았습니다. 잠시 후 다시 시도해주세요.");
            return;
        }

        _isClaiming = true;
        if (claimButton != null) claimButton.interactable = false;
        SetStatus("서버에 요청 중...");

        var result = await SupabaseManager.Instance.ClaimDailyReward();

        // [Bug Fix #3]
        if (this == null) return;

        _isClaiming = false;

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

        // 효과음: Inspector 클립 우선, 없으면 AudioManager 폴백
        if (claimSoundClip != null && _audioSource != null)
            _audioSource.PlayOneShot(claimSoundClip);
        else
            AudioManager.Instance?.PlayDailyReward();

        StartCoroutine(RefreshPizzaUIAfterDelay(0.5f));

        Debug.Log($"[DailyReward] ✅ 수령 완료 — streak={result.Streak}, pizza={result.RewardPizza}");
    }

    private IEnumerator RefreshPizzaUIAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        // [Bug Fix #3]
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

// ════════════════════════════════════════════════════════════════
//  DaySlotUI — 7일 칸 하나를 제어하는 컴포넌트
//
//  [Bug Fix #1] [Serializable] 제거
//  MonoBehaviour 상속 클래스에 [Serializable]을 함께 붙이면
//  Unity 직렬화 시스템과 충돌하여 Inspector 오작동 발생.
// ════════════════════════════════════════════════════════════════
public class DaySlotUI : MonoBehaviour
{
    [Header("슬롯 내부 UI 참조")]
    public TextMeshProUGUI dayLabel;       // "1일"
    public TextMeshProUGUI pizzaLabel;     // "🍕 10"
    public Image           highlightImage; // 오늘 테두리 강조
    public Image           checkMark;      // 수령 완료 체크 아이콘
    public CanvasGroup     futureOverlay;  // 미래 슬롯 반투명 오버레이

    [Header("색상")]
    public Color colorToday     = new Color(1f,   0.85f, 0f);
    public Color colorCompleted = new Color(0.4f, 0.85f, 0.4f);
    public Color colorFuture    = new Color(0.5f, 0.5f,  0.5f);

    public void SetDay(int dayNumber, int pizza, bool isCompleted, bool isToday, bool isFuture)
    {
        if (dayLabel != null)
            dayLabel.text = $"{dayNumber}일";

        if (pizzaLabel != null)
        {
            pizzaLabel.text  = dayNumber == 7 ? $"🍕 {pizza}\n🎁 보너스" : $"🍕 {pizza}";
            pizzaLabel.color = isCompleted ? colorCompleted
                             : isToday     ? colorToday
                             : colorFuture;
        }

        if (highlightImage != null)
        {
            highlightImage.enabled = isToday;
            highlightImage.color   = colorToday;
        }

        if (checkMark != null)
            checkMark.enabled = isCompleted;

        if (futureOverlay != null)
        {
            futureOverlay.alpha          = isFuture ? 0.45f : 0f;
            futureOverlay.blocksRaycasts = false;
        }
    }
}

// ════════════════════════════════════════════════════════════════
//  ORM 모델 — daily_logins 테이블
//
//  [Bug Fix #6] login_date 컬럼을 string LoginDateStr으로 매핑.
//  Supabase DATE 컬럼은 "yyyy-MM-dd" 문자열로 내려옵니다.
//  C# DateTime 자동변환은 시간대 처리가 불명확하므로 string으로 수신합니다.
// ════════════════════════════════════════════════════════════════
[Supabase.Postgrest.Attributes.Table("daily_logins")]
public class DailyLoginRecord : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [JsonProperty("id")]              public long   Id           { get; set; }

    [Supabase.Postgrest.Attributes.Column("player_id")]
    [JsonProperty("player_id")]       public string PlayerId     { get; set; }

    // [Bug Fix #6] DateTime → string
    [Supabase.Postgrest.Attributes.Column("login_date")]
    [JsonProperty("login_date")]      public string LoginDateStr { get; set; }

    [Supabase.Postgrest.Attributes.Column("streak")]
    [JsonProperty("streak")]          public int    Streak       { get; set; }

    [Supabase.Postgrest.Attributes.Column("reward_pizza")]
    [JsonProperty("reward_pizza")]    public int    RewardPizza  { get; set; }

    [Supabase.Postgrest.Attributes.Column("claimed")]
    [JsonProperty("claimed")]         public bool   Claimed      { get; set; }
}
