#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Cinemachine;

/// <summary>
/// [Fusion 2 마이그레이션 — Pass B 배선 자동화]
/// 현재 열린 씬(InGameScene)에서 기존 캔버스 HUD(InGameHUD)와 Cinemachine 카메라를
/// Fusion 경로로 연결하는 인스펙터 작업을 메뉴 한 번으로 처리한다.
///
/// 수행:
///  1) InGameHUD 캔버스 재활성화(Pass A 4단계에서 끈 것을 복원).
///  2) FusionRig(=FusionLauncher 보유)에 NetHudBridge 부착 + hud 참조 연결.
///     (FusionRig가 없으면 InGameHUD 오브젝트에 부착)
///  3) CinemachineCamera(vcam) 재활성화 + NetCinemachineTarget 부착(vcam 연결).
///  4) 충돌 컴포넌트 비활성화:
///     · NetCameraFollow (FusionRig — 카메라 직접 이동, Cinemachine과 충돌)
///     · CameraFollowLocalPlayer (NGO — vcam에 부착, IsOwner 기반)
///
/// 멱등: 여러 번 실행해도 안전(이미 있으면 재사용/갱신). 변경 후 씬을 Dirty로만 표시하므로
/// 검증 후 사용자가 직접 저장(Ctrl+S)한다. 모든 단계는 로그로 결과를 남긴다.
///
/// 메뉴: Tools ▸ Homebody Monster ▸ Fusion ▸ Wire Open Scene for Pass B (HUD + Cinemachine)
/// </summary>
public static class FusionInGameCutover
{
    [MenuItem("Tools/Homebody Monster/Fusion/Wire Open Scene for Pass B (HUD + Cinemachine)", priority = 2)]
    public static void WirePassB()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Debug.Log($"[Pass B] 열린 씬 배선 시작: {scene.name}");

        // ── 1) InGameHUD 찾기 + 활성화 ──────────────────────────────
        var hud = Object.FindFirstObjectByType<InGameHUD>(FindObjectsInactive.Include);
        if (hud == null)
        {
            Debug.LogError("[Pass B] ❌ InGameHUD를 찾지 못했습니다. InGameScene을 먼저 여세요. 중단.");
            return;
        }
        ActivateWithParents(hud.gameObject);
        Debug.Log($"[Pass B] ✅ InGameHUD 활성화: {GetPath(hud.transform)}");

        // ── 2) NetHudBridge 부착 (FusionRig 우선, 없으면 InGameHUD) ──
        var launcher = Object.FindFirstObjectByType<FusionLauncher>(FindObjectsInactive.Include);
        GameObject bridgeHost = launcher != null ? launcher.gameObject : hud.gameObject;

        var bridge = bridgeHost.GetComponent<NetHudBridge>();
        if (bridge == null) bridge = Undo.AddComponent<NetHudBridge>(bridgeHost);
        Undo.RecordObject(bridge, "Wire NetHudBridge");
        bridge.hud = hud;
        EditorUtility.SetDirty(bridge);
        Debug.Log($"[Pass B] ✅ NetHudBridge 부착: {GetPath(bridgeHost.transform)} (hud → {hud.name})");

        if (launcher == null)
            Debug.LogWarning("[Pass B] ⚠️ FusionLauncher(FusionRig)를 찾지 못해 NetHudBridge를 InGameHUD에 부착했습니다. " +
                             "Pass A 2단계(FusionRig 생성)가 누락됐는지 확인하세요.");

        // ── 2b) NetHUD 보장 (준비/리롤 패널·머리위 닉네임/체력바·상태 글리프·관전 라벨) ──
        // Pass A 안내에서 FusionRig에 NetHUD가 누락됐을 수 있다. 없으면 리롤 윈도우 UI가 안 보여
        // "준비시간 없음"처럼 보인다. 캔버스와 겹치는 부분(로컬 패널·부활)은 NetHudBridge.Active로 자동 숨김.
        GameObject hudHost = launcher != null ? launcher.gameObject : bridgeHost;
        if (hudHost.GetComponent<NetHUD>() == null)
        {
            Undo.AddComponent<NetHUD>(hudHost);
            Debug.Log($"[Pass B] ✅ NetHUD 부착: {GetPath(hudHost.transform)} (리롤 준비창·머리위바·상태글리프).");
        }
        else
        {
            Debug.Log("[Pass B] (NetHUD 이미 있음 — 건너뜀)");
        }

        // ── 3) Cinemachine vcam 재활성화 + NetCinemachineTarget ──────
        var vcam = Object.FindFirstObjectByType<CinemachineCamera>(FindObjectsInactive.Include);
        if (vcam != null)
        {
            ActivateWithParents(vcam.gameObject);

            var cineTarget = vcam.GetComponent<NetCinemachineTarget>();
            if (cineTarget == null) cineTarget = Undo.AddComponent<NetCinemachineTarget>(vcam.gameObject);
            Undo.RecordObject(cineTarget, "Wire NetCinemachineTarget");
            cineTarget.vcam = vcam;
            EditorUtility.SetDirty(cineTarget);
            Debug.Log($"[Pass B] ✅ CinemachineCamera 활성화 + NetCinemachineTarget 부착: {GetPath(vcam.transform)}");
            // [Pass C] NGO CameraFollowLocalPlayer는 삭제됨 — 별도 비활성화 불필요.
        }
        else
        {
            Debug.LogWarning("[Pass B] ⚠️ CinemachineCamera를 찾지 못했습니다. Cinemachine 미사용이면 " +
                             "FusionRig의 NetCameraFollow(옵션 A)를 그대로 두면 됩니다.");
        }

        // ── 4b) NetCameraFollow(옵션 A — 카메라 직접 이동) 비활성화 ──
        // Cinemachine을 쓸 경우에만 충돌하므로 vcam이 있을 때만 끈다.
        if (vcam != null)
        {
            foreach (var nf in Object.FindObjectsByType<NetCameraFollow>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!nf.enabled) continue;
                Undo.RecordObject(nf, "Disable NetCameraFollow");
                nf.enabled = false;
                EditorUtility.SetDirty(nf);
                Debug.Log($"[Pass B] ✅ NetCameraFollow 비활성화: {GetPath(nf.transform)} (Cinemachine 사용).");
            }
        }

        // ── 5) 레거시 온스크린 조이스틱 비활성화 ────────────────────
        // InGameHUD 캔버스를 다시 켜면 NGO용 VariableJoystick(Joystick Pack)이 되살아나
        // "background not assigned" 에러를 낸다. Fusion은 NetMobileInput(OnGUI 조이스틱)을 쓰므로
        // 이 레거시 조이스틱은 비활성화한다. 서드파티 타입 결합을 피하려 타입명으로 판별(리플렉션).
        int joyDisabled = 0;
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (mb == null) continue;
            string tn = mb.GetType().Name;
            if (tn != "VariableJoystick" && tn != "Joystick" &&
                tn != "FixedJoystick" && tn != "FloatingJoystick" && tn != "DynamicJoystick")
                continue;
            // [Fix] 비활성화가 아니라 삭제 — Joystick Pack의 커스텀 에디터(JoystickEditor.OnInspectorGUI)가
            // 미배선 상태에서 NullReferenceException을 뱉으므로(인스펙터 선택 시) 오브젝트를 제거한다.
            Debug.Log($"[Pass B] ✅ 레거시 조이스틱 삭제: {GetPath(mb.transform)} ({tn})");
            Undo.DestroyObjectImmediate(mb.gameObject);
            joyDisabled++;
        }
        if (joyDisabled == 0)
            Debug.Log("[Pass B] (레거시 온스크린 조이스틱 없음 — 건너뜀)");

        // ── 6) NetArenaBounds 보장 + 벽(Wall_*)에 자동 맞춤 ──────────
        // Fusion 이동은 NetArena.Clamp(코드)로 막으므로 실 벽에 경계를 맞춰야 캐릭터가 안 뚫는다.
        var bounds = Object.FindFirstObjectByType<NetArenaBounds>(FindObjectsInactive.Include);
        if (bounds == null)
        {
            var go = new GameObject("NetArenaBounds");
            Undo.RegisterCreatedObjectUndo(go, "Create NetArenaBounds");
            bounds = Undo.AddComponent<NetArenaBounds>(go);
        }
        Undo.RecordObject(bounds, "Fit NetArenaBounds");
        if (FitArenaBoundsToWalls(bounds))
            Debug.Log($"[Pass B] ✅ NetArenaBounds 벽 자동 맞춤: center={bounds.center}, " +
                      $"half=({bounds.halfWidth:0.##},{bounds.halfHeight:0.##}).");
        else
            Debug.LogWarning("[Pass B] ⚠️ Wall_Left/Right/Top/Bottom를 못 찾아 NetArenaBounds 자동맞춤 실패 — Scene 뷰에서 수동 조정.");
        EditorUtility.SetDirty(bounds);

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[Pass B] ✅ 배선 완료. 씬을 저장(Ctrl+S)한 뒤 ParrelSync 2피어로 검증하세요.\n" +
                  "  검증: 체력바/스킬버튼+쿨다운/생존자/타이머/킬피드/종료배너(캔버스), 카메라가 내 캐릭터 추적.");
    }

    /// <summary>대상과 모든 부모 GameObject를 활성화(꺼진 캔버스 루트 복원용).</summary>
    private static void ActivateWithParents(GameObject go)
    {
        var t = go.transform;
        while (t != null)
        {
            if (!t.gameObject.activeSelf)
            {
                Undo.RecordObject(t.gameObject, "Activate");
                t.gameObject.SetActive(true);
                EditorUtility.SetDirty(t.gameObject);
            }
            t = t.parent;
        }
    }

    private static string GetPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }

    // ════════════════════════════════════════════════════════════
    //  Stage 5 — 삭제된 NGO 스크립트가 남긴 missing-script 정리
    // ════════════════════════════════════════════════════════════

    [MenuItem("Tools/Homebody Monster/Fusion/Clean Missing Scripts (Open Scene)", priority = 5)]
    public static void CleanMissingScriptsInOpenScene()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        int removed = 0, objs = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                int c = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                if (c <= 0) continue;
                Undo.RegisterCompleteObjectUndo(t.gameObject, "Remove missing scripts");
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                EditorUtility.SetDirty(t.gameObject);
                removed += c; objs++;
                Debug.Log($"[CleanMissing] {GetPath(t)} — missing-script {c}개 제거");
            }
        }
        if (removed > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[CleanMissing] ✅ 총 {removed}개 missing-script 제거({objs} 오브젝트). " +
                      "빈 NGO 오브젝트(NetworkManager 등)는 직접 삭제 권장. Ctrl+S로 저장.");
        }
        else
        {
            Debug.Log("[CleanMissing] missing-script 없음 — 씬이 깨끗합니다.");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  NetArenaBounds 벽 자동 맞춤
    // ════════════════════════════════════════════════════════════

    [MenuItem("Tools/Homebody Monster/Fusion/Fit NetArenaBounds to Walls", priority = 3)]
    public static void FitArenaBoundsMenu()
    {
        var bounds = Object.FindFirstObjectByType<NetArenaBounds>(FindObjectsInactive.Include);
        if (bounds == null)
        {
            var go = new GameObject("NetArenaBounds");
            Undo.RegisterCreatedObjectUndo(go, "Create NetArenaBounds");
            bounds = Undo.AddComponent<NetArenaBounds>(go);
        }
        Undo.RecordObject(bounds, "Fit NetArenaBounds");
        if (FitArenaBoundsToWalls(bounds))
        {
            EditorUtility.SetDirty(bounds);
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Debug.Log($"[ArenaBounds] ✅ 벽에 맞춤: center={bounds.center}, " +
                      $"half=({bounds.halfWidth:0.##},{bounds.halfHeight:0.##}). Ctrl+S로 저장하세요.");
            EditorGUIUtility.PingObject(bounds.gameObject);
        }
        else
        {
            Debug.LogError("[ArenaBounds] ❌ Wall_Left/Right/Top/Bottom 오브젝트를 찾지 못했습니다. " +
                           "InGameScene에서 벽 오브젝트 이름을 확인하거나 수동으로 NetArenaBounds를 조정하세요.");
        }
    }

    /// <summary>Wall_Left/Right/Top/Bottom의 안쪽 면으로 NetArenaBounds를 맞춘다.</summary>
    private static bool FitArenaBoundsToWalls(NetArenaBounds bounds)
    {
        var left   = FindByName("Wall_Left");
        var right  = FindByName("Wall_Right");
        var top    = FindByName("Wall_Top");
        var bottom = FindByName("Wall_Bottom");
        if (left == null || right == null || top == null || bottom == null) return false;

        float innerL = WorldBounds(left).max.x;   // 좌벽 안쪽(오른쪽) 면
        float innerR = WorldBounds(right).min.x;  // 우벽 안쪽(왼쪽) 면
        float innerB = WorldBounds(bottom).max.y; // 하벽 안쪽(위) 면
        float innerT = WorldBounds(top).min.y;    // 상벽 안쪽(아래) 면

        if (innerR <= innerL || innerT <= innerB) return false; // 비정상 배치 방어

        bounds.center     = new Vector2((innerL + innerR) / 2f, (innerB + innerT) / 2f);
        bounds.halfWidth  = Mathf.Max(1f, (innerR - innerL) / 2f);
        bounds.halfHeight = Mathf.Max(1f, (innerT - innerB) / 2f);
        return true;
    }

    private static GameObject FindByName(string name)
    {
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t.name == name) return t.gameObject;
        return null;
    }

    /// <summary>월드 AABB — Collider2D(실제 이동 장벽) 우선, 없으면 SpriteRenderer/Renderer.</summary>
    private static Bounds WorldBounds(GameObject go)
    {
        var col = go.GetComponentInChildren<Collider2D>();
        if (col != null) return col.bounds;
        var sr = go.GetComponentInChildren<SpriteRenderer>();
        if (sr != null) return sr.bounds;
        var rend = go.GetComponentInChildren<Renderer>();
        if (rend != null) return rend.bounds;
        return new Bounds(go.transform.position, Vector3.one);
    }
}
#endif
