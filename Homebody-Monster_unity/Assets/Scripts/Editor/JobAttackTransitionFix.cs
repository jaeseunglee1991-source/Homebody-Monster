#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// 모든 직업의 AnimatorController 에서 Attack 상태의 트랜지션을 표준화하는 일회용 도구.
///
/// 문제 배경:
///   원본 컨트롤러 (Tiny_RPG 에셋팩) 의 Attack 상태에는 Idle 로 돌아가는 트랜지션이 누락되거나
///   Exit Time 이 설정되지 않은 경우가 있어, 공격 클립이 반복 재생되거나
///   "Attack" 트리거가 한 번 발화되면 Attack 상태에 갇혀 빈 공간 클릭으로
///   취소해도 다음 공격 모션이 계속 나오는 증상이 발생.
///
/// 이 도구가 하는 일:
///   1. 각 컨트롤러의 BaseLayer 에서 이름에 "Attack" 이 포함된 상태를 찾음.
///   2. 해당 상태의 motion (AnimationClip) 의 Loop Time 을 끔.
///   3. Attack → Idle 트랜지션이 없으면 추가 (HasExitTime=true, ExitTime=0.85, Duration=0.1).
///   4. Any State → Attack 트랜지션의 canTransitionToSelf 를 OFF (이미 공격 중일 때 재발화로 인한
///      애니메이션 리셋/끊김 방지). 단 트리거 자체는 큐잉되므로 다음 사이클에 자연 발화.
///
/// ── 사용법 ──────────────────────────────────────────────────────
///   Unity 메뉴 → Tools → Homebody → Fix All Job Attack Transitions
///   → 콘솔에 처리 결과 출력. 이미 올바르게 설정된 컨트롤러는 변경 없이 스킵.
///
/// ── 안전성 ──────────────────────────────────────────────────────
///   ・PlayerController.ClearTarget 에서 ResetTrigger("Attack") 와 함께 동작.
///   ・여러 번 실행해도 idempotent — 같은 트랜지션을 중복 추가하지 않음.
///   ・Editor 전용. 출시 빌드에 영향 없음.
/// </summary>
public static class JobAttackTransitionFix
{
    private const string RootPath = "Assets/_ThirdParty/Tiny_RPG";

    private struct ControllerEntry
    {
        public string DisplayName;
        public string Path;
    }

    private static readonly ControllerEntry[] Controllers = new[]
    {
        new ControllerEntry { DisplayName = "Archer",    Path = $"{RootPath}/Archer/Archer_Ctrl.controller" },
        new ControllerEntry { DisplayName = "Assassin",  Path = $"{RootPath}/Assassin/Assassin-Controller.controller" },
        new ControllerEntry { DisplayName = "Berserker", Path = $"{RootPath}/Berserker/Berserker-Controller.controller" },
        new ControllerEntry { DisplayName = "Chef",      Path = $"{RootPath}/Chef/Chef-Controller.controller" },
        new ControllerEntry { DisplayName = "Mage",      Path = $"{RootPath}/Mage/Mage-Controller.controller" },
        new ControllerEntry { DisplayName = "Paladin",   Path = $"{RootPath}/Paladin/Paladin_Controller.controller" },
        new ControllerEntry { DisplayName = "Priest",    Path = $"{RootPath}/Priest/Priest-Controller.controller" },
        new ControllerEntry { DisplayName = "Rogue",     Path = $"{RootPath}/Rogue/Rouge-Controller.controller" },
        new ControllerEntry { DisplayName = "Warrior",   Path = $"{RootPath}/Swordsman/Swordsman_Controller.controller" },
        new ControllerEntry { DisplayName = "Tanker",    Path = $"{RootPath}/Tanker/Tanker-Controller.controller" },
    };

    [MenuItem("Tools/Homebody/Fix All Job Attack Transitions")]
    public static void FixAll()
    {
        int success = 0, skipped = 0, failed = 0;
        var report = new List<string>();

        foreach (var entry in Controllers)
        {
            try
            {
                var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(entry.Path);
                if (ctrl == null)
                {
                    Debug.LogWarning($"[AttackFix] {entry.DisplayName}: 컨트롤러 못 찾음 → {entry.Path}");
                    failed++;
                    continue;
                }

                var result = FixController(ctrl, entry.DisplayName);
                if (result.changed)
                {
                    EditorUtility.SetDirty(ctrl);
                    success++;
                    report.Add($"  ✓ {entry.DisplayName}: {result.summary}");
                }
                else
                {
                    skipped++;
                    report.Add($"  - {entry.DisplayName}: 이미 정상 ({result.summary})");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[AttackFix] {entry.DisplayName} 처리 실패: {e.Message}\n{e.StackTrace}");
                failed++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[AttackFix] 완료: 수정 {success}, 스킵 {skipped}, 실패 {failed} (총 {Controllers.Length})\n" +
                  string.Join("\n", report));
    }

    private struct FixResult
    {
        public bool   changed;
        public string summary;
    }

    private static FixResult FixController(AnimatorController ctrl, string displayName)
    {
        var sm = ctrl.layers[0].stateMachine;
        bool changed = false;
        var actions = new List<string>();

        // 1) Attack 상태 찾기 — 이름에 "Attack" 포함 (대소문자 무관).
        AnimatorState attack = null;
        foreach (var s in sm.states)
        {
            if (s.state.name.IndexOf("Attack", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                attack = s.state;
                break;
            }
        }
        if (attack == null)
        {
            return new FixResult { changed = false, summary = "Attack 상태 없음 — 스킵" };
        }

        // 2) Attack clip 의 Loop Time 끄기
        if (attack.motion is AnimationClip clip)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.loopTime)
            {
                settings.loopTime = false;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                EditorUtility.SetDirty(clip);
                changed = true;
                actions.Add("Loop OFF");
            }
        }

        // 3) Idle 상태 찾기 (JobAnimationSetupTool 과 동일 로직)
        AnimatorState idle = FindIdleState(sm);
        if (idle == null)
        {
            return new FixResult
            {
                changed = changed,
                summary = "Idle 상태 못 찾음 — Attack→Idle 트랜지션 추가 불가 " +
                          (actions.Count > 0 ? $"({string.Join(", ", actions)})" : "")
            };
        }

        // 4) Attack → Idle 트랜지션 보장
        bool hasIdleTransition = false;
        foreach (var t in attack.transitions)
        {
            if (t.destinationState == idle)
            {
                hasIdleTransition = true;
                // 기존 트랜지션도 안전한 값으로 보정 (Exit Time 누락/0 인 경우 등)
                if (!t.hasExitTime || t.exitTime < 0.5f)
                {
                    t.hasExitTime = true;
                    t.exitTime = 0.85f;
                    if (t.duration < 0.01f) t.duration = 0.1f;
                    changed = true;
                    actions.Add("기존 Attack→Idle 보정");
                }
                break;
            }
        }
        if (!hasIdleTransition)
        {
            var exit = attack.AddTransition(idle);
            exit.hasExitTime = true;
            exit.exitTime = 0.85f;     // 클립의 85% 재생 후 Idle 로 자연스럽게
            exit.duration = 0.1f;
            exit.canTransitionToSelf = false;
            changed = true;
            actions.Add("Attack→Idle 트랜지션 추가");
        }

        // 5) Any State → Attack 트랜지션에서 canTransitionToSelf 끄기
        // 공격 중에 다시 Attack 트리거가 발화되면 상태가 자기 자신으로 재진입하면서
        // 클립이 처음부터 다시 시작되어 끊겨 보이는 증상 방지.
        // (단, 트리거 자체는 큐에 남아 있다가 Attack→Idle 후 다음 발화 시 자연 진입.)
        foreach (var t in sm.anyStateTransitions)
        {
            if (t.destinationState != attack) continue;
            bool isAttackTrigger = false;
            foreach (var c in t.conditions)
            {
                if (string.Equals(c.parameter, "Attack", System.StringComparison.OrdinalIgnoreCase))
                {
                    isAttackTrigger = true;
                    break;
                }
            }
            if (!isAttackTrigger) continue;

            if (t.canTransitionToSelf)
            {
                t.canTransitionToSelf = false;
                changed = true;
                actions.Add("AnyState→Attack canTransitionToSelf OFF");
            }
        }

        string summary = actions.Count > 0 ? string.Join(", ", actions) : "변경 없음";
        return new FixResult { changed = changed, summary = summary };
    }

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
        if (fallback == null) fallback = sm.defaultState;
        return fallback;
    }
}
#endif
