using UnityEngine;
using Unity.Cinemachine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 사망 후 관전(스펙테이터) 모드 관리자.
/// 사망 시 EnterSpectator()를 호출하면 살아있는 다른 플레이어를 순환 관전합니다.
///
/// PlayerController.PlayDeathAnimation() 끝에서 호출하세요.
/// (로컬 플레이어에게만 해당)
/// </summary>
public class SpectatorManager : MonoBehaviour
{
    public static SpectatorManager Instance { get; private set; }

    // ── Inspector ───────────────────────────────────────────────
    [Header("씬 참조")]
    [Tooltip("InGameScene의 CinemachineCamera 오브젝트")]
    public CinemachineCamera virtualCamera;

    [Header("UI")]
    public GameObject spectatorUI;                      // "관전 중" 배너
    public TMPro.TextMeshProUGUI spectatorNameText;     // "관전: 플레이어A"
    public UnityEngine.UI.Button prevButton;            // ◀
    public UnityEngine.UI.Button nextButton;            // ▶

    // ── 내부 상태 ────────────────────────────────────────────────
    private List<PlayerController> _targets = new();
    private int                    _currentIndex;
    private Coroutine              _refreshCoroutine;
    private bool                   _isSpectating;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        spectatorUI?.SetActive(false);

        if (prevButton != null) prevButton.onClick.AddListener(PrevTarget);
        if (nextButton != null) nextButton.onClick.AddListener(NextTarget);
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 로컬 플레이어 사망 시 호출합니다.
    /// </summary>
    public void EnterSpectator()
    {
        if (_isSpectating) return;
        _isSpectating = true;

        // 인게임 조작 UI 숨기기
        InGameHUD.Instance?.SetControlsVisible(false);

        spectatorUI?.SetActive(true);

        RefreshTargetList();

        if (_targets.Count == 0)
        {
            if (spectatorNameText != null) spectatorNameText.text = "관전할 플레이어 없음";
            return;
        }

        _currentIndex = 0;
        FocusTarget(_currentIndex);

        if (_refreshCoroutine != null) StopCoroutine(_refreshCoroutine);
        _refreshCoroutine = StartCoroutine(RefreshRoutine());
    }

    /// <summary>
    /// 관전 모드를 종료합니다 (게임 종료 또는 씬 전환 시).
    /// </summary>
    public void ExitSpectator()
    {
        _isSpectating = false;
        if (_refreshCoroutine != null) { StopCoroutine(_refreshCoroutine); _refreshCoroutine = null; }
        spectatorUI?.SetActive(false);
        InGameHUD.Instance?.SetControlsVisible(true);
    }

    // ════════════════════════════════════════════════════════════
    //  관전 대상 전환
    // ════════════════════════════════════════════════════════════

    public void NextTarget()
    {
        if (_targets.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _targets.Count;
        FocusTarget(_currentIndex);
    }

    public void PrevTarget()
    {
        if (_targets.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _targets.Count) % _targets.Count;
        FocusTarget(_currentIndex);
    }

    // ════════════════════════════════════════════════════════════
    //  내부 메서드
    // ════════════════════════════════════════════════════════════

    private void FocusTarget(int index)
    {
        if (index < 0 || index >= _targets.Count) return;
        var target = _targets[index];
        if (target == null) { RefreshTargetList(); return; }

        // Cinemachine 3.x: Follow / LookAt 대상 교체
        if (virtualCamera != null)
        {
            virtualCamera.Follow = target.transform;
            virtualCamera.LookAt = target.transform;
        }

        string name = target.myData?.playerName ?? $"Player_{index}";
        if (spectatorNameText != null)
            spectatorNameText.text = $"관전: {name}";
    }

    private void RefreshTargetList()
    {
        _targets.Clear();

        var mgr = InGameManager.Instance;
        if (mgr == null) return;

        // alivePlayers에서 로컬 플레이어 제외
        foreach (var p in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
        {
            if (p == null || p.IsDead) continue;
            if (p.IsLocalPlayer) continue;
            _targets.Add(p);
        }

        // 현재 인덱스 범위 보정
        if (_targets.Count > 0)
            _currentIndex = Mathf.Clamp(_currentIndex, 0, _targets.Count - 1);
    }

    private IEnumerator RefreshRoutine()
    {
        var wait = new WaitForSeconds(1f);
        while (_isSpectating)
        {
            yield return wait;
            RefreshTargetList();

            if (_targets.Count == 0)
            {
                if (spectatorNameText != null) spectatorNameText.text = "관전 종료";
                ExitSpectator();
                yield break;
            }

            // 현재 대상이 사망했으면 다음으로 이동
            if (_currentIndex >= _targets.Count)
                _currentIndex = 0;

            FocusTarget(_currentIndex);
        }
    }
}
