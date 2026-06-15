using UnityEngine;

/// <summary>
/// [Fix] 실제 InGameScene 아레나 경계를 NetArena에 주입하는 씬 컴포넌트.
///
/// Fusion NetPlayer는 물리 콜라이더가 아니라 NetArena.Clamp(코드)로 경계를 막는다(결정적 시뮬).
/// NetArena 기본 상수(PoC 30×54, 중심 원점)는 실 씬과 다를 수 있으므로, 이 컴포넌트를 씬에 두고
/// Scene 뷰의 노란 사각형(Gizmo)을 **벽 안쪽 플레이 영역**에 맞추면 이동/투사체/카메라가 모두 반영한다.
///
/// 사용: InGameScene 빈 GameObject에 부착 → center/halfWidth/halfHeight를 벽에 맞게 조정.
/// (Pass B 자동 배선 메뉴가 없으면 추가해 줌. 값은 실제 아레나에 맞춰 직접 조정 필요.)
/// </summary>
[ExecuteAlways]
public class NetArenaBounds : MonoBehaviour
{
    public static NetArenaBounds Instance { get; private set; }

    [Tooltip("플레이 영역 중심(월드 좌표)")]
    public Vector2 center = Vector2.zero;
    [Tooltip("플레이 영역 가로 절반(유닛)")]
    public float halfWidth = 15f;
    [Tooltip("플레이 영역 세로 절반(유닛)")]
    public float halfHeight = 27f;
    [Tooltip("벽 두께(투사체 생존 여유)")]
    public float wallThickness = 1f;

    private void OnEnable()  => Instance = this;
    private void OnDisable() { if (Instance == this) Instance = null; }

    private void OnDrawGizmos()
    {
        // 플레이 영역(노랑) + 벽 포함 영역(주황) 시각화 — 벽 안쪽에 노란 사각형을 맞추세요.
        Vector3 c = new Vector3(center.x, center.y, 0f);
        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.9f);
        Gizmos.DrawWireCube(c, new Vector3(halfWidth * 2f, halfHeight * 2f, 0.01f));
        Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.5f);
        Gizmos.DrawWireCube(c, new Vector3((halfWidth + wallThickness) * 2f,
                                           (halfHeight + wallThickness) * 2f, 0.01f));
    }
}
