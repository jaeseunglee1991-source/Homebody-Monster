#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// 10 개 직업 전체에 Skill / Hurt 애니메이션 + AnimatorController 상태/트리거를 일괄 추가하는 일회용 도구.
///
/// ── 동작 ────────────────────────────────────────────────────────
///  1. *-skill.png / *-Hurt.png 를 Sprite Mode = Multiple 로 100x100 그리드 슬라이싱
///  2. {Job}_Skill.anim / {Job}_Hurt.anim AnimationClip 자산 생성 (12 fps, no loop)
///  3. 각 직업의 AnimatorController 에 "Skill" / "Hurt" Trigger 추가
///  4. Skill / Hurt 상태 추가 + Any State → 상태 진입 + 상태 → Idle 자동 복귀 트랜지션 추가
///
/// ── 사용법 ──────────────────────────────────────────────────────
///  Unity 메뉴 → Tools → Homebody → Setup All Job Skill+Hurt Animations
///  → 콘솔에 작업 결과 출력. 이미 셋업된 항목은 안전하게 스킵.
///
/// ── 안전성 ──────────────────────────────────────────────────────
///  ・Archer 처럼 이미 셋업된 직업은 파라미터/상태 중복 없이 스킵.
///  ・PNG 없는 항목(Mage 의 skill) 은 자동 생략.
///  ・Editor 스크립트라서 출시 빌드에는 포함되지 않음.
///  ・작업 완료 후 이 파일을 지워도 무방.
/// </summary>
public static class JobAnimationSetupTool
{
    private const string RootPath  = "Assets/_ThirdParty/Tiny_RPG";
    private const float  FrameRate = 12f;
    private const int    CellSize  = 100;
    private const float  PixelsPerUnit = 32f;

    private struct JobConfig
    {
        public string JobName;        // 코드의 JobType 이름 (ex. "Archer")
        public string Folder;         // _ThirdParty/Tiny_RPG/<Folder>
        public string SkillPngBase;   // 확장자 제외 (null 이면 Skill 생략)
        public string HurtPngBase;
        public string ControllerName; // 확장자 제외
    }

    private static readonly JobConfig[] Configs = new[]
    {
        new JobConfig { JobName = "Archer",    Folder = "Archer",    SkillPngBase = "Archer-skill",           HurtPngBase = "Archer-Hurt",           ControllerName = "Archer_Ctrl" },
        new JobConfig { JobName = "Assassin",  Folder = "Assassin",  SkillPngBase = "Elite Orc-skill",        HurtPngBase = "Elite Orc-Hurt",        ControllerName = "Assassin-Controller" },
        new JobConfig { JobName = "Berserker", Folder = "Berserker", SkillPngBase = "Armored Axeman-skill",   HurtPngBase = "Armored Axeman-Hurt",   ControllerName = "Berserker-Controller" },
        new JobConfig { JobName = "Chef",      Folder = "Chef",      SkillPngBase = "Lancer-skill",           HurtPngBase = "Lancer-Hurt",           ControllerName = "Chef-Controller" },
        new JobConfig { JobName = "Mage",      Folder = "Mage",      SkillPngBase = "Wizard-Skill",           HurtPngBase = "Wizard-Hurt",           ControllerName = "Mage-Controller" },
        new JobConfig { JobName = "Paladin",   Folder = "Paladin",   SkillPngBase = "Knight Templar-skill",   HurtPngBase = "Knight Templar-Hurt",   ControllerName = "Paladin_Controller" },
        new JobConfig { JobName = "Priest",    Folder = "Priest",    SkillPngBase = "Priest-skill",           HurtPngBase = "Priest-Hurt",           ControllerName = "Priest-Controller" },
        new JobConfig { JobName = "Rogue",     Folder = "Rogue",     SkillPngBase = "Werewolf-skill",         HurtPngBase = "Werewolf-Hurt",         ControllerName = "Rouge-Controller" },
        new JobConfig { JobName = "Warrior",   Folder = "Swordsman", SkillPngBase = "Swordsman-skill",        HurtPngBase = "Swordsman-Hurt",        ControllerName = "Swordsman_Controller" },
        new JobConfig { JobName = "Tanker",    Folder = "Tanker",    SkillPngBase = "Knight-skill",           HurtPngBase = "Knight-Hurt",           ControllerName = "Tanker-Controller" },
    };

    [MenuItem("Tools/Homebody/Setup All Job Skill+Hurt Animations")]
    public static void SetupAll()
    {
        int success = 0, skipped = 0, failed = 0;
        foreach (var cfg in Configs)
        {
            try
            {
                if (ProcessJob(cfg)) success++;
                else skipped++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[JobAnimSetup] {cfg.JobName} 처리 실패: {e.Message}\n{e.StackTrace}");
                failed++;
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[JobAnimSetup] 완료: 성공 {success}, 스킵 {skipped}, 실패 {failed} (총 {Configs.Length})");
    }

    // ════════════════════════════════════════════════════════════
    //  Per-job 처리
    // ════════════════════════════════════════════════════════════

    private static bool ProcessJob(JobConfig cfg)
    {
        string ctrlPath = $"{RootPath}/{cfg.Folder}/{cfg.ControllerName}.controller";
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
        if (ctrl == null)
        {
            Debug.LogWarning($"[JobAnimSetup] {cfg.JobName}: AnimatorController 못 찾음 → {ctrlPath}");
            return false;
        }

        bool anyChange = false;

        // ── Skill 처리 (PNG 가 있는 경우만) ─────────────────────
        if (!string.IsNullOrEmpty(cfg.SkillPngBase))
        {
            var skillClip = SliceAndCreateClip(cfg.Folder, cfg.SkillPngBase, $"{cfg.JobName}_Skill", loop: false);
            if (skillClip != null)
            {
                anyChange |= AddTrigger(ctrl, "Skill");
                anyChange |= AddStateWithTransitions(ctrl, $"{cfg.JobName}_Skill", skillClip, "Skill",
                    transitionDuration: 0.1f, exitTime: 0.95f);
            }
        }

        // ── Hurt 처리 ───────────────────────────────────────────
        if (!string.IsNullOrEmpty(cfg.HurtPngBase))
        {
            var hurtClip = SliceAndCreateClip(cfg.Folder, cfg.HurtPngBase, $"{cfg.JobName}_Hurt", loop: false);
            if (hurtClip != null)
            {
                anyChange |= AddTrigger(ctrl, "Hurt");
                anyChange |= AddStateWithTransitions(ctrl, $"{cfg.JobName}_Hurt", hurtClip, "Hurt",
                    transitionDuration: 0.05f, exitTime: 0.9f);
            }
        }

        if (anyChange)
        {
            EditorUtility.SetDirty(ctrl);
            Debug.Log($"[JobAnimSetup] {cfg.JobName}: 셋업 완료 ({ctrl.name})");
        }
        else
        {
            Debug.Log($"[JobAnimSetup] {cfg.JobName}: 이미 셋업됨 → 변경 없음");
        }
        return true;
    }

    // ════════════════════════════════════════════════════════════
    //  스프라이트 슬라이싱 + AnimationClip 생성
    // ════════════════════════════════════════════════════════════

    private static AnimationClip SliceAndCreateClip(string folder, string pngBase, string clipName, bool loop)
    {
        string pngPath  = $"{RootPath}/{folder}/{pngBase}.png";
        string clipPath = $"{RootPath}/{folder}/{clipName}.anim";

        var importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
        if (importer == null)
        {
            Debug.LogWarning($"[JobAnimSetup] PNG 없음 또는 TextureImporter 아님: {pngPath}");
            return null;
        }

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
        if (tex == null) return null;

        int frames = tex.width / CellSize;
        if (frames <= 0) { Debug.LogWarning($"[JobAnimSetup] {pngBase}: 프레임 수 0"); return null; }

        // ── Sprite Mode = Multiple + 슬라이싱 ─────────────
        bool importerChanged = false;
        if (importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType = TextureImporterType.Sprite;
            importerChanged = true;
        }
        if (importer.spriteImportMode != SpriteImportMode.Multiple)
        {
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importerChanged = true;
        }
        if (Mathf.Abs(importer.spritePixelsPerUnit - PixelsPerUnit) > 0.01f)
        {
            importer.spritePixelsPerUnit = PixelsPerUnit;
            importerChanged = true;
        }
        if (importer.filterMode != FilterMode.Point)
        {
            importer.filterMode = FilterMode.Point;
            importerChanged = true;
        }

        // 기존 spritesheet 정보와 다르면 갱신
        bool needReslice = importer.spritesheet == null
                           || importer.spritesheet.Length != frames;
        if (needReslice)
        {
            var sheet = new SpriteMetaData[frames];
            for (int i = 0; i < frames; i++)
            {
                sheet[i] = new SpriteMetaData
                {
                    name      = $"{pngBase}_{i}",
                    rect      = new Rect(i * CellSize, 0, CellSize, CellSize),
                    alignment = (int)SpriteAlignment.Center,
                    pivot     = new Vector2(0.5f, 0.5f),
                    border    = Vector4.zero,
                };
            }
            importer.spritesheet = sheet;
            importerChanged = true;
        }

        if (importerChanged)
        {
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        // ── 슬라이싱된 스프라이트 로드 ────────────────────
        var sprites = AssetDatabase.LoadAllAssetRepresentationsAtPath(pngPath)
                                   .OfType<Sprite>()
                                   .OrderBy(s => GetFrameIndex(s.name))
                                   .ToArray();
        if (sprites.Length == 0)
        {
            Debug.LogWarning($"[JobAnimSetup] {pngBase}: 슬라이싱된 스프라이트가 없음");
            return null;
        }

        // ── AnimationClip 생성 또는 업데이트 ──────────────
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
        bool newClip = clip == null;
        if (newClip)
        {
            clip = new AnimationClip { frameRate = FrameRate };
            AssetDatabase.CreateAsset(clip, clipPath);
        }
        else
        {
            clip.frameRate = FrameRate;
        }

        // 루프 설정
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = loop;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        // 스프라이트 키프레임 입력
        var binding = EditorCurveBinding.PPtrCurve(string.Empty, typeof(SpriteRenderer), "m_Sprite");
        var keyframes = new ObjectReferenceKeyframe[sprites.Length];
        for (int i = 0; i < sprites.Length; i++)
        {
            keyframes[i] = new ObjectReferenceKeyframe
            {
                time  = i / FrameRate,
                value = sprites[i],
            };
        }
        AnimationUtility.SetObjectReferenceCurve(clip, binding, keyframes);

        EditorUtility.SetDirty(clip);
        return clip;
    }

    private static int GetFrameIndex(string spriteName)
    {
        // "Pawn-skill_3" 같은 이름에서 끝 숫자 추출
        int underscore = spriteName.LastIndexOf('_');
        if (underscore < 0 || underscore == spriteName.Length - 1) return 0;
        int.TryParse(spriteName.Substring(underscore + 1), out int idx);
        return idx;
    }

    // ════════════════════════════════════════════════════════════
    //  AnimatorController 조작 (공식 API 사용)
    // ════════════════════════════════════════════════════════════

    private static bool AddTrigger(AnimatorController ctrl, string paramName)
    {
        foreach (var p in ctrl.parameters)
            if (p.name == paramName) return false; // 이미 존재
        ctrl.AddParameter(paramName, AnimatorControllerParameterType.Trigger);
        return true;
    }

    /// <summary>
    /// 지정 트리거로 진입하는 새 상태 + Idle 로 자동 복귀하는 트랜지션을 모두 추가.
    /// 이미 같은 이름의 상태가 있으면 모션만 갱신.
    /// </summary>
    private static bool AddStateWithTransitions(AnimatorController ctrl, string stateName, AnimationClip clip,
                                                string triggerName, float transitionDuration, float exitTime)
    {
        var sm = ctrl.layers[0].stateMachine;

        AnimatorState state = FindState(sm, stateName);
        bool changed = false;

        if (state == null)
        {
            // 기존 상태들과 겹치지 않게 위치 자동 배치
            Vector3 pos = new Vector3(540f, 50f + sm.states.Length * 60f, 0f);
            state = sm.AddState(stateName, pos);
            state.motion = clip;
            changed = true;
        }
        else if (state.motion != clip)
        {
            state.motion = clip;
            changed = true;
        }

        // Any State → 새 상태 트랜지션 (트리거 조건)
        if (!HasAnyStateTransitionTo(sm, state, triggerName))
        {
            var enter = sm.AddAnyStateTransition(state);
            enter.AddCondition(AnimatorConditionMode.If, 0f, triggerName);
            enter.duration = transitionDuration;
            enter.hasExitTime = false;
            enter.canTransitionToSelf = true;
            changed = true;
        }

        // 새 상태 → Idle 자동 복귀 트랜지션
        AnimatorState idle = FindIdleState(sm);
        if (idle != null && !HasTransitionTo(state, idle))
        {
            var exit = state.AddTransition(idle);
            exit.hasExitTime = true;
            exit.exitTime = exitTime;
            exit.duration = 0.1f;
            exit.canTransitionToSelf = false;
            changed = true;
        }

        return changed;
    }

    private static AnimatorState FindState(AnimatorStateMachine sm, string name)
    {
        foreach (var s in sm.states)
            if (s.state.name == name) return s.state;
        return null;
    }

    /// <summary>
    /// "Idle" 또는 "{Job}_Idle" / "{Job}-Idle" 형태의 상태를 유연하게 탐색.
    /// 어느 컨트롤러는 "Archer_Idle", 어느 곳은 "Berserker-Idle", 어느 곳은 그냥 "Idle".
    /// </summary>
    private static AnimatorState FindIdleState(AnimatorStateMachine sm)
    {
        AnimatorState fallback = null;
        foreach (var s in sm.states)
        {
            string nm = s.state.name;
            if (nm == "Idle") return s.state;
            if (nm.EndsWith("_Idle") || nm.EndsWith("-Idle") || nm.EndsWith("Idle"))
                fallback = s.state;
        }
        // Idle 못 찾으면 기본 상태(DefaultState) 폴백
        if (fallback == null) fallback = sm.defaultState;
        return fallback;
    }

    private static bool HasAnyStateTransitionTo(AnimatorStateMachine sm, AnimatorState target, string conditionParam)
    {
        foreach (var t in sm.anyStateTransitions)
        {
            if (t.destinationState != target) continue;
            foreach (var c in t.conditions)
                if (c.parameter == conditionParam) return true;
        }
        return false;
    }

    private static bool HasTransitionTo(AnimatorState from, AnimatorState target)
    {
        foreach (var t in from.transitions)
            if (t.destinationState == target) return true;
        return false;
    }
}
#endif
