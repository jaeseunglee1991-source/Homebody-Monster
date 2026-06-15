using UnityEngine;

/// <summary>
/// [Phase 4-A / Fix] 아레나 규격 — 이동/투사체/카메라가 공유하는 경계 판정 유틸.
/// 시뮬레이션 결정성을 위해 물리 대신 클램프 사용.
///
/// 기본값은 PoC 규격(플레이 영역 30×54, 중심 원점)이지만, 씬에 <see cref="NetArenaBounds"/>가
/// 있으면 그 값을 사용한다 → 실제 InGameScene 아레나(크기·중심이 다름)에 맞출 수 있다.
/// </summary>
public static class NetArena
{
    private const float DefaultHalfWidth     = 15f; // PlayWidth  30 / 2
    private const float DefaultHalfHeight    = 27f; // PlayHeight 54 / 2
    private const float DefaultWallThickness = 1f;

    public const float SpawnInset = 2f;

    private static NetArenaBounds B => NetArenaBounds.Instance;

    public static float   HalfWidth     => B != null ? B.halfWidth     : DefaultHalfWidth;
    public static float   HalfHeight    => B != null ? B.halfHeight    : DefaultHalfHeight;
    public static float   WallThickness => B != null ? B.wallThickness : DefaultWallThickness;
    public static Vector2 Center        => B != null ? B.center        : Vector2.zero;

    /// <summary>플레이 영역 안으로 위치를 고정한다 (이동·넉백·돌진 공용).</summary>
    public static Vector3 Clamp(Vector3 p)
    {
        Vector2 c = Center;
        return new Vector3(
            Mathf.Clamp(p.x, c.x - HalfWidth,  c.x + HalfWidth),
            Mathf.Clamp(p.y, c.y - HalfHeight, c.y + HalfHeight),
            p.z);
    }

    /// <summary>벽 포함 영역 내부인지 (투사체 생존 판정).</summary>
    public static bool Contains(Vector2 p)
    {
        Vector2 c = Center;
        return p.x >= c.x - (HalfWidth  + WallThickness) && p.x <= c.x + (HalfWidth  + WallThickness) &&
               p.y >= c.y - (HalfHeight + WallThickness) && p.y <= c.y + (HalfHeight + WallThickness);
    }

    /// <summary>아레나 내부 임의 위치 (inset 만큼 벽에서 들여놓음).</summary>
    public static Vector3 RandomInside(float inset)
    {
        Vector2 c = Center;
        return new Vector3(
            c.x + Random.Range(-(HalfWidth  - inset), HalfWidth  - inset),
            c.y + Random.Range(-(HalfHeight - inset), HalfHeight - inset),
            0f);
    }

    /// <summary>
    /// 스폰/부활 폴백 — 중앙부 임의 위치(아레나 크기에 맞춰 클램프).
    /// 실제 게임은 NetSpawnPoints의 둘레 지점을 우선 사용.
    /// </summary>
    public static Vector3 RandomSpawn()
    {
        Vector2 c  = Center;
        float   rx = Mathf.Max(0f, Mathf.Min(8f,  HalfWidth  - SpawnInset));
        float   ry = Mathf.Max(0f, Mathf.Min(10f, HalfHeight - SpawnInset));
        return new Vector3(c.x + Random.Range(-rx, rx), c.y + Random.Range(-ry, ry), 0f);
    }
}
