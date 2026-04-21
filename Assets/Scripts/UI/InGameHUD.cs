using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 인게임 HUD를 관리합니다.
/// </summary>
public class InGameHUD : MonoBehaviour
{
    public static InGameHUD Instance { get; private set; }

    [Header("체력 UI")]
    public Slider healthSlider;
    public Text   healthText;

    [Header("생존자 UI")]
    public Text survivorCountText;

    [Header("스킬 버튼")]
    public Button[]   skillButtons;       
    public Image[]    skillCooldownFills; 
    public Text[]     skillNameTexts;     

    [Header("타이머")]
    public Text timerText;

    [Header("게임 종료 배너")]
    public GameObject endBannerPanel;
    public Text       endBannerText;

    private PlayerController localPlayer;
    private float[] skillCooldownTimers; 
    private float[] skillMaxCooldowns;   

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        if (endBannerPanel != null) endBannerPanel.SetActive(false);
    }

    private void Start()
    {
        StartCoroutine(FindLocalPlayerRoutine());
    }

    private IEnumerator FindLocalPlayerRoutine()
    {
        while (localPlayer == null)
        {
            foreach (var p in FindObjectsOfType<PlayerController>())
            {
                if (p.IsLocalPlayer) { localPlayer = p; break; }
            }
            yield return new WaitForSeconds(0.2f);
        }
        SetupSkillButtons(localPlayer);
    }

    private void Update()
    {
        UpdateCooldownUI();
    }

    public void UpdateHealthBar(float current, float max)
    {
        if (healthSlider != null)
            healthSlider.value = max > 0 ? current / max : 0f;

        if (healthText != null)
            healthText.text = $"{current:0.#} / {max:0.#}";
    }

    public void UpdateSurvivorCount(int alive, int total)
    {
        if (survivorCountText != null)
            survivorCountText.text = $"생존자: {alive} / {total}";
    }

    public void UpdateTimer(float elapsed)
    {
        if (timerText == null) return;
        int minutes = (int)(elapsed / 60f);
        int seconds = (int)(elapsed % 60f);
        timerText.text = $"{minutes:00}:{seconds:00}";
    }

    public void ShowGameEndBanner(string message)
    {
        if (endBannerPanel != null) endBannerPanel.SetActive(true);
        if (endBannerText  != null) endBannerText.text = message;
    }

    private void SetupSkillButtons(PlayerController player)
    {
        if (player.myData == null) return;

        List<SkillType> skills = player.myData.skills;
        int count = Mathf.Min(skills.Count, skillButtons?.Length ?? 0);

        skillCooldownTimers  = new float[count];
        skillMaxCooldowns    = new float[count];

        for (int i = 0; i < skillButtons.Length; i++)
        {
            if (skillButtons[i] == null) continue;

            if (i < count)
            {
                SkillType skill = skills[i];
                int capturedIndex = i;

                skillButtons[i].gameObject.SetActive(true);
                skillButtons[i].onClick.RemoveAllListeners();
                skillButtons[i].onClick.AddListener(() => OnSkillButtonClicked(capturedIndex));

                if (skillNameTexts != null && i < skillNameTexts.Length && skillNameTexts[i] != null)
                    skillNameTexts[i].text = skill.ToString();

                float cd = SkillSystem.GetCooldown(skill);
                skillMaxCooldowns[i]   = cd;
                skillCooldownTimers[i] = 0f;

                if (skillCooldownFills != null && i < skillCooldownFills.Length && skillCooldownFills[i] != null)
                    skillCooldownFills[i].fillAmount = 0f;
            }
            else
            {
                skillButtons[i].gameObject.SetActive(false);
            }
        }
    }

    private void OnSkillButtonClicked(int slotIndex)
    {
        if (localPlayer == null || skillCooldownTimers == null) return;
        if (slotIndex >= skillCooldownTimers.Length) return;
        if (skillCooldownTimers[slotIndex] > 0f) return;

        localPlayer.UseSkill(slotIndex);
        skillCooldownTimers[slotIndex] = skillMaxCooldowns[slotIndex];
    }

    private void UpdateCooldownUI()
    {
        if (skillCooldownTimers == null) return;

        for (int i = 0; i < skillCooldownTimers.Length; i++)
        {
            if (skillCooldownTimers[i] > 0f)
            {
                skillCooldownTimers[i] -= Time.deltaTime;
                skillCooldownTimers[i] = Mathf.Max(0f, skillCooldownTimers[i]);
            }

            if (skillCooldownFills != null && i < skillCooldownFills.Length && skillCooldownFills[i] != null)
            {
                float fillAmount = skillMaxCooldowns[i] > 0f ? skillCooldownTimers[i] / skillMaxCooldowns[i] : 0f;
                skillCooldownFills[i].fillAmount = fillAmount;
            }

            if (skillButtons != null && i < skillButtons.Length && skillButtons[i] != null)
                skillButtons[i].interactable = (skillCooldownTimers[i] <= 0f);
        }
    }
}
