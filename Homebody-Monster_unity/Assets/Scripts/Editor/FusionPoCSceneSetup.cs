#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Fusion;

/// <summary>
/// [Fusion 2 마이그레이션 — Phase 0a]
/// Photon Fusion 2 연결 검증용 PoC 씬을 메뉴 한 번으로 자동 생성합니다.
///
/// 생성물:
///   • Assets/Prefabs/Network/FusionRunner.prefab  (NetworkRunner + NetworkSceneManagerDefault)
///   • Assets/Scenes/FusionPoC.unity
///       - Main Camera (Orthographic, 2D 친화)
///       - Directional Light (기본)
///       - "FusionBootstrap" 오브젝트:
///           · FusionBootstrap        (StartMode = UserInterface, RunnerPrefab = 위 프리팹)
///           · FusionBootstrapDebugGUI (실행 시 Host/Client 버튼 GUI)
///
/// ※ FusionBootstrap 2.0.12 는 RunnerPrefab 이 반드시 지정돼야 동작합니다
///   (미지정 시 "RunnerPrefab not set" 에러). 이 스크립트가 프리팹 생성+연결까지 자동 처리.
///
/// 작성할 게임 코드 0줄. 이 씬을 2-피어(ParrelSync 등)로 실행해
/// 'AllConnected' 도달 → App ID + Photon Cloud 릴레이 동작을 검증합니다.
///
/// 메뉴: Tools ▸ Homebody Monster ▸ Fusion ▸ Create PoC Scene (0a)
/// </summary>
public static class FusionPoCSceneSetup
{
    private const string SceneFolder       = "Assets/Scenes";
    private const string ScenePath         = "Assets/Scenes/FusionPoC.unity";
    private const string PrefabFolderRoot  = "Assets/Prefabs";
    private const string PrefabFolderNet   = "Assets/Prefabs/Network";
    private const string RunnerPrefabPath  = "Assets/Prefabs/Network/FusionRunner.prefab";
    private const string PlayerPrefabPath  = "Assets/Prefabs/Network/NetPlayer.prefab";
    private const string ProjectilePrefabPath = "Assets/Prefabs/Network/NetProjectile.prefab";
    private const string TrapPrefabPath        = "Assets/Prefabs/Network/NetTrap.prefab";
    private const string MatchPrefabPath       = "Assets/Prefabs/Network/NetMatch.prefab";

    [MenuItem("Tools/Homebody Monster/Fusion/Create PoC Scene (0a)", priority = 0)]
    public static void CreatePoCScene()
    {
        // 1) 현재 수정된 씬이 있으면 저장 여부를 먼저 묻는다 (작업 유실 방지).
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("[FusionPoC] 사용자가 취소했습니다. 씬 생성 중단.");
            return;
        }

        // 2) Runner 프리팹 보장 (없으면 생성).
        NetworkRunner runnerComp = EnsureRunnerPrefab();
        if (runnerComp == null)
        {
            Debug.LogError("[FusionPoC] Runner 프리팹 생성 실패. 중단.");
            return;
        }

        // 3) 기본 오브젝트(Main Camera + Directional Light) 포함 새 씬 생성.
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // 4) 2D 프로젝트에 맞게 메인 카메라를 Orthographic으로.
        var cam = Object.FindFirstObjectByType<Camera>();
        if (cam != null)
        {
            cam.orthographic       = true;
            cam.orthographicSize   = 8f;
            cam.backgroundColor    = new Color(0.12f, 0.13f, 0.16f, 1f);
            cam.transform.position = new Vector3(0f, 0f, -10f);
        }

        // 5) FusionLauncher 오브젝트 구성 (자동 연결 — Play 시 같은 세션으로 auto-match).
        var go = new GameObject("FusionLauncher");
        var launcher = go.AddComponent<FusionLauncher>();
        launcher.RunnerPrefab = runnerComp; // ★ 필수
        launcher.sessionName  = "HBM_PoC";
        go.AddComponent<NetCameraFollow>(); // 로컬 플레이어 추적 (2-B)
        go.AddComponent<NetHUD>();          // 체력바/쿨다운 HUD (2-B)
        go.AddComponent<NetMobileInput>();  // 가상 조이스틱/터치 입력 (4-B)
        go.AddComponent<NetFx>();           // 데미지 팝업/킬피드 (4-D)

        CreateArena(); // 아레나 배경 + 4면 벽 (4-A)

        EditorSceneManager.MarkSceneDirty(scene);

        // 6) Assets/Scenes 폴더 보장 후 저장.
        if (!AssetDatabase.IsValidFolder(SceneFolder))
            AssetDatabase.CreateFolder("Assets", "Scenes");

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
        {
            Debug.LogError($"[FusionPoC] 씬 저장 실패: {ScenePath}");
            return;
        }

        // 7) Build Settings 에 등록(중복 방지).
        AddSceneToBuildSettings(ScenePath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(
            $"[FusionPoC] ✅ PoC 씬 생성 완료(자동연결): {ScenePath}\n" +
            $"   RunnerPrefab = {RunnerPrefabPath}, Session = HBM_PoC\n" +
            "다음: 'Create NetPlayer & Wire (1-A)' 실행 후 양쪽 Play → 클릭 없이 자동 매칭됩니다.");

        var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
        if (asset != null) EditorGUIUtility.PingObject(asset);
    }

    // ════════════════════════════════════════════════════════════
    //  Phase 0b — 플레이어 프리팹 생성 + 러너에 콜백 배선
    // ════════════════════════════════════════════════════════════

    [MenuItem("Tools/Homebody Monster/Fusion/Create NetPlayer & Wire (1-A)", priority = 1)]
    public static void CreatePoCPlayerAndWire()
    {
        // 1) Runner 프리팹 보장 (0a 미실행 상태여도 동작).
        var runner = EnsureRunnerPrefab();
        if (runner == null) { Debug.LogError("[FusionPoC] Runner 프리팹 없음. 중단."); return; }

        // 2) 플레이어 프리팹 보장.
        var playerObj = EnsurePlayerPrefab();
        if (playerObj == null) { Debug.LogError("[FusionPoC] 플레이어 프리팹 생성 실패. 중단."); return; }

        var matchObj = EnsureMatchPrefab(); // 매치 관리자 (1-G)

        // 3) Runner 프리팹에 PoCNetworkCallbacks 부착 + PlayerPrefab/MatchPrefab 연결.
        var contents = PrefabUtility.LoadPrefabContents(RunnerPrefabPath);
        try
        {
            var cb = contents.GetComponent<PoCNetworkCallbacks>();
            if (cb == null) cb = contents.AddComponent<PoCNetworkCallbacks>();
            cb.PlayerPrefab = playerObj;
            cb.MatchPrefab  = matchObj;
            PrefabUtility.SaveAsPrefabAsset(contents, RunnerPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(
            "[FusionPoC] ✅ 1-A 배선 완료.\n" +
            $"   Player  : {PlayerPrefabPath} (NetworkObject + NetworkTransform + NetPlayer)\n" +
            $"   Runner  : {RunnerPrefabPath} 에 PoCNetworkCallbacks 부착 + PlayerPrefab 연결\n" +
            "이제 양쪽 Host/Client 로 실행 → 접속 시 NetPlayer 스폰 → WASD 이동 + Space 자해(HP 동기화) 확인.");
    }

    /// <summary>
    /// NetPlayer.prefab 보장. 가시화(SpriteRenderer) + NetworkObject + NetworkTransform + NetPlayer.
    /// 반환: 프리팹 루트의 NetworkObject (PoCNetworkCallbacks.PlayerPrefab 연결용).
    /// </summary>
    private static NetworkObject EnsurePlayerPrefab()
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        if (existing != null)
        {
            if (existing.GetComponent<NetworkObject>() != null)
            {
                EnsurePlayerComponents(); // 멱등: 누락 컴포넌트(BoxCollider2D/NetStatus) 보강
                return AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath).GetComponent<NetworkObject>();
            }
            AssetDatabase.DeleteAsset(PlayerPrefabPath);
        }

        if (!AssetDatabase.IsValidFolder(PrefabFolderRoot))
            AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder(PrefabFolderNet))
            AssetDatabase.CreateFolder(PrefabFolderRoot, "Network");

        var temp = new GameObject("NetPlayer");

        // 가시화 — 2D/URP 안전한 SpriteRenderer + 내장 스프라이트.
        var sr = temp.AddComponent<SpriteRenderer>();
        sr.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        if (sr.sprite == null)
            Debug.LogWarning("[FusionPoC] 내장 스프라이트 로드 실패 — 캡슐이 안 보일 수 있음(이동/동기화 자체엔 영향 없음).");
        sr.color = new Color(0.3f, 0.8f, 1f);
        temp.transform.localScale = Vector3.one * 4f;

        // 네트워크 컴포넌트 (NetworkObject 먼저).
        temp.AddComponent<NetworkObject>();
        temp.AddComponent<NetworkTransform>();
        temp.AddComponent<NetPlayer>();
        temp.AddComponent<NetStatus>();   // 상태이상 (1-C)
        temp.AddComponent<NetVisual>();   // 직업 비주얼 (2-E)
        var np0 = temp.GetComponent<NetPlayer>();
        np0.ProjectilePrefab = EnsureProjectilePrefab(); // 투사체 배선 (1-D)
        np0.TrapPrefab       = EnsureTrapPrefab();        // 덫 배선 (4-G)

        // 클릭 타겟용 콜라이더 (1-B 평타 타겟팅).
        var bc = temp.AddComponent<BoxCollider2D>();
        bc.isTrigger = true;
        bc.size      = Vector2.one * 0.25f; // localScale 4 → 월드 ≈1유닛

        var prefab = PrefabUtility.SaveAsPrefabAsset(temp, PlayerPrefabPath, out bool ok);
        Object.DestroyImmediate(temp);

        if (!ok || prefab == null)
        {
            Debug.LogError($"[FusionPoC] 플레이어 프리팹 저장 실패: {PlayerPrefabPath}");
            return null;
        }

        Debug.Log($"[FusionPoC] 플레이어 프리팹 생성: {PlayerPrefabPath}");
        return prefab.GetComponent<NetworkObject>();
    }

    /// <summary>기존 NetPlayer.prefab 에 누락 컴포넌트(BoxCollider2D, NetStatus, ProjectilePrefab 배선)를 보강(멱등).</summary>
    private static void EnsurePlayerComponents()
    {
        // 중첩 프리팹 편집 회피 — 투사체/덫 프리팹을 먼저 보장.
        var projectile = EnsureProjectilePrefab();
        var trap       = EnsureTrapPrefab();

        var contents = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            bool changed = false;
            if (contents.GetComponent<BoxCollider2D>() == null)
            {
                var bc = contents.AddComponent<BoxCollider2D>();
                bc.isTrigger = true;
                bc.size      = Vector2.one * 0.25f;
                changed = true;
                Debug.Log("[FusionPoC] NetPlayer.prefab 에 BoxCollider2D 보강(클릭 타겟용).");
            }
            if (contents.GetComponent<NetStatus>() == null)
            {
                contents.AddComponent<NetStatus>();
                changed = true;
                Debug.Log("[FusionPoC] NetPlayer.prefab 에 NetStatus 보강(상태이상).");
            }
            if (contents.GetComponent<NetVisual>() == null)
            {
                contents.AddComponent<NetVisual>();
                changed = true;
                Debug.Log("[FusionPoC] NetPlayer.prefab 에 NetVisual 보강(직업 비주얼).");
            }
            var np = contents.GetComponent<NetPlayer>();
            if (np != null && np.ProjectilePrefab == null && projectile != null)
            {
                np.ProjectilePrefab = projectile;
                changed = true;
                Debug.Log("[FusionPoC] NetPlayer.ProjectilePrefab 배선(투사체).");
            }
            if (np != null && np.TrapPrefab == null && trap != null)
            {
                np.TrapPrefab = trap;
                changed = true;
                Debug.Log("[FusionPoC] NetPlayer.TrapPrefab 배선(덫).");
            }
            if (changed) PrefabUtility.SaveAsPrefabAsset(contents, PlayerPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    /// <summary>NetProjectile.prefab 보장. 가시화(SpriteRenderer) + NetworkObject + NetworkTransform + NetProjectile.</summary>
    private static NetworkObject EnsureProjectilePrefab()
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePrefabPath);
        if (existing != null)
        {
            var no = existing.GetComponent<NetworkObject>();
            if (no != null) return no;
            AssetDatabase.DeleteAsset(ProjectilePrefabPath);
        }

        if (!AssetDatabase.IsValidFolder(PrefabFolderRoot))
            AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder(PrefabFolderNet))
            AssetDatabase.CreateFolder(PrefabFolderRoot, "Network");

        var temp = new GameObject("NetProjectile");
        var sr = temp.AddComponent<SpriteRenderer>();
        sr.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        sr.color  = new Color(1f, 0.85f, 0.2f); // 노란 투사체
        temp.transform.localScale = Vector3.one * 1.2f;

        temp.AddComponent<NetworkObject>();
        temp.AddComponent<NetworkTransform>();
        temp.AddComponent<NetProjectile>();

        var prefab = PrefabUtility.SaveAsPrefabAsset(temp, ProjectilePrefabPath, out bool ok);
        Object.DestroyImmediate(temp);

        if (!ok || prefab == null)
        {
            Debug.LogError($"[FusionPoC] 투사체 프리팹 저장 실패: {ProjectilePrefabPath}");
            return null;
        }

        Debug.Log($"[FusionPoC] 투사체 프리팹 생성: {ProjectilePrefabPath}");
        return prefab.GetComponent<NetworkObject>();
    }

    /// <summary>NetTrap.prefab 보장. 가시화(SpriteRenderer) + NetworkObject + NetworkTransform + NetTrap.</summary>
    private static NetworkObject EnsureTrapPrefab()
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(TrapPrefabPath);
        if (existing != null)
        {
            var no = existing.GetComponent<NetworkObject>();
            if (no != null) return no;
            AssetDatabase.DeleteAsset(TrapPrefabPath);
        }

        if (!AssetDatabase.IsValidFolder(PrefabFolderRoot))
            AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder(PrefabFolderNet))
            AssetDatabase.CreateFolder(PrefabFolderRoot, "Network");

        var temp = new GameObject("NetTrap");
        var sr = temp.AddComponent<SpriteRenderer>();
        sr.sprite       = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        sr.color        = new Color(0.7f, 0.2f, 0.2f, 0.7f); // 반투명 붉은 덫
        sr.sortingOrder = -40;
        temp.transform.localScale = Vector3.one * 1.4f;

        temp.AddComponent<NetworkObject>();
        temp.AddComponent<NetworkTransform>(); // 정지 객체지만 스폰 위치 동기화 보장
        temp.AddComponent<NetTrap>();

        var prefab = PrefabUtility.SaveAsPrefabAsset(temp, TrapPrefabPath, out bool ok);
        Object.DestroyImmediate(temp);

        if (!ok || prefab == null)
        {
            Debug.LogError($"[FusionPoC] 덫 프리팹 저장 실패: {TrapPrefabPath}");
            return null;
        }

        Debug.Log($"[FusionPoC] 덫 프리팹 생성: {TrapPrefabPath}");
        return prefab.GetComponent<NetworkObject>();
    }

    /// <summary>NetMatch.prefab 보장. NetworkObject + NetMatch (시각 없음, OnGUI 표시).</summary>
    private static NetworkObject EnsureMatchPrefab()
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(MatchPrefabPath);
        if (existing != null)
        {
            var no = existing.GetComponent<NetworkObject>();
            if (no != null) return no;
            AssetDatabase.DeleteAsset(MatchPrefabPath);
        }

        if (!AssetDatabase.IsValidFolder(PrefabFolderRoot))
            AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder(PrefabFolderNet))
            AssetDatabase.CreateFolder(PrefabFolderRoot, "Network");

        var temp = new GameObject("NetMatch");
        temp.AddComponent<NetworkObject>();
        temp.AddComponent<NetMatch>();

        var prefab = PrefabUtility.SaveAsPrefabAsset(temp, MatchPrefabPath, out bool ok);
        Object.DestroyImmediate(temp);

        if (!ok || prefab == null)
        {
            Debug.LogError($"[FusionPoC] 매치 프리팹 저장 실패: {MatchPrefabPath}");
            return null;
        }

        Debug.Log($"[FusionPoC] 매치 프리팹 생성: {MatchPrefabPath}");
        return prefab.GetComponent<NetworkObject>();
    }

    /// <summary>
    /// FusionRunner.prefab 을 보장한다. 이미 있으면 그 프리팹의 NetworkRunner 를 반환,
    /// 없으면 NetworkRunner(+NetworkSceneManagerDefault) 를 가진 프리팹을 새로 만든다.
    /// </summary>
    private static NetworkRunner EnsureRunnerPrefab()
    {
        // 이미 존재하면 재사용.
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(RunnerPrefabPath);
        if (existing != null)
        {
            var comp = existing.GetComponent<NetworkRunner>();
            if (comp != null) return comp;
            // 깨진 프리팹이면 삭제 후 재생성.
            AssetDatabase.DeleteAsset(RunnerPrefabPath);
        }

        // 폴더 보장.
        if (!AssetDatabase.IsValidFolder(PrefabFolderRoot))
            AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder(PrefabFolderNet))
            AssetDatabase.CreateFolder(PrefabFolderRoot, "Network");

        // 임시 GO 에 NetworkRunner 부착 → 프리팹으로 저장 → 임시 GO 제거.
        // NetworkSceneManagerDefault / NetworkObjectProviderDefault 는 FusionBootstrap 이
        // 런타임에 없으면 자동 추가하지만, 씬 매니저는 명시적으로 넣어 둔다.
        var temp = new GameObject("FusionRunner");
        temp.AddComponent<NetworkRunner>();
        temp.AddComponent<NetworkSceneManagerDefault>();

        var prefab = PrefabUtility.SaveAsPrefabAsset(temp, RunnerPrefabPath, out bool ok);
        Object.DestroyImmediate(temp);

        if (!ok || prefab == null)
        {
            Debug.LogError($"[FusionPoC] Runner 프리팹 저장 실패: {RunnerPrefabPath}");
            return null;
        }

        Debug.Log($"[FusionPoC] Runner 프리팹 생성: {RunnerPrefabPath}");
        return prefab.GetComponent<NetworkRunner>();
    }

    /// <summary>
    /// [4-A] 아레나 배경 + 4면 벽 생성 — InGameArenaSetup(실제 InGameScene)과 동일 규격(30×54, 벽 1u).
    /// 이동 경계는 NetArena.Clamp(코드)가 담당하고, 벽 콜라이더는 투사체 차단/시각용.
    /// </summary>
    private static void CreateArena()
    {
        var sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        if (sprite == null)
        {
            Debug.LogWarning("[FusionPoC] 내장 스프라이트 로드 실패 — 아레나 시각 생성 생략(경계 클램프는 동작).");
            return;
        }
        Vector2 spriteSize = sprite.bounds.size;

        var root = new GameObject("Arena");

        // 배경 (어두운 바닥) — 벽 포함 영역보다 약간 크게.
        float bgW = NetArena.HalfWidth  * 2f + NetArena.WallThickness * 2f + 2f;
        float bgH = NetArena.HalfHeight * 2f + NetArena.WallThickness * 2f + 2f;
        var bg = new GameObject("Background");
        bg.transform.SetParent(root.transform);
        var bgSr = bg.AddComponent<SpriteRenderer>();
        bgSr.sprite       = sprite;
        bgSr.color        = new Color(0.14f, 0.18f, 0.15f); // 어두운 초록빛 바닥
        bgSr.sortingOrder = -100;
        bg.transform.localScale = new Vector3(bgW / spriteSize.x, bgH / spriteSize.y, 1f);

        // 4면 벽 — InGameArenaSetup 배치와 동일.
        float hw = NetArena.HalfWidth, hh = NetArena.HalfHeight, t = NetArena.WallThickness;
        CreateWall(root.transform, sprite, spriteSize, "Wall_Left",
            new Vector2(-hw - t * 0.5f, 0f), new Vector2(t, hh * 2f + t * 2f));
        CreateWall(root.transform, sprite, spriteSize, "Wall_Right",
            new Vector2( hw + t * 0.5f, 0f), new Vector2(t, hh * 2f + t * 2f));
        CreateWall(root.transform, sprite, spriteSize, "Wall_Bottom",
            new Vector2(0f, -hh - t * 0.5f), new Vector2(hw * 2f + t * 2f, t));
        CreateWall(root.transform, sprite, spriteSize, "Wall_Top",
            new Vector2(0f,  hh + t * 0.5f), new Vector2(hw * 2f + t * 2f, t));

        // [6-A] 둘레 8개 스폰 포인트 (InGameArenaSetup 배치와 동일) + NetSpawnPoints.
        var spRoot = new GameObject("SpawnPoints");
        var sp     = spRoot.AddComponent<NetSpawnPoints>();
        float sx = hw - NetArena.SpawnInset, sy = hh - NetArena.SpawnInset;
        Vector2[] perimeter =
        {
            new(-sx,  sy), new(0f,  sy), new( sx,  sy), new( sx, 0f),
            new( sx, -sy), new(0f, -sy), new(-sx, -sy), new(-sx, 0f),
        };
        var pts = new Transform[perimeter.Length];
        for (int i = 0; i < perimeter.Length; i++)
        {
            var p = new GameObject($"Point{i + 1}");
            p.transform.SetParent(spRoot.transform);
            p.transform.position = perimeter[i];
            pts[i] = p.transform;
        }
        sp.points = pts;

        Debug.Log($"[FusionPoC] 아레나 생성 — 플레이 영역 {hw * 2f:0}×{hh * 2f:0}, 벽 {t}u, 스폰 {pts.Length}곳.");
    }

    private static void CreateWall(Transform parent, Sprite sprite, Vector2 spriteSize,
        string name, Vector2 pos, Vector2 size)
    {
        var wall = new GameObject(name);
        wall.transform.SetParent(parent);
        wall.transform.position = pos;

        var sr = wall.AddComponent<SpriteRenderer>();
        sr.sprite       = sprite;
        sr.color        = new Color(0.45f, 0.42f, 0.38f); // 돌벽 느낌
        sr.sortingOrder = -50;
        wall.transform.localScale = new Vector3(size.x / spriteSize.x, size.y / spriteSize.y, 1f);

        // 비트리거 콜라이더 — NetProjectile이 벽 충돌로 인식(소멸). 스케일이 크기를 담당하므로 size=sprite 원본.
        var bc = wall.AddComponent<BoxCollider2D>();
        bc.isTrigger = false;
        bc.size      = spriteSize;
    }

    private static void AddSceneToBuildSettings(string path)
    {
        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(
            EditorBuildSettings.scenes);

        foreach (var s in scenes)
            if (s.path == path) return; // 이미 등록됨

        scenes.Add(new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"[FusionPoC] Build Settings 에 씬 추가: {path}");
    }
}
#endif
