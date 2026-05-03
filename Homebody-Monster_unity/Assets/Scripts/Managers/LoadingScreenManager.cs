using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using System.Collections;

/// <summary>
/// 씬 전환 시 비동기 로딩 화면을 표시하는 매니저.
///
/// ─ 동작 흐름 ──────────────────────────────────────────────────
///  GameManager.LoadScene(sceneName)
///    → LoadingScreenManager.LoadSceneAsync(sceneName)
///      → LoadingScene 씬을 즉시 동기 로드 (로딩 UI 표시)
///        → 목적지 씬을 비동기로 로드 (allowSceneActivation = false)
///          → 프로그레스바 0 → 0.9 채우기
///            → 0.9 도달 시 자동 전환 또는 "탭하여 시작" 대기
///
/// ─ Inspector 연결 체크리스트 ──────────────────────────────────
///  • progressBar      : Slider (Min=0, Max=1, Interactable=false)
///  • progressText     : "로딩 중... 72%" 표시 TextMeshProUGUI
///  • sceneNameText    : "배틀 준비 중..." 목적지 씬명 표시 (선택)
///  • tipText          : 로딩팁 TextMeshProUGUI (선택)
///  • tapToContinueObj : "탭하여 시작" GameObject (autoActivate=false 시 표시)
///  • backgroundImages : 씬별 배경 이미지 배열 (랜덤 선택)
/// </summary>
public class LoadingScreenManager : MonoBehaviour
{
    public static LoadingScreenManager Instance { get; private set; }

    [Header("프로그레스 UI")]
    public Slider          progressBar;
    public TextMeshProUGUI progressText;
    public TextMeshProUGUI sceneNameText;
    public TextMeshProUGUI tipText;

    [Header("탭하여 시작 (autoActivate=false 일 때만 표시)")]
    public GameObject tapToContinueObj;

    [Header("배경 이미지 (랜덤 선택)")]
    public Sprite[] backgroundImages;
    public Image    backgroundDisplay;

    [Header("설정")]
    [Tooltip("true: 로딩 완료 후 자동 전환 / false: 탭 입력 대기")]
    public bool autoActivate = true;

    [Tooltip("자동 전환 시 0.9 도달 후 대기 시간(초)")]
    [Range(0f, 2f)] public float autoActivateDelay = 0.5f;

    [Tooltip("프로그레스바 보간 속도")]
    [Range(1f, 10f)] public float fillSpeed = 3f;

    private static readonly string[] Tips =
    {
        "💡 상성이 유리하면 데미지가 1.5배!",
        "💡 은신 첫 공격은 1.5배 데미지를 입힙니다.",
        "💡 적 HP가 25% 이하일 때 처형인 패시브가 발동합니다.",
        "💡 7일 연속 출석하면 부활권을 획득할 수 있어요!",
        "💡 재생 패시브는 전투 이탈 4초 후 HP를 회복합니다.",
        "💡 MintChoco와 Pineapple 상성은 2배 데미지!",
        "💡 가시갑옥 패시브는 받은 피해의 10%를 반사합니다.",
        "💡 부활권은 게임 시작 60초 이내에만 사용 가능합니다.",
        "💡 닌자 패시브는 15% 확률로 피격을 회피합니다.",
        "💡 수호 천사 궁극기는 한 번의 즉사를 막아줍니다.",
    };

    private static readonly System.Collections.Generic.Dictionary<string, string> SceneDisplayNames
        = new System.Collections.Generic.Dictionary<string, string>
    {
        { "LobbyScene",  "로비로 이동 중..."  },
        { "InGameScene", "배틀 준비 중..."    },
        { "ResultScene", "결과 집계 중..."    },
        { "LoginScene",  "로그인 화면으로..." },
    };

    private static string _pendingScene    = null;
    private        bool   _readyToActivate = false;

    // ════════════════════════════════════════════════════════════
    //  정적 진입점 — GameManager.LoadScene()에서 호출
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// GameManager.LoadScene()에서 SceneManager.LoadScene() 대신 호출합니다.
    /// NGO가 씬을 관리 중이거나 LoadingScene이 Build Settings에 없으면 동기 폴백합니다.
    /// </summary>
    public static void LoadSceneAsync(string sceneName)
    {
        // NGO가 활성 중이면 서버가 씬 전환을 주도하므로 개입하지 않음
        var netMgr = Unity.Netcode.NetworkManager.Singleton;
        if (netMgr != null && netMgr.IsListening)
        {
            Debug.LogWarning($"[LoadingScreen] NGO 활성 중 직접 씬 전환: {sceneName}. 동기 로드로 폴백.");
            SceneManager.LoadScene(sceneName);
            return;
        }

        _pendingScene = sceneName;

        // LoadingScene이 Build Settings에 없으면 동기 폴백 (기존 동작 유지)
        if (SceneUtility.GetBuildIndexByScenePath("LoadingScene") < 0 &&
            SceneUtility.GetBuildIndexByScenePath("Assets/Scenes/LoadingScene.unity") < 0)
        {
            Debug.LogWarning("[LoadingScreen] 'LoadingScene'이 Build Settings에 없습니다. 동기 로드로 폴백.");
            SceneManager.LoadScene(sceneName);
            return;
        }

        SceneManager.LoadScene("LoadingScene");
    }

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        Instance = this;

        if (progressBar      != null) progressBar.value = 0f;
        if (tapToContinueObj != null) tapToContinueObj.SetActive(false);
    }

    private void Start()
    {
        if (backgroundDisplay != null && backgroundImages != null && backgroundImages.Length > 0)
            backgroundDisplay.sprite = backgroundImages[Random.Range(0, backgroundImages.Length)];

        if (tipText != null)
            tipText.text = Tips[Random.Range(0, Tips.Length)];

        if (sceneNameText != null && _pendingScene != null)
        {
            sceneNameText.text = SceneDisplayNames.TryGetValue(_pendingScene, out string display)
                ? display
                : $"{_pendingScene} 로드 중...";
        }

        if (_pendingScene == null)
        {
            Debug.LogError("[LoadingScreen] pendingScene이 null입니다. LoadSceneAsync()를 통해 진입하세요.");
            return;
        }

        StartCoroutine(LoadRoutine(_pendingScene));
    }

    private void Update()
    {
        if (!autoActivate && _readyToActivate)
        {
            if (Input.GetMouseButtonDown(0) ||
                (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began))
            {
                _readyToActivate = false;
                if (tapToContinueObj != null) tapToContinueObj.SetActive(false);
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  비동기 로딩 코루틴
    // ════════════════════════════════════════════════════════════

    private IEnumerator LoadRoutine(string sceneName)
    {
        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName);
        op.allowSceneActivation = false;

        float displayProgress = 0f;

        while (!op.isDone)
        {
            float targetProgress = Mathf.Clamp01(op.progress / 0.9f);
            displayProgress = Mathf.MoveTowards(displayProgress, targetProgress, fillSpeed * Time.deltaTime);
            UpdateProgressUI(displayProgress);

            if (op.progress >= 0.9f)
            {
                // UI를 100%로 채우는 짧은 연출
                while (displayProgress < 1f)
                {
                    displayProgress = Mathf.MoveTowards(displayProgress, 1f, fillSpeed * Time.deltaTime);
                    UpdateProgressUI(displayProgress);
                    yield return null;
                }

                if (autoActivate)
                {
                    yield return new WaitForSeconds(autoActivateDelay);
                    _pendingScene = null;
                    op.allowSceneActivation = true;
                }
                else
                {
                    _readyToActivate = true;
                    if (tapToContinueObj != null) tapToContinueObj.SetActive(true);

                    while (_readyToActivate)
                        yield return null;

                    _pendingScene = null;
                    op.allowSceneActivation = true;
                }
                yield break;
            }

            yield return null;
        }
    }

    private void UpdateProgressUI(float t)
    {
        if (progressBar  != null) progressBar.value = t;
        if (progressText != null) progressText.text = $"로딩 중... {Mathf.FloorToInt(t * 100f)}%";
    }

    // ════════════════════════════════════════════════════════════
    //  NGO 씬 전환 훅 확장 포인트
    //  AppNetworkManager에서 NetworkManager.SceneManager.OnLoad에 연결하면
    //  서버 주도 씬 전환 시에도 로딩 오버레이를 표시할 수 있습니다.
    // ════════════════════════════════════════════════════════════
    public static void ShowOverlay(string sceneName, AsyncOperation asyncOp)
    {
        Debug.Log($"[LoadingScreen] NGO 씬 로드 시작: {sceneName}");
    }
}
