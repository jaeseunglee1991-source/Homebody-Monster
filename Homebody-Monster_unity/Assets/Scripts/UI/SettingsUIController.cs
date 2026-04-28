using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 설정 창 UI 패널에 붙여 사용합니다.
/// 화면이 켜질 때 현재 설정을 UI에 반영하고, 유저 조작을 SettingsManager로 전달합니다.
///
/// 유니티 Inspector 체크리스트:
///  - fps30Toggle / fps60Toggle / fps90Toggle 은 동일한 ToggleGroup 에 속해야 합니다.
///    (Toggle 컴포넌트의 Group 필드에 ToggleGroup 컴포넌트를 드래그하세요.)
///  - bgmSlider / sfxSlider 의 Min Value = 0, Max Value = 1 로 설정하세요.
/// </summary>
public class SettingsUIController : MonoBehaviour
{
    [Header("UI 패널")]
    public GameObject settingsPanel;
    public Button     openButton;   // 톱니바퀴 버튼 (Inspector에서 OnClick → OpenSettings 연결)
    public Button     closeButton;

    [Header("사운드 설정 (Slider, Min:0 Max:1)")]
    public Slider bgmSlider;
    public Slider sfxSlider;

    [Header("프레임 설정 (Toggle — 반드시 동일 ToggleGroup에 배치)")]
    public Toggle fps30Toggle;
    public Toggle fps60Toggle;
    public Toggle fps90Toggle;   // 고사양 폰 대응 (갤럭시 S/플래그십 라인)

    [Header("해상도 설정")]
    public Toggle lowResolutionToggle; // 체크 시 720p 저사양 모드

    private void Awake()
    {
        // 패널은 기본 비활성
        if (settingsPanel != null) settingsPanel.SetActive(false);
    }

    private void Start()
    {
        // 버튼 이벤트 연결 (Inspector에서 해도 무방하나, 코드로 보장)
        // [Fix #7] openButton Inspector 연결이 누락되어도 코드에서 보장
        openButton?.onClick.AddListener(OpenSettings);
        closeButton?.onClick.AddListener(CloseSettings);

        // 슬라이더·토글 이벤트 연결
        bgmSlider?.onValueChanged.AddListener(OnBgmChanged);
        sfxSlider?.onValueChanged.AddListener(OnSfxChanged);

        // 프레임 토글: isOn=true 일 때만 처리 (false 이벤트 중복 방지)
        fps30Toggle?.onValueChanged.AddListener(isOn => { if (isOn) OnFpsChanged(30); });
        fps60Toggle?.onValueChanged.AddListener(isOn => { if (isOn) OnFpsChanged(60); });
        fps90Toggle?.onValueChanged.AddListener(isOn => { if (isOn) OnFpsChanged(90); });

        lowResolutionToggle?.onValueChanged.AddListener(OnResolutionChanged);
    }

    // ════════════════════════════════════════════════════════════
    //  패널 열기 / 닫기
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 설정 창을 엽니다.
    /// 로비·인게임 씬의 설정(톱니바퀴) 버튼 OnClick() 에 연결하세요.
    /// </summary>
    public void OpenSettings()
    {
        if (settingsPanel != null) settingsPanel.SetActive(true);
        SyncUIFromManager();
    }

    public void CloseSettings()
    {
        if (settingsPanel != null) settingsPanel.SetActive(false);
        SettingsManager.Instance?.SaveAll();
    }

    // ════════════════════════════════════════════════════════════
    //  UI 동기화 (중요: SetValueWithoutNotify 사용)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// SettingsManager의 현재 값을 UI에 반영합니다.
    /// SetValueWithoutNotify 를 사용해 onValueChanged 콜백이 다시 호출되지 않도록 합니다.
    /// 이 처리가 없으면 창을 열 때마다 불필요한 PlayerPrefs.SetFloat 가 호출됩니다.
    /// </summary>
    private void SyncUIFromManager()
    {
        var sm = SettingsManager.Instance;
        if (sm == null) return;

        // 슬라이더: SetValueWithoutNotify 로 이벤트 없이 값만 반영
        bgmSlider?.SetValueWithoutNotify(sm.BgmVolume);
        sfxSlider?.SetValueWithoutNotify(sm.SfxVolume);

        // 토글: SetIsOnWithoutNotify 로 이벤트 없이 값만 반영
        fps30Toggle?.SetIsOnWithoutNotify(sm.TargetFPS == 30);
        fps60Toggle?.SetIsOnWithoutNotify(sm.TargetFPS == 60);
        fps90Toggle?.SetIsOnWithoutNotify(sm.TargetFPS == 90);
        lowResolutionToggle?.SetIsOnWithoutNotify(sm.IsLowResolution);
    }

    // ════════════════════════════════════════════════════════════
    //  UI 이벤트 콜백 (유저가 실제로 조작할 때만 호출됨)
    // ════════════════════════════════════════════════════════════

    private void OnBgmChanged(float val)        => SettingsManager.Instance?.SetBgmVolume(val);
    private void OnSfxChanged(float val)        => SettingsManager.Instance?.SetSfxVolume(val);
    private void OnFpsChanged(int fps)          => SettingsManager.Instance?.SetTargetFPS(fps);
    private void OnResolutionChanged(bool isLow)=> SettingsManager.Instance?.SetLowResolution(isLow);
}
