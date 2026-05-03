using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 게임 전체 밸런스 수치를 한 곳에서 관리하는 ScriptableObject.
///
/// ─ 사용법 ────────────────────────────────────────────────────
///  1. Unity 메뉴 → Assets → Create → HomebodyMonster → Game Balance Config
///  2. Assets/Resources/GameBalanceConfig.asset 으로 저장
///  3. 런타임: GameBalanceConfig.Get() 으로 어디서나 접근
///
/// ─ 수치 수정 가이드 ──────────────────────────────────────────
///  • Inspector에서 수정 → 플레이 모드 중에도 즉시 반영 (핫리로드)
///  • 출시 후 밸런스 패치 시 이 파일 수정 후 addressables/패키지로 배포
///  • CombatSystem, StatCalculator, SkillSystem의 하드코딩 상수를 이 값으로 교체
///
/// ─ 핫리로드 지원 ──────────────────────────────────────────────
///  OnValidate()에서 정적 캐시를 갱신하므로 Inspector 수정 시
///  플레이 중에도 즉시 적용됩니다. (단, 서버 재시작 필요 없음)
/// </summary>
[CreateAssetMenu(fileName = "GameBalanceConfig", menuName = "HomebodyMonster/Game Balance Config")]
public class GameBalanceConfig : ScriptableObject
{
    private static GameBalanceConfig _instance;

    /// <summary>Resources/GameBalanceConfig.asset을 로드합니다.</summary>
    public static GameBalanceConfig Get()
    {
        if (_instance == null)
            _instance = Resources.Load<GameBalanceConfig>("GameBalanceConfig");
        if (_instance == null)
            Debug.LogError("[GameBalanceConfig] Resources/GameBalanceConfig.asset이 없습니다.");
        return _instance;
    }

    private void OnValidate() => _instance = this; // Inspector 수정 시 캐시 갱신

    // ════════════════════════════════════════════════════════════
    //  ① 기본 스탯
    // ════════════════════════════════════════════════════════════

    [Header("① 기본 스탯 — StatCalculator 연동")]
    [Tooltip("기본 HP 랜덤 범위")]
    public float BaseHpMin  = 20f;
    public float BaseHpMax  = 50f;
    [Tooltip("기본 공격력 랜덤 범위")]
    public float BaseAtkMin = 2f;
    public float BaseAtkMax = 5f;
    [Tooltip("등급당 배율 증가량 (GradeTier * 이 값 + 1.0)")]
    public float GradeMultiplierStep = 0.111f;

    // ════════════════════════════════════════════════════════════
    //  ② 전투 배율
    // ════════════════════════════════════════════════════════════

    [Header("② 전투 배율 — CombatSystem 연동")]
    [Tooltip("상성 유리 시 데미지 배율")]
    public float AffinityAdvantageMultiplier = 1.5f;

    [Tooltip("특수 상성(MintChoco vs Pineapple) 배율")]
    public float SpecialAffinityMultiplier   = 2.0f;

    [Tooltip("은신 첫 타 보너스 배율")]
    public float StealthFirstHitMultiplier   = 1.5f;

    [Tooltip("행운의 일격 발동 확률 (0~1)")]
    [Range(0f, 1f)]
    public float LuckyStrikeChance           = 0.1f;

    [Tooltip("행운의 일격 데미지 배율")]
    public float LuckyStrikeMultiplier       = 1.5f;

    [Tooltip("닌자 회피 확률 (0~1)")]
    [Range(0f, 1f)]
    public float NinjaEvadeChance            = 0.15f;

    [Tooltip("흡혈 비율 (피해량 대비)")]
    [Range(0f, 1f)]
    public float LifestealRate               = 0.2f;

    [Tooltip("흡혈 최솟값")]
    public float LifestealMin                = 0.5f;

    [Tooltip("가시갑옥 반사 비율 (피해량 대비)")]
    [Range(0f, 1f)]
    public float ThornsReflectRate           = 0.1f;

    [Tooltip("가시갑옥 반사 최솟값")]
    public float ThornsReflectMin            = 0.5f;

    [Tooltip("거인 학살자: HP 차이 10당 데미지 증가율")]
    public float GiantKillerBonusPerTen      = 0.05f;

    [Tooltip("처형인: HP 임계값 비율 (이하 시 발동)")]
    [Range(0f, 1f)]
    public float ExecutionerHpThreshold      = 0.25f;

    [Tooltip("처형인: 데미지 배율")]
    public float ExecutionerMultiplier       = 1.3f;

    [Tooltip("수호자: HP 임계값 비율 (이하 시 발동)")]
    [Range(0f, 1f)]
    public float GuardianHpThreshold         = 0.3f;

    [Tooltip("수호자: 피해 감소율")]
    [Range(0f, 1f)]
    public float GuardianDamageReduction     = 0.2f;

    [Tooltip("방어 태세: 피해 감소율")]
    [Range(0f, 1f)]
    public float DefenseStanceDamageReduction= 0.5f;

    // ════════════════════════════════════════════════════════════
    //  ③ 재생 패시브
    // ════════════════════════════════════════════════════════════

    [Header("③ 재생 패시브 — CombatSystem.RegenerationRoutine 연동")]
    [Tooltip("재생 판정 비전투 시간 (초)")]
    public float RegenerationCooldown    = 4f;

    [Tooltip("재생 틱 간격 (초)")]
    public float RegenerationTickInterval = 2f;

    [Tooltip("재생 비율 (최대HP 대비)")]
    [Range(0f, 0.5f)]
    public float RegenerationHpRate      = 0.05f;

    [Tooltip("재생 최솟값")]
    public float RegenerationMin         = 1.5f;

    // ════════════════════════════════════════════════════════════
    //  ④ 부활 시스템
    // ════════════════════════════════════════════════════════════

    [Header("④ 부활 시스템 — PlayerNetworkSync 연동")]
    [Tooltip("부활 가능 경과 시간 상한 (초)")]
    public float ReviveTimeLimit         = 60f;

    [Tooltip("부활권 타임아웃 (UI 카운트다운, 초)")]
    public float ReviveDecisionTimeout   = 30f;

    [Tooltip("부활 후 HP 비율 (최대HP 대비)")]
    [Range(0.1f, 1f)]
    public float ReviveHpRatio           = 0.5f;

    [Tooltip("한 매치에서 전체 공유 최대 부활 횟수")]
    public int   ReviveMaxPerMatch       = 3;

    // ════════════════════════════════════════════════════════════
    //  ⑤ 서버 안티치트 임계값
    // ════════════════════════════════════════════════════════════

    [Header("⑤ 안티치트 — ServerValidator 연동")]
    [Tooltip("속도핵 판정 최대 속도 (유닛/초)")]
    public float AntiCheat_MaxSpeed          = 12f;

    [Tooltip("텔레포트 판정 거리 임계값")]
    public float AntiCheat_TeleportThreshold = 8f;

    [Tooltip("데미지핵 판정 최대 배율 (baseAtk 기준)")]
    public float AntiCheat_MaxDamageMultiplier = 5f;

    [Tooltip("강제 추방까지 누적 위반 횟수")]
    public int   AntiCheat_KickThreshold     = 5;

    // ════════════════════════════════════════════════════════════
    //  ⑥ 매칭 설정
    // ════════════════════════════════════════════════════════════

    [Header("⑥ 매칭 — MatchmakingManager 연동")]
    public int   Matchmaking_MaxPlayers   = 8;
    public int   Matchmaking_MinPlayers   = 2;
    public float Matchmaking_MaxWaitSecs  = 60f;
    public float Matchmaking_SceneLoadDelaySecs = 3f;

    // ════════════════════════════════════════════════════════════
    //  ⑦ 보상 공식
    // ════════════════════════════════════════════════════════════

    [Header("⑦ 보상 공식 (참고용 — 실제 계산은 Supabase RPC에서)")]
    [Tooltip("1위 기본 피자 보상")]
    public int   Reward_Rank1Pizza        = 100;
    [Tooltip("2위 기본 피자 보상")]
    public int   Reward_Rank2Pizza        = 60;
    [Tooltip("3~4위 기본 피자 보상")]
    public int   Reward_Rank3to4Pizza     = 40;
    [Tooltip("5위 이하 기본 피자 보상")]
    public int   Reward_RankOtherPizza    = 20;
    [Tooltip("킬 1개당 피자 보상")]
    public int   Reward_PizzaPerKill      = 5;
    [Tooltip("광고 시청 일일 피자 보상")]
    public int   Reward_AdDailyPizza      = 20;

    // ════════════════════════════════════════════════════════════
    //  ⑧ 쿨다운 오버라이드 (SkillSystem 기본값 재정의)
    //     비워두면 SkillSystem 내 하드코딩 값 사용
    // ════════════════════════════════════════════════════════════

    [Header("⑧ 스킬 쿨다운 오버라이드 (비우면 SkillSystem 기본값 사용)")]
    public SkillCooldownOverride[] SkillCooldownOverrides;

    [System.Serializable]
    public struct SkillCooldownOverride
    {
        public ActiveSkillType Skill;
        [Tooltip("0이면 SkillSystem 기본값 사용")]
        public float           Cooldown;
    }

    /// <summary>오버라이드가 있으면 반환, 없으면 0 반환 (0 = 기본값 사용)</summary>
    public float GetSkillCooldownOverride(ActiveSkillType skill)
    {
        if (SkillCooldownOverrides == null) return 0f;
        foreach (var o in SkillCooldownOverrides)
            if (o.Skill == skill && o.Cooldown > 0f) return o.Cooldown;
        return 0f;
    }

    // ════════════════════════════════════════════════════════════
    //  ⑨ 시야(Fog of War)
    // ════════════════════════════════════════════════════════════

    [Header("⑨ 시야 설정 — PlayerVisibility 연동")]
    [Tooltip("캐릭터 시야 반경 (유닛)")]
    public float FogOfWar_ViewRadius      = 8f;
    [Tooltip("시야 갱신 간격 (초). 낮을수록 정확하지만 성능 부담↑")]
    public float FogOfWar_UpdateInterval  = 0.1f;

    // ════════════════════════════════════════════════════════════
    //  ⑩ 인앱결제 상품 가격 (참고용 — 실제 가격은 Google Play에서 설정)
    // ════════════════════════════════════════════════════════════

    [Header("⑩ 인앱결제 상품 구성 (참고용)")]
    public ShopProductConfig[] ShopProducts;

    [System.Serializable]
    public struct ShopProductConfig
    {
        public string  ProductId;
        [Tooltip("표시 이름")]
        public string  DisplayName;
        [Tooltip("피자 보상량")]
        public int     PizzaAmount;
        [Tooltip("부활권 보상량")]
        public int     ReviveAmount;
        [Tooltip("첫 구매 한정 여부")]
        public bool    IsFirstPurchaseOnly;
    }
}
