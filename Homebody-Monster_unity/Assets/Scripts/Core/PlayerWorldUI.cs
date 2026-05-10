using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 인게임 캐릭터 머리 위 월드-스페이스 UI 컨트롤러.
///
/// 표시 규칙
///  • 닉네임          : 모든 플레이어에게 보임
///  • 직업·상성·등급 : 모든 플레이어에게 보임 (NetworkVariable 동기화)
///  • HP 바          : 본인(Owner)에게만 보임 — 적의 체력 정보 노출 차단
///
/// 사용법
///  PlayerCharacter 프리팹에 이 컴포넌트를 추가하기만 하면 런타임에 Canvas/Slider/TMP를
///  자동 생성합니다. 별도 Inspector 세팅 불필요.
///  (이미 자식 Canvas / NicknameText / WorldHpBar 가 있다면 재사용)
///
/// NetworkBehaviour 가 아닌 일반 MonoBehaviour 로 두는 이유:
///   런타임에 새 NetworkBehaviour 를 추가하면 NGO 의 컴포넌트 인덱스가 호스트/클라이언트 사이에서
///   어긋나 직렬화가 깨집니다. PlayerNetworkSync 가 보유한 NetworkVariable 만 외부에서 구독합니다.
/// </summary>
[RequireComponent(typeof(PlayerNetworkSync))]
public class PlayerWorldUI : MonoBehaviour
{
    [Header("표시 위치")]
    [Tooltip("캐릭터 중심으로부터의 UI 오프셋(월드 단위).")]
    public Vector3 anchorOffset = new Vector3(0f, 0.9f, 0f);

    [Header("스타일")]
    public float canvasScale = 0.01f;
    public Color hpBarColor = new Color(0.85f, 0.25f, 0.25f, 1f);
    public Color infoColor  = new Color(1f, 0.92f, 0.55f, 1f);
    public Color nameColor  = Color.white;

    private PlayerNetworkSync _sync;
    private Canvas            _canvas;
    private Slider            _hpBar;
    private Image             _hpFill;
    private TextMeshProUGUI   _nameText;
    private TextMeshProUGUI   _infoText;
    private bool              _subscribed;

    private void Awake()
    {
        _sync = GetComponent<PlayerNetworkSync>();
    }

    private void OnEnable()
    {
        StartCoroutine(WaitForSpawnAndSetup());
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private IEnumerator WaitForSpawnAndSetup()
    {
        // PlayerNetworkSync.OnNetworkSpawn 이 끝나야 IsSpawned == true.
        while (_sync != null && !_sync.IsSpawned) yield return null;
        if (_sync == null) yield break;

        BuildOrFindUI();
        Subscribe();
        ApplyInitialValues();
        ApplyOwnerVisibility();
    }

    private void LateUpdate()
    {
        // 월드-스페이스 캔버스를 카메라를 향하게 (빌보드).
        if (_canvas == null) return;
        var cam = Camera.main;
        if (cam != null)
            _canvas.transform.rotation = cam.transform.rotation;
    }

    // ─────────────────────────────────────────────────────────────
    //  NetworkVariable 구독
    // ─────────────────────────────────────────────────────────────
    private void Subscribe()
    {
        if (_subscribed || _sync == null) return;
        _sync.NetworkHp.OnValueChanged       += HandleHpChanged;
        _sync.NetworkMaxHp.OnValueChanged    += HandleMaxHpChanged;
        _sync.NetworkJob.OnValueChanged      += HandleJobChanged;
        _sync.NetworkAffinity.OnValueChanged += HandleAffinityChanged;
        _sync.NetworkGrade.OnValueChanged    += HandleGradeChanged;
        _sync.NetworkNickname.OnValueChanged += HandleNicknameChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _sync == null) { _subscribed = false; return; }
        _sync.NetworkHp.OnValueChanged       -= HandleHpChanged;
        _sync.NetworkMaxHp.OnValueChanged    -= HandleMaxHpChanged;
        _sync.NetworkJob.OnValueChanged      -= HandleJobChanged;
        _sync.NetworkAffinity.OnValueChanged -= HandleAffinityChanged;
        _sync.NetworkGrade.OnValueChanged    -= HandleGradeChanged;
        _sync.NetworkNickname.OnValueChanged -= HandleNicknameChanged;
        _subscribed = false;
    }

    // ─────────────────────────────────────────────────────────────
    //  UI 생성 / 검색
    // ─────────────────────────────────────────────────────────────
    private void BuildOrFindUI()
    {
        _canvas = GetComponentInChildren<Canvas>(true);
        if (_canvas == null)
        {
            var canvasGo = new GameObject("[WorldUI]");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.transform.localPosition = anchorOffset;
            canvasGo.transform.localScale    = Vector3.one * canvasScale;
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.WorldSpace;
            _canvas.sortingOrder = 100;
            canvasGo.AddComponent<CanvasScaler>();
            var rect = _canvas.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(180f, 90f);
        }

        _nameText = FindOrCreateText(_canvas.transform, "NicknameText", new Vector2(0f, 35f),  22, nameColor, FontStyles.Bold);
        _infoText = FindOrCreateText(_canvas.transform, "InfoText",     new Vector2(0f, 12f),  16, infoColor, FontStyles.Normal);
        _hpBar    = FindOrCreateHpBar(_canvas.transform, new Vector2(0f, -10f));
    }

    private static TextMeshProUGUI FindOrCreateText(Transform parent, string name, Vector2 anchoredPos, float fontSize, Color color, FontStyles style)
    {
        var existing = parent.Find(name);
        if (existing != null)
        {
            var tmp = existing.GetComponent<TextMeshProUGUI>();
            if (tmp != null) return tmp;
        }

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.alignment      = TextAlignmentOptions.Center;
        t.fontSize       = fontSize;
        t.color          = color;
        t.fontStyle      = style;
        t.text           = "";
        t.raycastTarget  = false;

        var rect = t.rectTransform;
        rect.sizeDelta        = new Vector2(180f, fontSize + 8f);
        rect.anchoredPosition = anchoredPos;
        return t;
    }

    private Slider FindOrCreateHpBar(Transform parent, Vector2 anchoredPos)
    {
        var existing = parent.Find("WorldHpBar");
        if (existing != null)
        {
            var existingSlider = existing.GetComponent<Slider>();
            if (existingSlider != null)
            {
                _hpFill = existing.GetComponentInChildren<Image>();
                return existingSlider;
            }
        }

        var sliderGo = new GameObject("WorldHpBar");
        sliderGo.transform.SetParent(parent, false);
        var sliderRect = sliderGo.AddComponent<RectTransform>();
        sliderRect.sizeDelta        = new Vector2(120f, 10f);
        sliderRect.anchoredPosition = anchoredPos;

        var slider = sliderGo.AddComponent<Slider>();
        slider.minValue     = 0f;
        slider.maxValue     = 1f;
        slider.value        = 1f;
        slider.transition   = Selectable.Transition.None;
        slider.interactable = false;

        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(sliderGo.transform, false);
        var bgRect = bgGo.AddComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color         = new Color(0f, 0f, 0f, 0.6f);
        bgImg.raycastTarget = false;

        var fillAreaGo = new GameObject("Fill Area");
        fillAreaGo.transform.SetParent(sliderGo.transform, false);
        var fillAreaRect = fillAreaGo.AddComponent<RectTransform>();
        fillAreaRect.anchorMin = Vector2.zero;
        fillAreaRect.anchorMax = Vector2.one;
        fillAreaRect.offsetMin = new Vector2(2f, 2f);
        fillAreaRect.offsetMax = new Vector2(-2f, -2f);

        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(fillAreaGo.transform, false);
        var fillRect = fillGo.AddComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        _hpFill = fillGo.AddComponent<Image>();
        _hpFill.color         = hpBarColor;
        _hpFill.raycastTarget = false;

        slider.targetGraphic = bgImg;
        slider.fillRect      = fillRect;
        slider.direction     = Slider.Direction.LeftToRight;

        return slider;
    }

    // ─────────────────────────────────────────────────────────────
    //  값 적용
    // ─────────────────────────────────────────────────────────────
    private void ApplyInitialValues()
    {
        if (_nameText != null && !_sync.NetworkNickname.Value.IsEmpty)
            _nameText.text = _sync.NetworkNickname.Value.ToString();

        UpdateInfoText();
        UpdateHpBar();
    }

    private void ApplyOwnerVisibility()
    {
        // 요구사항 #1: HP 바는 본인에게만 노출. 다른 플레이어 화면에서는 숨긴다.
        if (_hpBar != null)
            _hpBar.gameObject.SetActive(_sync.IsOwner);
    }

    private void HandleHpChanged(float prev, float curr)    => UpdateHpBar();
    private void HandleMaxHpChanged(float prev, float curr) => UpdateHpBar();
    private void HandleJobChanged(int prev, int curr)       => UpdateInfoText();
    private void HandleAffinityChanged(int prev, int curr)  => UpdateInfoText();
    private void HandleGradeChanged(int prev, int curr)     => UpdateInfoText();

    private void HandleNicknameChanged(Unity.Collections.FixedString64Bytes prev,
                                       Unity.Collections.FixedString64Bytes curr)
    {
        if (_nameText != null) _nameText.text = curr.ToString();
    }

    private void UpdateHpBar()
    {
        if (_hpBar == null || _sync == null || !_sync.IsOwner) return;
        float max  = _sync.NetworkMaxHp.Value;
        float curr = _sync.NetworkHp.Value;
        _hpBar.value = max > 0f ? curr / max : 0f;
    }

    private void UpdateInfoText()
    {
        if (_infoText == null) return;
        int j = _sync.NetworkJob.Value;
        int a = _sync.NetworkAffinity.Value;
        int g = _sync.NetworkGrade.Value;
        if (j < 0 || a < 0 || g < 0) { _infoText.text = ""; return; }
        _infoText.text = CharacterLabels.FormatJobAffinityGrade(
            (JobType)j, (AffinityType)a, (GradeTier)g);
    }
}
