#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// InGameScene 아레나 셋업 자동화.
/// Tools/Homebody Monster/Setup InGame Arena 메뉴 실행 시:
///   1) Background_Arena 위치/스케일 표준화 (월드 원점, 디자인 크기에 맞춰 스케일)
///   2) ArenaBounds 루트 GameObject 생성 (4면 BoxCollider2D 벽)
///   3) NetworkSpawnManager 위치 (0,0,0) 정렬 + Point1~PointN 을 플레이 영역 둘레에 재배치
///
/// 디자인 기준:
///   - 캐릭터 평균 이동속도 약 3.0 unit/s (StatCalculator JobBaseStats)
///   - 약 20초 종단 이동을 목표로 플레이 영역 세로 ~54 unit (= 3.0 * 18s 근사)
///   - 배경 스프라이트 비율 9:16 가정 → 가로 ~30 unit
/// </summary>
public static class InGameArenaSetup
{
    private const float PlayWidth  = 30f;   // 안쪽 플레이 가능 폭 (벽 사이)
    private const float PlayHeight = 54f;   // 안쪽 플레이 가능 높이 (벽 사이)
    private const float WallThickness = 1.0f;
    private const float SpawnInset    = 2.0f; // 벽에서 안쪽으로 들여놓는 거리

    [MenuItem("Tools/Homebody Monster/Setup InGame Arena")]
    public static void Setup()
    {
        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.name.Contains("InGameScene"))
        {
            EditorUtility.DisplayDialog("InGame Arena Setup",
                "InGameScene 을 먼저 열어주세요.\n현재 씬: " + scene.name, "OK");
            return;
        }

        var bg = GameObject.Find("Background_Arena");
        if (bg == null)
        {
            EditorUtility.DisplayDialog("InGame Arena Setup",
                "Background_Arena 오브젝트를 찾을 수 없습니다. " +
                "루트에 Background_Arena (SpriteRenderer) 가 있어야 합니다.", "OK");
            return;
        }

        Undo.RegisterFullObjectHierarchyUndo(bg, "Setup Arena");

        // 1) Background_Arena 좌표/스케일 표준화
        var bgT = bg.transform;
        bgT.SetParent(null, true);
        bgT.position = Vector3.zero;
        bgT.rotation = Quaternion.identity;

        var sr = bg.GetComponent<SpriteRenderer>();
        if (sr == null || sr.sprite == null)
        {
            EditorUtility.DisplayDialog("InGame Arena Setup",
                "Background_Arena 에 SpriteRenderer + Sprite 가 설정되어 있어야 합니다.", "OK");
            return;
        }

        // 스프라이트 원본 크기(unit) — PPU 반영된 sprite.bounds.size
        // 플레이 영역(벽 안쪽)이 PlayWidth × PlayHeight 가 되도록 스케일 결정.
        // 벽이 배경 가장자리에서 약 7% 안쪽이라 가정 (이미지의 돌벽 두께 기준).
        // → 배경 실제 표시 크기 = 플레이 영역 / 0.86
        Vector2 spriteSize = sr.sprite.bounds.size; // unit (PPU 반영)
        if (spriteSize.x < 0.01f || spriteSize.y < 0.01f)
        {
            EditorUtility.DisplayDialog("InGame Arena Setup",
                "Sprite bounds 가 0 입니다. 임포트 설정을 확인하세요.", "OK");
            return;
        }
        float targetVisualW = PlayWidth  / 0.78f;
        float targetVisualH = PlayHeight / 0.78f;
        // max 사용 — 비율이 안 맞아도 배경이 플레이 영역 전체를 항상 덮도록 (벽이 BG 밖으로 튀어나오는 것 방지)
        float scale = Mathf.Max(targetVisualW / spriteSize.x, targetVisualH / spriteSize.y);
        bgT.localScale = new Vector3(scale, scale, 1f);

        sr.sortingOrder = -100;

        Debug.Log($"[ArenaSetup] Background_Arena 스케일={scale:F3} " +
                  $"(스프라이트={spriteSize.x:F2}x{spriteSize.y:F2}, 목표 시각={targetVisualW:F1}x{targetVisualH:F1})");

        // 2) ArenaBounds (벽 콜라이더) 재생성
        var oldBounds = GameObject.Find("ArenaBounds");
        if (oldBounds != null) Undo.DestroyObjectImmediate(oldBounds);

        var bounds = new GameObject("ArenaBounds");
        Undo.RegisterCreatedObjectUndo(bounds, "Create ArenaBounds");
        bounds.transform.SetParent(null, true);
        bounds.transform.position = Vector3.zero;
        bounds.transform.localScale = Vector3.one;

        float hw = PlayWidth  * 0.5f;
        float hh = PlayHeight * 0.5f;
        // 4면 벽 — 약간 두껍게 만들어 빠른 캐릭터가 관통하지 않도록.
        CreateWall(bounds.transform, "Wall_Left",   new Vector2(-hw - WallThickness * 0.5f, 0f),                     new Vector2(WallThickness, PlayHeight + WallThickness * 2f));
        CreateWall(bounds.transform, "Wall_Right",  new Vector2( hw + WallThickness * 0.5f, 0f),                     new Vector2(WallThickness, PlayHeight + WallThickness * 2f));
        CreateWall(bounds.transform, "Wall_Bottom", new Vector2(0f, -hh - WallThickness * 0.5f),                     new Vector2(PlayWidth + WallThickness * 2f, WallThickness));
        CreateWall(bounds.transform, "Wall_Top",    new Vector2(0f,  hh + WallThickness * 0.5f),                     new Vector2(PlayWidth + WallThickness * 2f, WallThickness));

        // 3) NetworkSpawnManager 위치 + Point1..N 재배치
        var spawnMgr = GameObject.Find("NetworkSpawnManager");
        if (spawnMgr != null)
        {
            Undo.RegisterFullObjectHierarchyUndo(spawnMgr, "Reset Spawn Points");
            spawnMgr.transform.SetParent(null, true);
            spawnMgr.transform.position = Vector3.zero;
            spawnMgr.transform.rotation = Quaternion.identity;
            spawnMgr.transform.localScale = Vector3.one;

            // 자식 Point1..PointN 수집
            var points = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in spawnMgr.transform)
            {
                if (child.name.StartsWith("Point")) points.Add(child);
            }
            // 이름 기준 정렬 (Point1, Point2, ...)
            points.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            // 둘레 8 지점 배치 (포인트 수가 8 미만/초과여도 동작)
            // 시계 방향: 좌상 → 상중앙 → 우상 → 우중앙 → 우하 → 하중앙 → 좌하 → 좌중앙
            float sx = hw - SpawnInset;
            float sy = hh - SpawnInset;
            Vector2[] perimeter = new Vector2[]
            {
                new Vector2(-sx,  sy),
                new Vector2( 0f,  sy),
                new Vector2( sx,  sy),
                new Vector2( sx,  0f),
                new Vector2( sx, -sy),
                new Vector2( 0f, -sy),
                new Vector2(-sx, -sy),
                new Vector2(-sx,  0f),
            };
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 pos = perimeter[i % perimeter.Length];
                points[i].localPosition = new Vector3(pos.x, pos.y, 0f);
            }
            Debug.Log($"[ArenaSetup] NetworkSpawnManager 재정렬 완료 — {points.Count} 개 포인트 배치");
        }
        else
        {
            Debug.LogWarning("[ArenaSetup] NetworkSpawnManager 를 찾지 못해 스폰 포인트 재배치 생략.");
        }

        // 4) 카메라 가이드 — Cinemachine ortho size 권장값 안내 (자동 변경은 하지 않음)
        var cam = GameObject.Find("CinemachineCamera");
        Debug.Log($"[ArenaSetup] 권장 Cinemachine Orthographic Size: 8 ~ 10 " +
                  $"(현재 카메라 오브젝트={(cam != null ? cam.name : "없음")})");

        // 5) 씬 저장 마킹
        EditorSceneManager.MarkSceneDirty(scene);

        EditorUtility.DisplayDialog("InGame Arena Setup",
            $"아레나 셋업 완료.\n\n" +
            $"플레이 영역: {PlayWidth} x {PlayHeight} unit\n" +
            $"평균 이동속도 3.0 기준 종단 이동 약 {PlayHeight / 3.0f:F0}초\n" +
            $"벽 콜라이더: ArenaBounds 자식 4개\n\n" +
            $"Ctrl+S 로 씬 저장 후 Play 테스트 하세요.", "OK");
    }

    private static void CreateWall(Transform parent, string name, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create Wall");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(position.x, position.y, 0f);
        go.transform.localScale = Vector3.one;

        var box = go.AddComponent<BoxCollider2D>();
        box.size = size;
        box.isTrigger = false;
    }
}
#endif
