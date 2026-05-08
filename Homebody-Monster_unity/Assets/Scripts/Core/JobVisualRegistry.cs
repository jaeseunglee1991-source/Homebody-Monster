using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 직업별 외형(애니메이터 컨트롤러 + Idle 스프라이트) 매핑 ScriptableObject.
///
/// ─ 사용 방법 ───────────────────────────────────────────────────
/// 1. Unity 에디터: Assets/Resources/ 폴더 생성 (없으면)
/// 2. Project 창에서 우클릭 → Create → Game → JobVisualRegistry
/// 3. 파일명을 정확히 "JobVisualRegistry" 로 지정 (Resources.Load 호출용)
/// 4. Inspector 의 Visuals 리스트에 직업별 매핑 추가:
///      - Job: Warrior
///      - AnimatorController: Swordsman_AnimatorController (사용자 제작)
///      - DefaultIdleSprite: Swordsman-Idle (첫 프레임)
/// 5. 등록되지 않은 직업은 프리팹의 기본 외형 그대로 유지됨.
///
/// ─ 캐싱 ────────────────────────────────────────────────────────
/// OnEnable 시 List → Dictionary 캐시 (룩업 O(1)).
/// 인스턴스 자체는 Resources.Load 결과를 정적 필드에 1회 캐시.
/// </summary>
[CreateAssetMenu(fileName = "JobVisualRegistry", menuName = "Game/JobVisualRegistry")]
public class JobVisualRegistry : ScriptableObject
{
    [System.Serializable]
    public class JobVisual
    {
        public JobType                  job;
        public RuntimeAnimatorController animatorController;
        public Sprite                   defaultIdleSprite;
    }

    [SerializeField] private List<JobVisual> visuals = new List<JobVisual>();

    private Dictionary<JobType, JobVisual> _cache;

    private void OnEnable()
    {
        BuildCache();
    }

    private void BuildCache()
    {
        _cache = new Dictionary<JobType, JobVisual>(visuals.Count);
        foreach (var v in visuals)
        {
            if (v == null) continue;
            // 중복 등록 시 마지막 항목이 우선 (Inspector 실수 방지)
            _cache[v.job] = v;
        }
    }

    /// <summary>등록되지 않은 직업이면 null. 호출 측에서 null 가드 필수.</summary>
    public JobVisual GetVisual(JobType job)
    {
        if (_cache == null) BuildCache();
        return _cache.TryGetValue(job, out var v) ? v : null;
    }

    // ── 정적 인스턴스 캐시 ─────────────────────────────────────
    private static JobVisualRegistry _instance;
    private static bool              _loadAttempted;

    /// <summary>
    /// Resources/JobVisualRegistry.asset 자동 로드. 누락 시 null 반환 + 1회 경고.
    /// PlayerController 가 spawn 시 1번만 호출하므로 부하 무시 가능.
    /// </summary>
    public static JobVisualRegistry Instance
    {
        get
        {
            if (_instance != null) return _instance;
            if (_loadAttempted) return null; // 재시도 방지

            _loadAttempted = true;
            _instance = Resources.Load<JobVisualRegistry>("JobVisualRegistry");
            if (_instance == null)
            {
                Debug.LogWarning(
                    "[JobVisualRegistry] Resources/JobVisualRegistry.asset 를 찾을 수 없습니다. " +
                    "직업별 외형이 적용되지 않습니다. (없으면 기본 프리팹 외형 유지 — 정상 동작)");
            }
            return _instance;
        }
    }
}
