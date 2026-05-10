using UnityEngine;

/// <summary>
/// InGameScene 의 재접속 UI 를 DontDestroyOnLoad 싱글톤인 ReconnectManager 에 주입합니다.
///
/// 이유:
///   ReconnectManager 는 LoginScene 에서 1회 생성된 DDOL 오브젝트이므로 Inspector 참조를
///   InGameScene 의 UI(Panel/Slider/Text)와 직접 연결할 수 없습니다(씬 경계).
///   본 컴포넌트를 InGameScene 에 배치하고 자기 씬의 UI 를 Inspector 에서 연결하면,
///   씬 진입 시 ReconnectManager.Instance 의 필드에 자동 주입됩니다.
///
/// 배치:
///   InGameScene 에 빈 GameObject(예: [ReconnectUIBinder]) 하나 만들고 이 컴포넌트 부착.
///   reconnectPanel / progressSlider / statusText 를 Inspector 에서 연결.
/// </summary>
public class ReconnectUIBinder : MonoBehaviour
{
    [Header("InGameScene 의 재접속 UI")]
    public GameObject               reconnectPanel;
    public UnityEngine.UI.Slider    progressSlider;
    public TMPro.TextMeshProUGUI    statusText;

    private void Start()
    {
        if (ReconnectManager.Instance == null)
        {
            Debug.LogWarning("[ReconnectUIBinder] ReconnectManager.Instance 가 아직 없습니다. " +
                             "LoginScene → InGameScene 흐름이 정상이라면 발생하지 않습니다.");
            return;
        }

        var rm = ReconnectManager.Instance;
        if (reconnectPanel != null) rm.reconnectPanel = reconnectPanel;
        if (progressSlider != null) rm.progressSlider = progressSlider;
        if (statusText     != null) rm.statusText     = statusText;

        // 패널은 기본 비활성 — 재접속 시작 시 ReconnectManager 가 활성화합니다.
        if (reconnectPanel != null) reconnectPanel.SetActive(false);
    }

    private void OnDestroy()
    {
        // 씬 이탈 시 dangling 참조 제거 — 다음 씬에서 잘못된 UI 가 잡히지 않도록.
        if (ReconnectManager.Instance == null) return;
        var rm = ReconnectManager.Instance;
        if (rm.reconnectPanel == reconnectPanel) rm.reconnectPanel = null;
        if (rm.progressSlider == progressSlider) rm.progressSlider = null;
        if (rm.statusText     == statusText)     rm.statusText     = null;
    }
}
