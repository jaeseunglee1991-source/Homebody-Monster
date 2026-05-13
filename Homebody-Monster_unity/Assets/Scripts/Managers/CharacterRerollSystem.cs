using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;

/// <summary>
/// 인게임 캐릭터 리롤 시스템.
///
/// ── 기능 개요 ─────────────────────────────────────────────────
///  · BeginGameClientRpc 직후(게임 시작 직전) 짧은 리롤 윈도우 제공
///  · 피자 20개 소비 → StatCalculator.GenerateRandomCharacter() 재실행
///  · 1매치 당 최초 1회만 가능 (MaxRerollsPerMatch = 1)
///  · 피자 부족 / 횟수 초과 / 윈도우 만료 시 버튼 비활성화
///  · 리롤 후 PlayerNetworkSync.ResubmitCharacterData()로 서버에 새 스탯 반영
///
/// ── Inspector 연결 ────────────────────────────────────────────
///  □ rerollPanel       : 리롤 패널 루트 GameObject
///  □ claimButton       : "리롤 🍕20" 버튼
///  □ skipButton        : "시작하기" 버튼 (패널 닫기)
///  □ pizzaCountText    : 현재 피자 수 표시 TMP
///  □ rerollCountText   : "리롤 가능: 1회 / 리롤 사용 완료" TMP
///  □ timerText         : "남은 시간: 12초" TMP
///  □ characterInfoText : 현재 캐릭터 직업·등급·스킬 요약 TMP
///  □ statusText        : 오류·안내 TMP
/// </summary>
public class CharacterRerollSystem : MonoBehaviour
{
    public static CharacterRerollSystem Instance { get; private set; }

    // ── 설정 ────────────────────────────────────────────────────
    public const int   RerollCostPizza    = 20;
    public const int   MaxRerollsPerMatch = 1;
    public const float RerollWindowSecs  = 15f;

    // ── Inspector ───────────────────────────────────────────────
    [Header("리롤 패널")]
    public GameObject rerollPanel;

    [Header("버튼")]
    public Button claimButton;
    public Button skipButton;

    [Header("텍스트")]
    public TextMeshProUGUI pizzaCountText;
    public TextMeshProUGUI rerollCountText;
    public TextMeshProUGUI timerText;
    public TextMeshProUGUI characterInfoText;
    public TextMeshProUGUI statusText;

    // ── 스킬 한국어 이름 매핑 ────────────────────────────────────
    private static readonly Dictionary<ActiveSkillType, string> SkillKoreanNames
        = new Dictionary<ActiveSkillType, string>
    {
        { ActiveSkillType.Sweep,            "휩쓸기"        },
        { ActiveSkillType.ChargeStrike,     "돌진 강타"     },
        { ActiveSkillType.DefenseStance,    "방어 태세"     },
        { ActiveSkillType.EarthquakeStrike, "지진 강타"     },
        { ActiveSkillType.ShieldBash,       "방패 강타"     },
        { ActiveSkillType.Shockwave,        "충격파"        },
        { ActiveSkillType.IronSkin,         "강철 피부"     },
        { ActiveSkillType.Bulldozer,        "불도저"        },
        { ActiveSkillType.HolyStrike,       "성스러운 강타" },
        { ActiveSkillType.JudgmentHammer,   "심판의 망치"   },
        { ActiveSkillType.DivineGrace,      "신성한 은총"   },
        { ActiveSkillType.PillarOfJudgment, "심판의 기둥"   },
        { ActiveSkillType.RuthlessStrike,   "무자비한 타격" },
        { ActiveSkillType.BleedSlash,       "출혈 베기"     },
        { ActiveSkillType.UndyingRage,      "불사의 분노"   },
        { ActiveSkillType.BladeStorm,       "칼날 폭풍"     },
        { ActiveSkillType.Fireball,         "화염구"        },
        { ActiveSkillType.IceShards,        "얼음 파편"     },
        { ActiveSkillType.IceShield,        "얼음 방패"     },
        { ActiveSkillType.Meteor,           "메테오"        },
        { ActiveSkillType.PierceArrow,      "관통 화살"     },
        { ActiveSkillType.MultiShot,        "다중 사격"     },
        { ActiveSkillType.Trap,             "함정"          },
        { ActiveSkillType.ArrowRain,        "화살 비"       },
        { ActiveSkillType.Smite,            "응징"          },
        { ActiveSkillType.HolyExplosion,    "성스러운 폭발" },
        { ActiveSkillType.HealingLight,     "치유의 빛"     },
        { ActiveSkillType.GuardianAngel,    "수호 천사"     },
    };

    private static string GetSkillName(ActiveSkillType skill)
        => SkillKoreanNames.TryGetValue(skill, out var name) ? name : skill.ToString();

    // ── 직업·등급·속성 한국어 이름 매핑 ─────────────────────────
    private static readonly Dictionary<JobType, string> JobKoreanNames
        = new Dictionary<JobType, string>
    {
        { JobType.Warrior,    "전사"   },
        { JobType.Tanker,     "탱커"   },
        { JobType.Paladin,    "성기사" },
        { JobType.Berserker,  "광전사" },
        { JobType.Mage,       "마법사" },
        { JobType.Archer,     "궁수"   },
        { JobType.Priest,     "성직자" },
        { JobType.Rogue,      "도적"   },
        { JobType.Assassin,   "암살자" },
        { JobType.Chef,       "셰프"   },
    };

    private static readonly Dictionary<GradeTier, string> GradeKoreanNames
        = new Dictionary<GradeTier, string>
    {
        { GradeTier.Normal,       "일반"   },
        { GradeTier.Advanced,     "고급"   },
        { GradeTier.Rare,         "희귀"   },
        { GradeTier.Ancient,      "고대"   },
        { GradeTier.Heroic,       "영웅"   },
        { GradeTier.Legendary,    "전설"   },
        { GradeTier.Mythic,       "신화"   },
        { GradeTier.Celestial,    "천상"   },
        { GradeTier.Transcendent, "초월"   },
        { GradeTier.Absolute,     "절대"   },
    };

    private static readonly Dictionary<AffinityType, string> AffinityKoreanNames
        = new Dictionary<AffinityType, string>
    {
        { AffinityType.Spicy,     "매운맛"   },
        { AffinityType.Greasy,    "느끼한맛" },
        { AffinityType.Fresh,     "신선한맛" },
        { AffinityType.Salty,     "짠맛"     },
        { AffinityType.Sweet,     "단맛"     },
        { AffinityType.MintChoco, "민트초코" },
        { AffinityType.Pineapple, "파인애플" },
    };

    private static string GetJobName(JobType job)
        => JobKoreanNames.TryGetValue(job, out var n) ? n : job.ToString();
    private static string GetGradeName(GradeTier grade)
        => GradeKoreanNames.TryGetValue(grade, out var n) ? n : grade.ToString();
    private static string GetAffinityName(AffinityType affinity)
        => AffinityKoreanNames.TryGetValue(affinity, out var n) ? n : affinity.ToString();

    // ── 내부 상태 ────────────────────────────────────────────────
    private int       _rerollsUsed;
    private bool      _isRolling;
    private bool      _hasOpenedThisMatch;
    private Coroutine _timerRoutine;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        if (rerollPanel != null) rerollPanel.SetActive(false);
    }

    private void Start()
    {
        claimButton?.onClick.AddListener(OnRerollClicked);
        skipButton?.onClick.AddListener(ClosePanel);
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 게임 시작 직전 리롤 윈도우를 엽니다.
    /// NetworkSpawnManager.BeginGameClientRpc() 내에서 호출됩니다.
    /// </summary>
    public void OpenRerollWindow()
    {
        // 데디케이티드 서버에서는 UI 불필요
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer
            && !NetworkManager.Singleton.IsHost)
            return;

        if (rerollPanel == null) return;

        // 매치 당 1회만 열 수 있도록 중복 호출 방지
        if (_hasOpenedThisMatch) return;
        _hasOpenedThisMatch = true;

        _rerollsUsed = 0;
        rerollPanel.SetActive(true);
        SetStatus("");
        RefreshUI();

        if (_timerRoutine != null) StopCoroutine(_timerRoutine);
        _timerRoutine = StartCoroutine(RerollWindowTimer());
    }

    /// <summary>패널을 닫습니다. skipButton 또는 타이머 만료 시 호출됩니다.</summary>
    public void ClosePanel()
    {
        if (_timerRoutine != null) { StopCoroutine(_timerRoutine); _timerRoutine = null; }
        if (rerollPanel != null) rerollPanel.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  리롤 실행
    // ════════════════════════════════════════════════════════════

    private async void OnRerollClicked()
    {
        // BUG-18: 데디케이티드 서버에서는 IsOwner=true PlayerNetworkSync가 없으므로 항상 실패하여
        // 무의미한 LogWarning이 발생. OpenRerollWindow와 동일하게 데디서버 early return.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer
            && !NetworkManager.Singleton.IsHost) return;

        if (_isRolling) return;

        if (_rerollsUsed >= MaxRerollsPerMatch)
        {
            SetStatus("이번 매치에서는 리롤을 이미 사용하셨습니다. (1회 제한)");
            return;
        }

        int localPizza = GameManager.Instance?.pizzaCount ?? 0;
        if (localPizza < RerollCostPizza)
        {
            SetStatus($"피자가 부족합니다. (필요: 🍕{RerollCostPizza}, 보유: 🍕{localPizza})");
            return;
        }

        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized)
        {
            SetStatus("⚠ 서버에 연결되지 않았습니다.");
            return;
        }

        _isRolling = true;
        if (claimButton != null) claimButton.interactable = false;
        SetStatus("처리 중...");

        // A-5: 피자 차감 전에 IsOwner PlayerNetworkSync가 실제로 존재하는지 먼저 검증.
        // 차감 후 sync 탐색 실패 시 "피자는 소모됐는데 리롤 효과는 없는" 결제 사기성 결함이 발생.
        PlayerNetworkSync localSync = null;
        foreach (var sync in FindObjectsByType<PlayerNetworkSync>(FindObjectsSortMode.None))
        {
            if (sync.IsOwner) { localSync = sync; break; }
        }
        if (localSync == null)
        {
            SetStatus("플레이어 정보를 찾을 수 없습니다. 잠시 후 다시 시도해주세요.");
            Debug.LogError("[Reroll] PlayerNetworkSync(IsOwner) 미존재 → 결제 차단 (피자 보존)");
            return;
        }

        // HIGH-03: 기존엔 try-finally가 없어 SpendPizzaForReroll/ResubmitCharacterData 등에서 예외 발생 시
        // _isRolling=true가 영구히 남아 같은 세션 내 리롤 버튼이 회복 불가능하게 잠기던 버그.
        try
        {
            bool success = await SupabaseManager.Instance.SpendPizzaForReroll();

            if (this == null) return;

            if (!success)
            {
                SetStatus($"피자가 부족합니다. (필요: 🍕{RerollCostPizza})");
                return;
            }

            // 피자 차감 성공: 로컬 캐시 갱신
            if (GameManager.Instance != null)
                GameManager.Instance.pizzaCount =
                    Mathf.Max(0, GameManager.Instance.pizzaCount - RerollCostPizza);

            // 새 캐릭터 생성
            string nickname = GameManager.Instance?.currentPlayerNickname ?? "";
            CharacterData newData = StatCalculator.GenerateRandomCharacter(nickname);
            if (GameManager.Instance != null)
                GameManager.Instance.myCharacterData = newData;

            // [버그 수정] 데디케이티드 서버 구조에서 _controller.SetMyData(_serverData)는
            // 서버 측에서만 실행되어 원격 Owner 클라이언트의 PlayerController.myData가
            // Start() 시점 OLD 참조로 영구 고정됨. 결과: 리롤 후 클라가 OLD attackCooldown으로
            // RPC를 발사하지만 서버는 NEW 쿨다운으로 검증 → ghost-shot(데미지 미적용) 또는
            // 과도한 throttling 발생. moveSpeed/maxHp도 동일 문제이나 NetworkHp/MoveDir 동기화로
            // 보정됨. 평타 쿨다운은 NetworkVariable이 없으므로 클라 myData를 직접 갱신.
            var localController = localSync != null ? localSync.GetComponent<PlayerController>() : null;
            if (localController != null) localController.SetMyData(newData);

            // 서버에 새 스탯 재제출 (A-5: 사전 검증된 sync 사용)
            // await 동안 sync가 Despawn될 가능성 재확인
            if (localSync != null && localSync.IsSpawned)
            {
                localSync.ResubmitCharacterData();
            }
            else
            {
                Debug.LogWarning("[Reroll] await 도중 PlayerNetworkSync가 Despawn됨 — 다음 스폰 시 새 스탯 자동 적용");
            }

            _rerollsUsed++;

            SetStatus("");

            Debug.Log($"[Reroll] 리롤 완료 — 직업={newData.job}, 등급={newData.grade}, " +
                      $"HP={newData.maxHp:F0}, ATK={newData.baseAtk:F1}, " +
                      $"사용횟수={_rerollsUsed}/{MaxRerollsPerMatch}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Reroll] 처리 중 오류: {e.Message}");
            if (this != null) SetStatus("오류가 발생했습니다. 다시 시도해주세요.");
        }
        finally
        {
            if (this != null)
            {
                _isRolling = false;
                if (claimButton != null) claimButton.interactable = CanReroll();
                RefreshUI();
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  리롤 윈도우 타이머
    // ════════════════════════════════════════════════════════════

    private IEnumerator RerollWindowTimer()
    {
        float remaining = RerollWindowSecs;

        while (remaining > 0f)
        {
            if (this == null) yield break;

            if (timerText != null)
                timerText.text = $"남은 시간: {Mathf.CeilToInt(remaining)}초";

            yield return new WaitForSeconds(0.5f);
            remaining -= 0.5f;
        }

        if (this == null) yield break;

        if (timerText != null) timerText.text = "시간 종료";
        ClosePanel();
    }

    // ════════════════════════════════════════════════════════════
    //  UI 갱신
    // ════════════════════════════════════════════════════════════

    private void RefreshUI()
    {
        int pizza = GameManager.Instance?.pizzaCount ?? 0;
        if (pizzaCountText  != null) pizzaCountText.text = $"🍕 {pizza}";
        if (rerollCountText != null)
            rerollCountText.text = _rerollsUsed >= MaxRerollsPerMatch
                ? "리롤 사용 완료"
                : "리롤 가능: 1회";

        bool canRoll = CanReroll();
        if (claimButton != null)
        {
            claimButton.interactable = canRoll;
            var label = claimButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = $"리롤  🍕{RerollCostPizza}";
        }

        RefreshCharacterInfo();
    }

    private void RefreshCharacterInfo()
    {
        if (characterInfoText == null) return;

        var d = GameManager.Instance?.myCharacterData;
        if (d == null)
        {
            characterInfoText.text = "캐릭터 정보 없음";
            return;
        }

        string skills = "없음";
        if (d.activeSkills != null && d.activeSkills.Count > 0)
        {
            var names = new List<string>();
            for (int i = 0; i < Mathf.Min(2, d.activeSkills.Count); i++)
                names.Add(GetSkillName(d.activeSkills[i]));
            skills = string.Join(" / ", names);
        }

        characterInfoText.text =
            $"<b>{GetJobName(d.job)}</b>  |  {GetGradeName(d.grade)}\n" +
            $"HP {d.maxHp:F0}  ATK {d.baseAtk:F1}  SPD {d.moveSpeed:F1}\n" +
            $"속성: {GetAffinityName(d.affinity)}\n" +
            $"스킬: {skills}";
    }

    private bool CanReroll()
    {
        int pizza = GameManager.Instance?.pizzaCount ?? 0;
        return _rerollsUsed < MaxRerollsPerMatch && pizza >= RerollCostPizza;
    }

    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }
}
