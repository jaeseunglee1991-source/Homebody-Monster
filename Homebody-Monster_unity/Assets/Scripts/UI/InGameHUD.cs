using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

public class InGameHUD : MonoBehaviour
{
    public static InGameHUD Instance { get; private set; }

    [Header("체력 UI")] public Slider healthSlider; public Text healthText;
    [Header("생존자 UI")] public Text survivorCountText;
    [Header("스킬 버튼 (최대 4개)")]
    public Button[]   skillButtons;
    public Image[]    skillCooldownFills;
    public Text[]     skillNameTexts;
    [Header("타이머")] public Text timerText;
    [Header("게임 종료 배너")] public GameObject endBannerPanel; public Text endBannerText;

    private PlayerController localPlayer;
    private float[] cooldownTimers;
    private float[] cooldownMaxes;

    private void Awake()
    {
        if (Instance == null) Instance = this; else { Destroy(gameObject); return; }
        if (endBannerPanel != null) endBannerPanel.SetActive(false);
    }

    /// <summary>
    /// PlayerController.Start()에서 로컬 플레이어 스폰 직후 호출합니다.
    /// 코루틴 polling 없이 즉시 UI를 세팅하므로 타이밍 불일치 버그가 없습니다.
    /// </summary>
    public void InitPlayerUI(PlayerController player)
    {
        localPlayer = player;
        SetupSkillButtons(player);
    }

    private void Update() => UpdateCooldownUI();

    private void SetupSkillButtons(PlayerController player)
    {
        if (player?.myData == null) return;
        List<ActiveSkillType> skills = player.myData.activeSkills;
        int count = Mathf.Min(skills.Count, skillButtons?.Length ?? 0);
        cooldownTimers = new float[skillButtons.Length];
        cooldownMaxes  = new float[skillButtons.Length];

        for (int i = 0; i < skillButtons.Length; i++)
        {
            if (skillButtons[i] == null) continue;
            if (i < count)
            {
                ActiveSkillType skill = skills[i];
                int captured = i;
                skillButtons[i].gameObject.SetActive(true);
                skillButtons[i].onClick.RemoveAllListeners();
                skillButtons[i].onClick.AddListener(() => OnSkillClicked(captured));
                if (skillNameTexts != null && i < skillNameTexts.Length && skillNameTexts[i] != null)
                    skillNameTexts[i].text = GetSkillShortName(skill);
                float cd = SkillSystem.GetCooldown(skill);
                cooldownMaxes[i] = cd; cooldownTimers[i] = 0f;
                if (skillCooldownFills != null && i < skillCooldownFills.Length && skillCooldownFills[i] != null)
                    skillCooldownFills[i].fillAmount = 0f;
            }
            else { skillButtons[i].gameObject.SetActive(false); }
        }
    }

    private void OnSkillClicked(int slot)
    {
        if (localPlayer == null || cooldownTimers == null || slot >= cooldownTimers.Length || cooldownTimers[slot] > 0f) return;
        localPlayer.UseSkill(slot);
        cooldownTimers[slot] = cooldownMaxes[slot];
    }

    private void UpdateCooldownUI()
    {
        if (cooldownTimers == null) return;
        for (int i = 0; i < cooldownTimers.Length; i++)
        {
            if (cooldownTimers[i] > 0f) cooldownTimers[i] = Mathf.Max(0f, cooldownTimers[i] - Time.deltaTime);
            if (skillCooldownFills != null && i < skillCooldownFills.Length && skillCooldownFills[i] != null)
                skillCooldownFills[i].fillAmount = cooldownMaxes[i] > 0f ? cooldownTimers[i] / cooldownMaxes[i] : 0f;
            if (skillButtons != null && i < skillButtons.Length && skillButtons[i] != null)
                skillButtons[i].interactable = cooldownTimers[i] <= 0f;
        }
    }

    public void UpdateHealthBar(float current, float max)
    {
        if (healthSlider != null) healthSlider.value = max > 0 ? current / max : 0f;
        if (healthText   != null) healthText.text    = $"{current:0.#} / {max:0.#}";
    }

    public void UpdateSurvivorCount(int alive, int total)
    { if (survivorCountText != null) survivorCountText.text = $"생존자: {alive} / {total}"; }

    public void UpdateTimer(float elapsed)
    {
        if (timerText == null) return;
        timerText.text = $"{(int)(elapsed/60f):00}:{(int)(elapsed%60f):00}";
    }

    public void ShowGameEndBanner(string message)
    { if (endBannerPanel != null) endBannerPanel.SetActive(true); if (endBannerText != null) endBannerText.text = message; }

    private static string GetSkillShortName(ActiveSkillType skill) => skill switch
    {
        ActiveSkillType.Sweep => "휩쓸기", ActiveSkillType.ChargeStrike => "돌진강타",
        ActiveSkillType.DefenseStance => "방어태세", ActiveSkillType.EarthquakeStrike => "지진강타",
        ActiveSkillType.ShieldBash => "방패강타", ActiveSkillType.Shockwave => "지진파",
        ActiveSkillType.IronSkin => "강철피부", ActiveSkillType.Bulldozer => "불도저",
        ActiveSkillType.HolyStrike => "신성타격", ActiveSkillType.JudgmentHammer => "심판망치",
        ActiveSkillType.DivineGrace => "신의가호", ActiveSkillType.PillarOfJudgment => "심판기둥",
        ActiveSkillType.RuthlessStrike => "무자비", ActiveSkillType.BleedSlash => "출혈베기",
        ActiveSkillType.UndyingRage => "불굴분노", ActiveSkillType.BladeStorm => "칼날폭풍",
        ActiveSkillType.Fireball => "화염구", ActiveSkillType.IceShards => "얼음파편",
        ActiveSkillType.IceShield => "얼음방패", ActiveSkillType.Meteor => "메테오",
        ActiveSkillType.PierceArrow => "관통화살", ActiveSkillType.MultiShot => "다중사격",
        ActiveSkillType.Trap => "덫놓기", ActiveSkillType.ArrowRain => "화살폭우",
        ActiveSkillType.Smite => "징벌", ActiveSkillType.HolyExplosion => "신성폭발",
        ActiveSkillType.HealingLight => "치유빛", ActiveSkillType.GuardianAngel => "수호천사",
        ActiveSkillType.PoisonDagger => "독단검", ActiveSkillType.Ambush => "기습",
        ActiveSkillType.SmokeBomb => "연막탄", ActiveSkillType.ShadowRaid => "그림자습격",
        ActiveSkillType.VitalStrike => "급소찌르기", ActiveSkillType.Shuriken => "표창",
        ActiveSkillType.StealthSkill => "은신", ActiveSkillType.DeathMark => "죽음표식",
        ActiveSkillType.FryingPan => "프라이팬", ActiveSkillType.BurningOil => "불타는기름",
        ActiveSkillType.SnackTime => "간식타임", ActiveSkillType.FeastTime => "만찬시간",
        _ => skill.ToString()
    };
}