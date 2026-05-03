using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using System;
using System.Collections.Generic;

/// <summary>
/// 로비 씬 UI 관리자. (팝업형 매칭 UI 및 모바일 호환성 적용 버전)
///
/// 채팅 기능 (Supabase Realtime Broadcast 기반):
///  - 로비 전용 (인게임/결과 화면에서는 작동하지 않음)
///  - 세로형 안드로이드 모바일 최적화: 자동 스크롤, 100자 제한, 엔터키 전송
///  - 채팅 로그 메모리 관리: 최대 50줄 유지
///  - 씬 전환 시 Supabase Realtime 채널 자동 해제
/// </summary>
public class LobbyUIController : MonoBehaviour
{
    [Header("로비 기본 UI")]
    public GameObject lobbyPanel;         // 플레이어 목록 + 채팅 + 시작 버튼 영역
    /// <summary>
    /// "🟢 현재 접속자: 124명" 처럼 단순 카운트를 표시하는 텍스트.
    /// Inspector에서 채팅창 바로 위 작은 텍스트 오브젝트에 연결하세요.
    /// </summary>
    public TextMeshProUGUI onlineCountText;
    /// <summary>상세 접속자 목록 팝업 (선택). 버튼 클릭 시 토글됩니다.</summary>
    public GameObject      playerListPopup;
    public TextMeshProUGUI playerListPopupText;
    public Button          playerListToggleButton;  // "접속자 목록" 버튼
    public TextMeshProUGUI chatLogText;
    public TMP_InputField  chatInputField;
    public Button          sendChatButton;     // 채팅 전송 버튼 (모바일 터치용)
    public Button          startMatchButton;
    public ScrollRect      chatScrollRect;     // 채팅 로그 스크롤 영역 (Inspector 연결)

    [Header("상단 바 UI (프로필 정보)")]
    public TextMeshProUGUI nicknameText;
    public TextMeshProUGUI pizzaCountText;
    public TextMeshProUGUI reviveTicketCountText;

    [Header("매칭 팝업 (Dim 처리 포함)")]
    public GameObject      dimBackground;      // 반투명 배경 (Raycast Target 체크 필수)
    public GameObject      matchmakingPanel;   // 매칭 중일 때 보일 팝업창
    public TextMeshProUGUI queueCountText;     // "3 / 8명"
    public TextMeshProUGUI timerText;          // "00:42"
    public TextMeshProUGUI statusText;         // "상대를 찾는 중..."
    public Slider          timerSlider;        // 60초 진행 바
    public Button          cancelMatchButton;

    [Header("MatchmakingUX (선택 — 매칭 카운트다운 UX 컴포넌트)")]
    /// <summary>
    /// 씬에 MatchmakingUX 컴포넌트가 있을 경우 연결하세요.
    /// OnMatchFound(ip, port) / OnMatchFailed(reason) 이벤트를 수신하여
    /// 카운트다운 후 ConnectToGameServer를 호출합니다.
    /// null이면 MatchmakingManager가 직접 연결합니다.
    /// </summary>
    public MatchmakingUX matchmakingUX;

    [Header("뒤로가기 확인 팝업 (선택)")]
    public GameObject exitConfirmPopup;
    public Button     exitConfirmYesButton;
    public Button     exitConfirmNoButton;
    private bool      _exitPopupOpen = false;

    private float maxWaitSeconds  = 60f;
    private int   maxPlayers      = 8;
    private bool  isPopupActive = false;  // 현재 팝업이 켜져 있는지 추적
    private bool  isPlayerListOpen = false; // 접속자 목록 팝업 토글 상태

    // ── 접속자 목록 캐시 ─────────────────────────────────────────
    private readonly List<string> _cachedPlayerList = new List<string>();

    // ── 채팅 로그 관리 ───────────────────────────────────────────
    private readonly Queue<string> chatLines = new Queue<string>();
    private const int MaxChatLogLines = 50;

    private void Start()
    {
        // MatchmakingManager 값을 null 체크 후 안전하게 읽기
        if (MatchmakingManager.Instance != null)
        {
            maxWaitSeconds = MatchmakingManager.Instance.maxWaitSeconds;
            maxPlayers     = MatchmakingManager.Instance.maxPlayers;
        }

        ShowLobbyPanel();
        AudioManager.Instance?.PlayLobbyBGM();

        // 1. 네트워크(채팅/접속자) 이벤트 연결
        if (AppNetworkManager.Instance != null)
        {
            AppNetworkManager.Instance.OnPlayerPresenceUpdated += UpdatePlayerListDetail;
            AppNetworkManager.Instance.OnChatReceived          += UpdateChatUI;
            AppNetworkManager.Instance.OnClientDisconnected    += HandleServerDisconnected;
            // 채팅 채널 구독 완료 이벤트 — 완료 전까지 전송 버튼 비활성화
            AppNetworkManager.Instance.OnLobbyChatReady        += HandleLobbyChatReady;
            AppNetworkManager.Instance.ConnectToLobby();
        }

        // 접속자 목록 상세 팝업 버튼
        if (playerListToggleButton != null)
            playerListToggleButton.onClick.AddListener(TogglePlayerListPopup);
        if (playerListPopup != null)
            playerListPopup.SetActive(false);

        // 2. 매치메이킹 이벤트 연결
        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnQueueCountChanged    += HandleQueueCountChanged;
            MatchmakingManager.Instance.OnTimerUpdated         += HandleTimerUpdated;
            MatchmakingManager.Instance.OnStatusMessageChanged += HandleStatusChanged;
            MatchmakingManager.Instance.OnMatchFound           += HandleMatchFound;
            MatchmakingManager.Instance.OnMatchmakingFailed    += HandleMatchmakingFailed;

            // [Fix #8] MatchmakingUX — ip/port 페이로드 이벤트 연결
            // OnMatchFound(string, ushort): 매칭 성공 시 카운트다운 + 씬 전환 처리
            // OnMatchFailed(string):        매칭 실패/취소 시 UX 정리
            if (matchmakingUX != null)
            {
                MatchmakingManager.Instance.OnMatchFound  += matchmakingUX.OnMatchFound;
                MatchmakingManager.Instance.OnMatchFailed += matchmakingUX.OnMatchFailed;
            }
        }

        if (timerSlider != null)
            timerSlider.maxValue = maxWaitSeconds;

        // 3. 채팅 입력 필드 설정 (모바일 최적화)
        SetupChatInput();

        // 4. 뒤로가기 확인 팝업 설정
        SetupExitConfirmPopup();

        // 5. 로비 진입 시 시스템 메시지 표시
        UpdateChatUI("[시스템]: 로비에 입장했습니다. 즐거운 게임 되세요!");

        // 6. 일일 출석 보상 체크 (오늘 첫 접속이면 팝업 자동 표시)
        DailyRewardSystem.Instance?.TryClaimOnLobbyEnter();

        // 7. 유저 프로필 정보 로드 및 UI 반영
        RefreshUserProfileUI();
    }

    /// <summary>
    /// Supabase에서 최신 프로필 정보를 가져와 UI에 반영합니다.
    /// [Fix #6] async void 래퍼 + async Task 본체 패턴:
    ///  - async void는 예외를 삼키기 때문에 로직 본체를 별도 Task 메서드로 분리합니다.
    ///  - await 전후에 this == null 체크를 추가하여 씬 전환 중 MissingReferenceException을 방어합니다.
    /// </summary>
    public async void RefreshUserProfileUI()
    {
        await RefreshUserProfileUIAsync();
    }

    private async System.Threading.Tasks.Task RefreshUserProfileUIAsync()
    {
        if (this == null) return; // 씬 전환으로 이미 파괴된 경우 조기 종료
        if (SupabaseManager.Instance == null || string.IsNullOrEmpty(GameManager.Instance?.currentPlayerId)) return;

        string playerId = GameManager.Instance.currentPlayerId;
        var profile = await SupabaseManager.Instance.GetOrCreateProfile(playerId);

        if (this == null) return; // await 도중 씬이 전환되어 오브젝트가 파괴된 경우 방어

        if (profile != null)
        {
            if (nicknameText != null) nicknameText.text = profile.Nickname;
            if (pizzaCountText != null) pizzaCountText.text = $"{profile.PizzaCount}";
            if (reviveTicketCountText != null) reviveTicketCountText.text = $"{profile.ReviveTicketCount}";

            // GameManager 캐시 업데이트
            if (GameManager.Instance != null)
            {
                GameManager.Instance.currentPlayerNickname = profile.Nickname; // 채팅용 닉네임 저장
                GameManager.Instance.reviveTicketCount = profile.ReviveTicketCount;
            }

            // Supabase Presence 등록 — 닉네임 확보 후 호출해야 올바른 식별자로 등록됨
            AppNetworkManager.Instance?.TrackLobbyPresence(profile.Nickname);
        }
    }

    private void Update()
    {
        // Android 뒤로가기 버튼 (Escape 키와 동일)
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            HandleBackButton();
        }
    }

    private void HandleBackButton()
    {
        // 뒤로가기 확인 팝업이 열려 있으면 닫기
        if (_exitPopupOpen)
        {
            CloseExitConfirmPopup();
            return;
        }

        // 접속자 목록 팝업이 열려 있으면 닫기
        if (isPlayerListOpen)
        {
            TogglePlayerListPopup();
            return;
        }

        // 매칭 중이면 매칭 취소
        if (isPopupActive)
        {
            OnClickCancelMatch();
            return;
        }

        // 로비 상태 → 앱 종료 확인 팝업 표시
        OpenExitConfirmPopup();
    }

    private void SetupExitConfirmPopup()
    {
        if (exitConfirmPopup != null) exitConfirmPopup.SetActive(false);
        if (exitConfirmYesButton != null) exitConfirmYesButton.onClick.AddListener(QuitApp);
        if (exitConfirmNoButton  != null) exitConfirmNoButton.onClick.AddListener(CloseExitConfirmPopup);
    }

    private void OpenExitConfirmPopup()
    {
        if (exitConfirmPopup != null)
        {
            _exitPopupOpen = true;
            exitConfirmPopup.SetActive(true);
        }
        else
        {
            QuitApp();
        }
    }

    private void CloseExitConfirmPopup()
    {
        _exitPopupOpen = false;
        if (exitConfirmPopup != null) exitConfirmPopup.SetActive(false);
    }

    private void QuitApp()
    {
        Debug.Log("[LobbyUI] 앱 종료");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void OnDestroy()
    {
        // 🧹 씬 전환 시 참조 해제 (MissingReferenceException 에러 방지)
        if (AppNetworkManager.Instance != null)
        {
            AppNetworkManager.Instance.OnPlayerPresenceUpdated -= UpdatePlayerListDetail;
            AppNetworkManager.Instance.OnChatReceived          -= UpdateChatUI;
            AppNetworkManager.Instance.OnClientDisconnected    -= HandleServerDisconnected;
            AppNetworkManager.Instance.OnLobbyChatReady        -= HandleLobbyChatReady;

            // Supabase Realtime 채팅 채널 구독 해제 (로비 전용이므로 씬 이탈 시 정리)
            AppNetworkManager.Instance.DisconnectLobbyChat();
        }

        if (MatchmakingManager.Instance != null)
        {
            MatchmakingManager.Instance.OnQueueCountChanged    -= HandleQueueCountChanged;
            MatchmakingManager.Instance.OnTimerUpdated         -= HandleTimerUpdated;
            MatchmakingManager.Instance.OnStatusMessageChanged -= HandleStatusChanged;
            MatchmakingManager.Instance.OnMatchFound           -= HandleMatchFound;
            MatchmakingManager.Instance.OnMatchmakingFailed    -= HandleMatchmakingFailed;

            if (matchmakingUX != null)
            {
                MatchmakingManager.Instance.OnMatchFound  -= matchmakingUX.OnMatchFound;
                MatchmakingManager.Instance.OnMatchFailed -= matchmakingUX.OnMatchFailed;
            }
        }
    }

    // ── 채팅 입력 필드 설정 (모바일 UX) ───────────────────────

    private void SetupChatInput()
    {
        if (chatInputField == null) return;

        // 글자 수 제한: Supabase 전송 제한과 일치
        chatInputField.characterLimit = SupabaseManager.MaxChatMessageLength;

        // 모바일 키보드: 한 줄 입력 + 리턴 버튼을 "Send"로 표시
        chatInputField.lineType   = TMP_InputField.LineType.SingleLine;
        chatInputField.contentType = TMP_InputField.ContentType.Standard;

        // 플레이스홀더 텍스트 설정
        if (chatInputField.placeholder is TextMeshProUGUI placeholderText)
            placeholderText.text = "메시지를 입력하세요...";

        // 전송 버튼 연결 — 채널 구독 완료 전까지 비활성화
        if (sendChatButton != null)
        {
            sendChatButton.interactable = false;
            sendChatButton.onClick.AddListener(OnClickSendChat);
        }

        // ⌨️ 엔터키 전송 지원 (New Input System 에러 방지 및 UX 향상)
        chatInputField.onSubmit.AddListener((val) =>
        {
            OnClickSendChat();
            // 전송 후 바로 다시 입력할 수 있게 포커스 유지
            chatInputField.ActivateInputField();
        });
    }

    /// <summary>
    /// Supabase 채팅 채널 구독 완료 시 호출됩니다 (AppNetworkManager.OnLobbyChatReady).
    /// 전송 버튼을 활성화하고 시스템 메시지를 표시합니다.
    /// </summary>
    private void HandleLobbyChatReady()
    {
        if (sendChatButton != null)
            sendChatButton.interactable = true;

        UpdateChatUI("[시스템]: 채팅 연결되었습니다.");
    }

    // ── 버튼 클릭 이벤트 ──────────────────────────────────────

    public void OnClickStartMatch()
    {
        if (MatchmakingManager.Instance == null)
        {
            Debug.LogError("[LobbyUI] MatchmakingManager가 없습니다.");
            return;
        }
        if (startMatchButton != null) startMatchButton.interactable = false;
        ShowMatchmakingPanel();
        MatchmakingManager.Instance.StartSearch();
    }

    public void OnClickCancelMatch()
    {
        if (cancelMatchButton != null) cancelMatchButton.interactable = false;
        if (MatchmakingManager.Instance != null)
            MatchmakingManager.Instance.CancelSearch();
        else
            ShowLobbyPanel();
    }

    public void OnClickSendChat()
    {
        if (chatInputField == null) return;

        string msg = chatInputField.text;
        if (string.IsNullOrWhiteSpace(msg)) return;

        AppNetworkManager.Instance?.SendChatMessage(msg);
        chatInputField.text = "";

        // 📱 모바일: 전송 후 입력 필드에 포커스 유지 (연속 채팅 편의성)
        chatInputField.ActivateInputField();
    }

    // ── 매치메이킹 UI 콜백 ────────────────────────────────────

    private void HandleQueueCountChanged(int current, int max)
    {
        if (queueCountText != null)
            queueCountText.text = $"{current} / {max}명";
    }

    private void HandleTimerUpdated(float remaining)
    {
        if (timerText != null)
        {
            int sec = Mathf.CeilToInt(remaining);
            timerText.text = $"{sec:00}초";
        }

        if (timerSlider != null)
            timerSlider.value = remaining;
    }

    private void HandleStatusChanged(string message)
    {
        if (statusText != null)
            statusText.text = message;
    }

    // [Fix #1] OnMatchFound 시그니처 변경(Action<string,ushort>)에 맞춰 파라미터 추가.
    // ip/port는 MatchmakingUX가 처리하므로 여기서는 UI 상태만 갱신.
    private void HandleMatchFound(string ip, ushort port)
    {
        if (statusText != null)
            statusText.text = "매칭 완료! 게임 입장 중...";

        // 🔒 매칭이 성사되면 더 이상 취소하지 못하도록 버튼 잠금
        if (cancelMatchButton != null)
            cancelMatchButton.interactable = false;
    }

    private void HandleMatchmakingFailed()
    {
        ShowLobbyPanel();
    }

    // ── 채팅/플레이어 UI 콜백 ─────────────────────────────────

    // ── 접속자 카운트 / 상세 목록 UI ─────────────────────────────

    /// <summary>
    /// AppNetworkManager.OnPlayerListUpdated(int) 콜백.
    /// 상단에 "🟢 현재 접속자: N명" 한 줄만 표시합니다.
    /// </summary>
    private void UpdateOnlineCountUI(int count)
    {
        if (onlineCountText != null)
            onlineCountText.text = $"현재 접속자: {count}명";
    }

    // [FIX] OnClientDisconnected 핸들러 추가.
    // 인게임 서버와 연결이 끊어졌을 때 호출됩니다.
    // 정상 게임 종료(ResultController가 NGO 정리) 시에는 호출되지 않고,
    // 비정상 연결 해제(서버 다운, 네트워크 끊김)일 때만 호출됩니다.
    // 인게임 중 재접속 시도는 ReconnectManager가 먼저 처리하며,
    // 재접속 실패 후 로비로 복귀했을 때 이 핸들러가 안내 메시지를 표시합니다.
    private void HandleServerDisconnected(string reason)
    {
        Debug.LogWarning($"[LobbyUIController] 서버 연결 해제: {reason}");
        // 채팅으로 안내 (이미 로비 씬에 있는 경우)
        UpdateChatUI($"[시스템]: 서버 연결이 끊어졌습니다. ({reason})");
        // 매칭 탐색 중이었다면 큐 정리
        if (MatchmakingManager.Instance != null && MatchmakingManager.Instance.IsSearching)
            MatchmakingManager.Instance.CancelSearch();
    }

    /// <summary>
    /// 상세 닉네임 목록이 필요한 경우(예: Supabase Presence 연동 후) 이 메서드를 호출합니다.
    /// 현재는 팝업 내용 갱신만 담당하며, 팝업을 자동으로 열지는 않습니다.
    /// </summary>
    public void UpdatePlayerListDetail(List<string> players)
    {
        _cachedPlayerList.Clear();
        _cachedPlayerList.AddRange(players);

        // 카운트 갱신
        UpdateOnlineCountUI(players.Count);

        // 팝업이 열려 있으면 내용 즉시 갱신
        if (isPlayerListOpen && playerListPopupText != null)
            playerListPopupText.text = "접속자 목록:\n" + string.Join("\n", _cachedPlayerList);
    }

    /// <summary>접속자 상세 목록 팝업을 토글합니다.</summary>
    private void TogglePlayerListPopup()
    {
        if (playerListPopup == null) return;
        isPlayerListOpen = !isPlayerListOpen;
        playerListPopup.SetActive(isPlayerListOpen);

        if (isPlayerListOpen && playerListPopupText != null)
        {
            playerListPopupText.text = _cachedPlayerList.Count > 0
                ? "접속자 목록:\n" + string.Join("\n", _cachedPlayerList)
                : "(상세 목록 없음)";
        }
    }

    private void UpdateChatUI(string message)
    {
        if (chatLogText == null) return;

        chatLines.Enqueue(message);
        while (chatLines.Count > MaxChatLogLines)
            chatLines.Dequeue();

        chatLogText.text = string.Join("\n", chatLines);

        // 📱 자동 스크롤: 새 메시지가 오면 항상 최하단으로 이동
        ScrollToBottom();
    }

    /// <summary>채팅 스크롤 영역을 맨 아래로 이동합니다.</summary>
    private void ScrollToBottom()
    {
        if (chatScrollRect == null || chatScrollRect.content == null) return;
        LayoutRebuilder.ForceRebuildLayoutImmediate(chatScrollRect.content);
        chatScrollRect.verticalNormalizedPosition = 0f;
    }

    // ── 패널 전환 상태 관리 ───────────────────────────────────

    private void ShowLobbyPanel()
    {
        isPopupActive = false;

        if (lobbyPanel       != null) lobbyPanel.SetActive(true);
        if (dimBackground    != null) dimBackground.SetActive(false);    // 딤 배경 숨기기
        if (matchmakingPanel != null) matchmakingPanel.SetActive(false); // 팝업 숨기기

        if (startMatchButton  != null) startMatchButton.interactable = true;
        // cancelMatchButton은 ShowMatchmakingPanel에서만 활성화

        // UI 텍스트/슬라이더 초기화
        if (timerText      != null) timerText.text = $"{(int)maxWaitSeconds:00}초";
        if (timerSlider    != null) timerSlider.value = maxWaitSeconds;
        if (statusText     != null) statusText.text = "";
        if (queueCountText != null) queueCountText.text = $"0 / {maxPlayers}명";
    }

    private void ShowMatchmakingPanel()
    {
        isPopupActive = true;

        if (lobbyPanel        != null) lobbyPanel.SetActive(true);
        if (dimBackground     != null) dimBackground.SetActive(true);
        if (matchmakingPanel  != null) matchmakingPanel.SetActive(true);
        if (cancelMatchButton != null) cancelMatchButton.interactable = true;
    }
}
