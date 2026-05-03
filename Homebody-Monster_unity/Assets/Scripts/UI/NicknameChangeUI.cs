using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Text.RegularExpressions;

/// <summary>
/// 닉네임 변경 팝업 UI.
/// 기존 SupabaseManager.IsNicknameAvailable() / UpdateNickname()을 그대로 사용합니다.
/// </summary>
public class NicknameChangeUI : MonoBehaviour
{
    public static NicknameChangeUI Instance { get; private set; }

    [Header("팝업 UI")]
    public GameObject      nicknamePanel;
    public TMP_InputField  inputField;
    public TextMeshProUGUI charCountText;
    public Button          checkButton;
    public Button          confirmButton;
    public Button          cancelButton;
    public Button          openButton;
    public TextMeshProUGUI statusText;

    [Header("색상")]
    public Color colorOk      = new Color(0.2f, 0.8f, 0.2f);
    public Color colorError   = new Color(0.9f, 0.2f, 0.2f);
    public Color colorNeutral = Color.white;

    // 첫 글자 한글·영문, 이후 한글·영문·숫자·_ (총 2~12자)
    private static readonly Regex NicknameRegex = new Regex(
        @"^[가-힣a-zA-Z][가-힣a-zA-Z0-9_]{1,11}$",
        RegexOptions.Compiled
    );

    private static readonly string[] ForbiddenWords =
    {
        "씨발","시발","씨팔","시팔","씨빨","시빨","쓰벌","ㅅㅂ",
        "개새끼","개새","개년","개놈","개쓰레기",
        "병신","벙신","ㅂㅅ",
        "보지","자지",
        "애미","애비","니애미","니애비",
        "창녀","창놈","걸레년",
        "미친놈","미친년","미친새끼",
        "꺼져","죽어","뒤져",
        "운영자","관리자","운영진","admin","gm","master","system",
        "fuck","fuk","fck","shit","bitch","asshole","bastard","cunt",
        "nigger","nigga"
    };

    private bool   _isAvailable = false;
    private string _checkedName = null;
    private bool   _isBusy      = false;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        nicknamePanel?.SetActive(false);
    }

    private void Start()
    {
        openButton?.onClick.AddListener(OpenPanel);
        cancelButton?.onClick.AddListener(ClosePanel);
        checkButton?.onClick.AddListener(OnClickCheck);
        confirmButton?.onClick.AddListener(OnClickConfirm);

        if (inputField != null)
        {
            inputField.characterLimit = 12;
            inputField.onValueChanged.AddListener(OnInputChanged);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    public void OpenPanel()
    {
        nicknamePanel?.SetActive(true);
        ResetState();

        string current = GameManager.Instance?.currentPlayerNickname ?? "";
        if (inputField != null)
        {
            inputField.text = current;
            inputField.ActivateInputField();
        }
        OnInputChanged(current);

        AudioManager.Instance?.PlayButtonClick();
    }

    public void ClosePanel()
    {
        nicknamePanel?.SetActive(false);
        AudioManager.Instance?.PlayButtonClick();
    }

    // ════════════════════════════════════════════════════════════
    //  입력 실시간 검사
    // ════════════════════════════════════════════════════════════

    private void OnInputChanged(string value)
    {
        if (charCountText != null)
            charCountText.text = $"{value.Length} / 12";

        _isAvailable = false;
        _checkedName = null;
        if (confirmButton != null) confirmButton.interactable = false;

        if (string.IsNullOrEmpty(value))
        {
            SetStatus("닉네임을 입력하세요.", colorNeutral);
            if (checkButton != null) checkButton.interactable = false;
            return;
        }

        string err = GetValidationError(value);
        if (err != null)
        {
            SetStatus(err, colorError);
            if (checkButton != null) checkButton.interactable = false;
        }
        else
        {
            SetStatus("중복 확인을 눌러주세요.", colorNeutral);
            if (checkButton != null) checkButton.interactable = true;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  중복 확인
    // ════════════════════════════════════════════════════════════

    private async void OnClickCheck()
    {
        if (_isBusy) return;

        string newName = inputField?.text?.Trim();
        string err = GetValidationError(newName);
        if (err != null) { SetStatus(err, colorError); return; }

        if (newName == GameManager.Instance?.currentPlayerNickname)
        {
            SetStatus("현재 사용 중인 닉네임입니다.", colorError);
            return;
        }

        _isBusy = true;
        SetAllButtonsInteractable(false);
        SetStatus("확인 중...", colorNeutral);

        bool available = false;
        try
        {
            available = await SupabaseManager.Instance.IsNicknameAvailable(newName);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[NicknameUI] 중복 확인 오류: {e.Message}");
            SetStatus("서버 오류가 발생했습니다. 다시 시도해주세요.", colorError);
            _isBusy = false;
            SetAllButtonsInteractable(true);
            return;
        }

        _isBusy = false;
        SetAllButtonsInteractable(true);

        if (available)
        {
            _isAvailable = true;
            _checkedName = newName;
            SetStatus("✅ 사용 가능한 닉네임입니다!", colorOk);
            if (confirmButton != null) confirmButton.interactable = true;
        }
        else
        {
            _isAvailable = false;
            _checkedName = null;
            SetStatus("❌ 이미 사용 중인 닉네임입니다.", colorError);
            if (confirmButton != null) confirmButton.interactable = false;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  닉네임 변경 확정
    // ════════════════════════════════════════════════════════════

    private async void OnClickConfirm()
    {
        if (_isBusy || !_isAvailable || string.IsNullOrEmpty(_checkedName)) return;

        if (inputField?.text?.Trim() != _checkedName)
        {
            SetStatus("닉네임이 변경되었습니다. 중복 확인을 다시 해주세요.", colorError);
            _isAvailable = false;
            _checkedName = null;
            if (confirmButton != null) confirmButton.interactable = false;
            return;
        }

        _isBusy = true;
        SetAllButtonsInteractable(false);
        SetStatus("변경 중...", colorNeutral);

        bool ok = false;
        try
        {
            ok = await SupabaseManager.Instance.UpdateNickname(_checkedName);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[NicknameUI] 닉네임 변경 오류: {e.Message}");
            SetStatus("서버 오류가 발생했습니다.", colorError);
            _isBusy = false;
            SetAllButtonsInteractable(true);
            return;
        }

        _isBusy = false;
        SetAllButtonsInteractable(true);

        if (ok)
        {
            if (GameManager.Instance != null)
                GameManager.Instance.currentPlayerNickname = _checkedName;

            AppNetworkManager.Instance?.TrackLobbyPresence(_checkedName);
            FindFirstObjectByType<LobbyUIController>()?.RefreshUserProfileUI();

            SetStatus("✅ 닉네임이 변경되었습니다!", colorOk);
            AudioManager.Instance?.PlayButtonClick();

            StartCoroutine(AutoClose(1.5f));
        }
        else
        {
            SetStatus("❌ 변경에 실패했습니다. 중복 확인을 다시 해주세요.", colorError);
            _isAvailable = false;
            _checkedName = null;
            if (confirmButton != null) confirmButton.interactable = false;
        }
    }

    private IEnumerator AutoClose(float delay)
    {
        yield return new WaitForSeconds(delay);
        ClosePanel();
    }

    // ════════════════════════════════════════════════════════════
    //  유효성 검사
    // ════════════════════════════════════════════════════════════

    private static string GetValidationError(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "닉네임을 입력하세요.";
        if (name.Length < 2)
            return "닉네임은 2자 이상이어야 합니다.";
        if (name.Length > 12)
            return "닉네임은 12자 이하여야 합니다.";
        if (!NicknameRegex.IsMatch(name))
            return "한글/영문으로 시작하고, 한글·영문·숫자·_만 사용 가능합니다.";

        string lower = name.ToLower();
        foreach (string bad in ForbiddenWords)
            if (lower.Contains(bad.ToLower()))
                return "사용할 수 없는 단어가 포함되어 있습니다.";

        return null;
    }

    // ════════════════════════════════════════════════════════════
    //  UI 헬퍼
    // ════════════════════════════════════════════════════════════

    private void ResetState()
    {
        _isAvailable = false;
        _checkedName = null;
        _isBusy      = false;

        if (inputField    != null) inputField.text = "";
        if (charCountText != null) charCountText.text = "0 / 12";

        SetStatus("새 닉네임을 입력해주세요.", colorNeutral);
        SetAllButtonsInteractable(true);
        if (checkButton   != null) checkButton.interactable   = false;
        if (confirmButton != null) confirmButton.interactable = false;
    }

    private void SetStatus(string msg, Color color)
    {
        if (statusText == null) return;
        statusText.text  = msg;
        statusText.color = color;
    }

    private void SetAllButtonsInteractable(bool v)
    {
        if (checkButton  != null) checkButton.interactable  = v && !_isAvailable;
        if (cancelButton != null) cancelButton.interactable = v;
        if (confirmButton != null) confirmButton.interactable = v && _isAvailable;
    }
}
