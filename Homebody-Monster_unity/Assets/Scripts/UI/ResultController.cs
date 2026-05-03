using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using System.Collections;
using System.Collections.Generic;

#if GOOGLE_MOBILE_ADS
using GoogleMobileAds.Api;
using GoogleMobileAds.Common;
#endif

/// <summary>
/// 결과 씬 UI를 관리합니다.
/// GameManager.lastMatchResult에서 실제 게임 결과를 가져와 표시합니다.
/// 피자 보상 수령, 광고 2배 보너스, 전적 표시, 결과 채팅 기능을 포함합니다.
/// </summary>
public class ResultController : MonoBehaviour
{
    [Header("UI References")]
    public CanvasGroup mainCanvasGroup;
    public TextMeshProUGUI resultTitleText;
    public TextMeshProUGUI resultDetailText;

    [Header("보상 UI")]
    public TextMeshProUGUI rewardText;          // "🍕 60 피자 획득!"
    public Button adBonusButton;                // "광고 시청으로 2배!" 버튼
    public TextMeshProUGUI adBonusButtonText;   // 버튼 내부 텍스트

    [Header("전적 UI")]
    public TextMeshProUGUI recordText;          // "전적: 12승 5패"

    [Header("Chat UI")]
    public TextMeshProUGUI chatLogText;
    public TMP_InputField chatInputField;
    public ScrollRect chatScrollRect;

    [Header("Buttons")]
    public Button exitButton;
    public Button sendButton;

    // ── 채팅 로그 메모리 관리 ─────────────────────────────────
    private readonly Queue<string> chatLines = new Queue<string>();
    private const int MaxChatLogLines = 50;

    // ── AdMob 광고 단위 ID ────────────────────────────────────
    // [수정 필요] 실제 AdMob 콘솔에서 발급받은 광고 단위 ID로 교체하세요.
    // 아래는 Google 공식 테스트 ID입니다 (출시 전까지 유지).
#if UNITY_ANDROID
    private const string RewardedAdUnitId = "ca-app-pub-3940256099942544/5224354917";
#elif UNITY_IOS
    private const string RewardedAdUnitId = "ca-app-pub-3940256099942544/1712485313";
#else
    private const string RewardedAdUnitId = "unused";
#endif

    // ── 보상 상태 ────────────────────────────────────────────
    private int  _earnedPizza    = 0;
    private bool _adBonusClaimed = false;
    private bool _rewardClaimed  = false;

#if GOOGLE_MOBILE_ADS
    // ── AdMob 보상형 광고 ─────────────────────────────────────
    private RewardedAd _rewardedAd;
    private bool _isAdLoading = false;
#endif

    private void Awake()
    {
        if (mainCanvasGroup != null)
            mainCanvasGroup.alpha = 0;

        // 광고 보너스 버튼 초기 비활성
        if (adBonusButton != null) adBonusButton.gameObject.SetActive(false);
    }

    private void Start()
    {
        // ── NGO 연결 정리 (인게임 → 결과 씬 전환 시) ──────────
        CleanupNetcodeConnection();

        // ── AdMob 초기화 ──────────────────────────────────────
#if GOOGLE_MOBILE_ADS
        InitializeAdMob();
#endif

        // ── 채팅 이벤트 연결 + 로비 채팅 채널 접속 ──────────────
        if (AppNetworkManager.Instance != null)
        {
            AppNetworkManager.Instance.OnChatReceived += UpdateChatUI;
            // [Fix #4] 이벤트 구독 이후에 ConnectToLobby 호출해야 이미 수신된 메시지가 누락되지 않음
            AppNetworkManager.Instance.ConnectToLobby();
        }

        DisplayMatchResult();
        StartCoroutine(FadeInSequence());

        // 버튼 리스너 등록
        if (exitButton != null) exitButton.onClick.AddListener(OnClickExitToLobby);
        if (sendButton != null) sendButton.onClick.AddListener(OnClickSendChat);
        if (adBonusButton != null) adBonusButton.onClick.AddListener(OnClickAdBonus);

        // 채팅 입력 설정
        SetupChatInput();

        // 전적 로드 및 피자 보상 수령
        StartCoroutine(PostResultSequence());
    }

    private void Update()
    {
        // 엔터 키로 채팅 전송 지원 (New Input System)
        if (Keyboard.current != null && Keyboard.current.enterKey.wasPressedThisFrame)
        {
            if (chatInputField != null && chatInputField.isFocused)
            {
                OnClickSendChat();
            }
            else if (chatInputField != null)
            {
                chatInputField.ActivateInputField();
            }
        }
    }

    private void OnDestroy()
    {
        if (AppNetworkManager.Instance != null)
        {
            AppNetworkManager.Instance.OnChatReceived -= UpdateChatUI;
            // [Fix #4] 씬 전환 시 채팅 채널 구독 해제 — 리소스 누수 및 이벤트 중복 방지
            AppNetworkManager.Instance.DisconnectLobbyChat();
        }

        // 광고 객체 메모리 해제
#if GOOGLE_MOBILE_ADS
        DestroyRewardedAd();
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  페이드 인 연출
    // ════════════════════════════════════════════════════════════

    private IEnumerator FadeInSequence()
    {
        if (mainCanvasGroup == null) yield break;

        float elapsed = 0f;
        float duration = 0.8f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            mainCanvasGroup.alpha = Mathf.Lerp(0, 1, elapsed / duration);
            yield return null;
        }
        mainCanvasGroup.alpha = 1;
    }

    // ════════════════════════════════════════════════════════════
    //  결과 표시
    // ════════════════════════════════════════════════════════════

    private void DisplayMatchResult()
    {
        if (GameManager.Instance == null) return;

        MatchResult result = GameManager.Instance.lastMatchResult;

        // 제목 텍스트
        if (resultTitleText != null)
        {
            if (result.isWinner)
                resultTitleText.text = "<color=#f1c40f><size=72>🏆 VICTORY!</size></color>\n<size=36>최후의 1인! 승리했습니다.</size>";
            else
                resultTitleText.text = $"<color=#e74c3c><size=72>DEFEATED</size></color>\n<size=36>{result.rank}위로 탈락했습니다.</size>";
        }

        // 상세 텍스트 (킬 수, 생존 시간)
        if (resultDetailText != null)
        {
            int min = (int)(result.survivedTime / 60f);
            int sec = (int)(result.survivedTime % 60f);
            resultDetailText.text = $"KILLS: <color=#3498db>{result.killCount}</color>   SURVIVAL: <color=#2ecc71>{min:00}:{sec:00}</color>";
        }
    }

    // ════════════════════════════════════════════════════════════
    //  결과 씬 진입 후 순차 처리 (전적 로드 → 보상 수령 → 광고 버튼)
    // ════════════════════════════════════════════════════════════

    private IEnumerator PostResultSequence()
    {
        // 1. Supabase 프로필에서 전적 불러오기
        yield return StartCoroutine(LoadAndDisplayRecord());

        // 2. 피자 보상 수령 (DB RPC: grant_match_rewards)
        yield return StartCoroutine(ClaimMatchRewards());
    }

    /// <summary>
    /// Supabase 프로필에서 승/패 전적을 로드하여 UI에 표시합니다.
    ///
    /// [Fix] GameManager.MatchResultSaveTask 완료 대기 추가.
    /// save_match_result RPC가 완료되기 전에 GetOrCreateProfile을 호출하면
    /// 이번 매치 결과가 반영되지 않은 이전 전적이 표시되는 버그 수정.
    /// 최대 5초 대기 후 타임아웃 시 그 시점의 프로필로 표시.
    /// </summary>
    private IEnumerator LoadAndDisplayRecord()
    {
        if (recordText == null || SupabaseManager.Instance == null ||
            GameManager.Instance == null || string.IsNullOrEmpty(GameManager.Instance.currentPlayerId))
        {
            yield break;
        }

        // save_match_result 완료 대기 (최대 5초 타임아웃)
        var saveTask = GameManager.Instance.MatchResultSaveTask;
        if (saveTask != null && !saveTask.IsCompleted)
        {
            float waited = 0f;
            const float timeout = 5f;
            while (!saveTask.IsCompleted && waited < timeout)
            {
                yield return null;
                waited += Time.deltaTime;
            }

            if (!saveTask.IsCompleted)
                Debug.LogWarning("[ResultController] 결과 저장 대기 타임아웃 — 이전 전적이 표시될 수 있습니다.");
        }

        var task = SupabaseManager.Instance.GetOrCreateProfile(GameManager.Instance.currentPlayerId);
        while (!task.IsCompleted) yield return null;

        if (task.Result != null)
        {
            var profile = task.Result;
            recordText.text = $"전적: <color=#f1c40f>{profile.WinCount}</color>승  <color=#e74c3c>{profile.LoseCount}</color>패";
        }
        else
        {
            recordText.text = "전적: 불러오기 실패";
        }
    }

    /// <summary>
    /// 피자 보상을 DB에서 수령하고 UI에 표시합니다.
    /// 수령 후 광고 2배 보너스 버튼을 활성화합니다.
    /// </summary>
    private IEnumerator ClaimMatchRewards()
    {
        if (_rewardClaimed || SupabaseManager.Instance == null || GameManager.Instance == null)
        {
            yield break;
        }

        MatchResult result = GameManager.Instance.lastMatchResult;

        var task = SupabaseManager.Instance.GrantMatchRewards(result.rank, result.killCount, false);
        while (!task.IsCompleted) yield return null;

        _earnedPizza = task.Result;
        _rewardClaimed = true;

        // 보상 표시
        if (rewardText != null)
        {
            if (_earnedPizza > 0)
                rewardText.text = $"<color=#f39c12>🍕 {_earnedPizza} 피자 획득!</color>";
            else
                rewardText.text = "<color=#95a5a6>보상 없음</color>";
        }

        // 광고 2배 버튼 활성화 (보상이 있을 때만)
        if (adBonusButton != null && _earnedPizza > 0)
        {
            adBonusButton.gameObject.SetActive(true);
            if (adBonusButtonText != null)
                adBonusButtonText.text = $"📺 광고 시청으로 {_earnedPizza}🍕 추가 획득!";
        }
    }

    // ════════════════════════════════════════════════════════════
    //  AdMob 초기화 및 보상형 광고
    // ════════════════════════════════════════════════════════════

#if GOOGLE_MOBILE_ADS
    private void InitializeAdMob()
    {
        MobileAds.Initialize(status =>
        {
            MobileAdsEventExecutor.ExecuteInUpdate(() =>
            {
                Debug.Log("[AdMob] 초기화 완료");
                LoadRewardedAd();
            });
        });
    }

    private void LoadRewardedAd()
    {
        if (_isAdLoading) return;
        _isAdLoading = true;
        DestroyRewardedAd();
        RewardedAd.Load(RewardedAdUnitId, new AdRequest(), OnRewardedAdLoaded);
    }

    private void OnRewardedAdLoaded(RewardedAd ad, LoadAdError error)
    {
        _isAdLoading = false;
        if (error != null || ad == null)
        {
            Debug.LogWarning($"[AdMob] 광고 로드 실패: {error?.GetMessage()}");
            return;
        }
        Debug.Log("[AdMob] 보상형 광고 로드 완료");
        _rewardedAd = ad;
        RegisterRewardedAdEvents(_rewardedAd);
    }

    private void RegisterRewardedAdEvents(RewardedAd ad)
    {
        ad.OnAdImpressionRecorded      += () => Debug.Log("[AdMob] 광고 노출 기록");
        ad.OnAdClicked                 += () => Debug.Log("[AdMob] 광고 클릭");
        ad.OnAdFullScreenContentOpened += () => Debug.Log("[AdMob] 광고 전체 화면 오픈");
        ad.OnAdFullScreenContentClosed += () =>
        {
            Debug.Log("[AdMob] 광고 닫힘 — 다음 광고 사전 로드");
            LoadRewardedAd();
        };
        ad.OnAdFullScreenContentFailed += adError =>
        {
            Debug.LogWarning($"[AdMob] 광고 표시 실패: {adError.GetMessage()}");
            SetAdButtonInteractable(true);
        };
    }

    private void DestroyRewardedAd()
    {
        if (_rewardedAd != null) { _rewardedAd.Destroy(); _rewardedAd = null; }
    }
#endif

    // ════════════════════════════════════════════════════════════
    //  광고 2배 보너스
    // ════════════════════════════════════════════════════════════

    private void OnClickAdBonus()
    {
        if (_adBonusClaimed || _earnedPizza <= 0) return;

#if GOOGLE_MOBILE_ADS
        if (_rewardedAd != null && _rewardedAd.CanShowAd())
        {
            SetAdButtonInteractable(false);
            _rewardedAd.Show(reward =>
            {
                MobileAdsEventExecutor.ExecuteInUpdate(() =>
                {
                    Debug.Log($"[AdMob] 보상 지급: {reward.Type} x{reward.Amount}");
                    StartCoroutine(ClaimAdBonusReward());
                });
            });
        }
        else
        {
            Debug.LogWarning("[AdMob] 광고 아직 준비되지 않음 — 재로드 중");
            if (adBonusButtonText != null)
                adBonusButtonText.text = "📡 광고 준비 중... 잠시 후 다시 눌러주세요";
            SetAdButtonInteractable(false);
            LoadRewardedAd();
            StartCoroutine(ReenableAdButtonAfterDelay(3f));
        }
#else
        // AdMob SDK 미설치 시 광고 없이 바로 보상 지급 (개발/테스트 환경)
        StartCoroutine(ClaimAdBonusReward());
#endif
    }

    private void SetAdButtonInteractable(bool interactable)
    {
        if (adBonusButton != null) adBonusButton.interactable = interactable;
    }

#if GOOGLE_MOBILE_ADS
    private IEnumerator ReenableAdButtonAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        SetAdButtonInteractable(true);
        if (adBonusButtonText != null)
            adBonusButtonText.text = $"📺 광고 시청으로 {_earnedPizza}🍕 추가 획득!";
    }
#endif

    private IEnumerator ClaimAdBonusReward()
    {
        // [Fix] _adBonusClaimed를 await 이전에 즉시 true로 설정.
        // 기존 코드는 GrantMatchRewards() 완료 후에 설정하므로,
        // 광고 콜백(MobileAdsEventExecutor.ExecuteInUpdate)과 버튼 탭 사이의
        // 수십~수백ms 사이에 OnClickAdBonus()가 재진입하면 _adBonusClaimed가
        // 아직 false여서 코루틴이 두 번 시작됨 → grant_match_rewards 중복 호출 →
        // 광고 보너스 피자 2배 중복 지급.
        if (_adBonusClaimed) yield break; // 혹시 모를 재진입 이중 방어
        _adBonusClaimed = true;

        if (adBonusButton != null) adBonusButton.interactable = false;

        if (SupabaseManager.Instance == null || GameManager.Instance == null)
            yield break;

        MatchResult result = GameManager.Instance.lastMatchResult;

        // DB에 광고 2배 보상 요청
        var task = SupabaseManager.Instance.GrantMatchRewards(result.rank, result.killCount, true);
        while (!task.IsCompleted) yield return null;

        int bonusPizza = task.Result;

        if (rewardText != null)
            rewardText.text = $"<color=#f39c12>🍕 {_earnedPizza} + {bonusPizza} 피자 획득! (광고 보너스)</color>";

        if (adBonusButton != null)
            adBonusButton.gameObject.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  채팅
    // ════════════════════════════════════════════════════════════

    private void SetupChatInput()
    {
        if (chatInputField == null) return;

        chatInputField.characterLimit = 100;
        chatInputField.lineType = TMP_InputField.LineType.SingleLine;

        // 엔터키 전송 (onSubmit 이벤트 활용 — Update()의 Input.GetKeyDown과 상보적)
        chatInputField.onSubmit.AddListener((val) =>
        {
            OnClickSendChat();
            chatInputField.ActivateInputField();
        });
    }

    public void OnClickSendChat()
    {
        if (chatInputField != null && !string.IsNullOrEmpty(chatInputField.text))
        {
            AppNetworkManager.Instance?.SendChatMessage(chatInputField.text);
            chatInputField.text = "";
            chatInputField.ActivateInputField(); // 전송 후 다시 포커스
        }
    }

    private void UpdateChatUI(string message)
    {
        if (chatLogText == null) return;

        // 채팅 로그 메모리 관리 (최대 50줄)
        chatLines.Enqueue(message);
        while (chatLines.Count > MaxChatLogLines)
            chatLines.Dequeue();

        chatLogText.text = string.Join("\n", chatLines);

        // 스크롤을 맨 아래로 내림
        ScrollToBottom();
    }

    /// <summary>채팅 스크롤 영역을 맨 아래로 이동합니다.</summary>
    private void ScrollToBottom()
    {
        if (chatScrollRect == null || chatScrollRect.content == null) return;
        LayoutRebuilder.ForceRebuildLayoutImmediate(chatScrollRect.content);
        chatScrollRect.verticalNormalizedPosition = 0f;
    }

    // ════════════════════════════════════════════════════════════
    //  NGO 연결 정리
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 인게임에서 결과 씬으로 넘어올 때 NGO 연결을 정리합니다.
    /// NetworkManager.Shutdown()은 씬 전환 후 호출해야 안전합니다.
    /// </summary>
    private void CleanupNetcodeConnection()
    {
        if (Unity.Netcode.NetworkManager.Singleton != null &&
            Unity.Netcode.NetworkManager.Singleton.IsListening)
        {
            Debug.Log("[ResultController] NGO 연결 종료 (결과 씬 진입)");
            Unity.Netcode.NetworkManager.Singleton.Shutdown();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  로비로 복귀
    // ════════════════════════════════════════════════════════════

    public void OnClickExitToLobby()
    {
        // 중복 클릭 방지
        if (exitButton != null) exitButton.interactable = false;
        
        GameManager.Instance?.ResetForNewMatch();
    }
}

