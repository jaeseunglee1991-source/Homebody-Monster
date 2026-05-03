using UnityEngine;

/// <summary>
/// 게임의 전역 환경 설정(사운드, 프레임, 해상도)을 관리하고 기기에 적용합니다.
/// GameManager, SupabaseManager와 동일한 DontDestroyOnLoad 싱글톤 패턴을 사용합니다.
/// 앱 실행 시 저장된 설정을 불러와 즉시 적용합니다.
/// </summary>
public class SettingsManager : MonoBehaviour
{
    public static SettingsManager Instance { get; private set; }

    // 현재 적용된 설정값 (외부에서 읽기 전용으로 사용)
    public float BgmVolume      { get; private set; } = 1f;
    public float SfxVolume      { get; private set; } = 1f;
    public int   TargetFPS      { get; private set; } = 60;
    public bool  IsLowResolution { get; private set; } = false;

    // PlayerPrefs 키 (오타 방지용 상수)
    private const string KEY_BGM = "Setting_BGM";
    private const string KEY_SFX = "Setting_SFX";
    private const string KEY_FPS = "Setting_FPS";
    private const string KEY_RES = "Setting_LowRes";

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadAndApplySettings();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>저장된 설정을 불러오고 실제 기기에 적용합니다.</summary>
    public void LoadAndApplySettings()
    {
        BgmVolume       = PlayerPrefs.GetFloat(KEY_BGM, 1f);
        SfxVolume       = PlayerPrefs.GetFloat(KEY_SFX, 1f);
        TargetFPS       = PlayerPrefs.GetInt(KEY_FPS, 60);
        IsLowResolution = PlayerPrefs.GetInt(KEY_RES, 0) == 1;

        ApplyPerformanceSettings();
        AudioManager.Instance?.SetBgmVolume(BgmVolume);
        AudioManager.Instance?.SetSfxVolume(SfxVolume);
    }

    // ════════════════════════════════════════════════════════════
    //  성능 설정 적용 (핵심 로직)
    // ════════════════════════════════════════════════════════════

    /// <summary>프레임 제한과 해상도를 실제 안드로이드 기기에 적용합니다.</summary>
    private void ApplyPerformanceSettings()
    {
        // ── 1. 화면 꺼짐 방지 (모바일 게임 필수) ──
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        // ── 2. 프레임 제한 ──
        // 중요: vSyncCount = 0 으로 설정해야 targetFrameRate가 안드로이드에서 실제로 적용됩니다.
        // vSyncCount 기본값이 1인 경우 기기 주사율에 강제 동기화되어 30/60 설정이 무시될 수 있습니다.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = TargetFPS;

        // ── 3. 해상도 조절 ──
        // Screen.SetResolution의 bool 오버로드는 Unity 2022+ 에서 Deprecated 처리되었습니다.
        // FullScreenMode 열거형을 사용하는 버전으로 작성합니다.
        // 세로형(Portrait) 기기에서는 height > width 이므로 아래와 같이 계산합니다.
        float screenAspect = (float)Screen.height / Screen.width;

        if (IsLowResolution)
        {
            // 저사양 모드: 가로 해상도 720p (갤럭시 A 시리즈급 배터리 절약)
            int w = 720;
            int h = Mathf.RoundToInt(w * screenAspect);
            Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
        }
        else
        {
            // 일반 모드: 가로 해상도 1080p (현재 안드로이드 표준)
            int w = 1080;
            int h = Mathf.RoundToInt(w * screenAspect);
            Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  SettingsUIController 에서 호출하는 Setter
    // ════════════════════════════════════════════════════════════

    /// <summary>BGM 볼륨을 설정합니다 (0.0 ~ 1.0).</summary>
    public void SetBgmVolume(float volume)
    {
        BgmVolume = Mathf.Clamp01(volume);
        PlayerPrefs.SetFloat(KEY_BGM, BgmVolume);
        AudioManager.Instance?.SetBgmVolume(BgmVolume);
    }

    /// <summary>SFX 볼륨을 설정합니다 (0.0 ~ 1.0).</summary>
    public void SetSfxVolume(float volume)
    {
        SfxVolume = Mathf.Clamp01(volume);
        PlayerPrefs.SetFloat(KEY_SFX, SfxVolume);
        AudioManager.Instance?.SetSfxVolume(SfxVolume);
    }

    /// <summary>목표 프레임을 설정합니다. 권장값: 30, 60, 90.</summary>
    public void SetTargetFPS(int fps)
    {
        TargetFPS = fps;
        PlayerPrefs.SetInt(KEY_FPS, fps);
        // 해상도는 변경하지 않고 프레임만 재적용
        QualitySettings.vSyncCount  = 0;
        Application.targetFrameRate = TargetFPS;
    }

    /// <summary>저사양 모드를 설정합니다. true = 720p 저해상도 모드.</summary>
    public void SetLowResolution(bool isLowRes)
    {
        IsLowResolution = isLowRes;
        PlayerPrefs.SetInt(KEY_RES, isLowRes ? 1 : 0);
        ApplyPerformanceSettings();
    }

    /// <summary>모든 변경 내용을 디스크에 즉시 씁니다. 창 닫을 때 호출합니다.</summary>
    public void SaveAll()
    {
        PlayerPrefs.Save();
    }
}
