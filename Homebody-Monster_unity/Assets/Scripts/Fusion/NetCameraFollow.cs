using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [Phase 2-B·2-C] 로컬(입력권한) NetPlayer를 카메라가 추적. 클라이언트 로컬 전용(네트워크 무관).
/// 로컬 플레이어 사망 시 생존자 관전 모드로 전환(Tab으로 대상 순환).
/// </summary>
public class NetCameraFollow : MonoBehaviour
{
    public float z = -10f;

    private Camera    _cam;
    private NetPlayer _local;
    private int       _spectateIdx;

    /// <summary>현재 카메라가 따라가는 대상(NetHUD 관전 라벨용).</summary>
    public NetPlayer CurrentTarget { get; private set; }

    private void LateUpdate()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        var local = LocalPlayer();
        NetPlayer target = null;

        if (local != null && !local.IsDead)
        {
            target = local;
        }
        else
        {
            // 관전: 생존자 목록에서 선택 (Tab으로 순환)
            var alive = AlivePlayers(exclude: local);
            if (alive.Count > 0)
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.tabKey.wasPressedThisFrame) _spectateIdx++;
                target = alive[Mathf.Abs(_spectateIdx) % alive.Count];
            }
            else if (local != null)
            {
                target = local; // 전원 사망 — 내 시체라도 비춤
            }
        }

        CurrentTarget = target;
        if (target == null) return;

        Vector3 pos = target.transform.position;

        // [4-A] 아레나 밖 허공이 보이지 않도록 카메라 중심을 클램프.
        // 뷰포트가 아레나보다 큰 축은 중앙(0) 고정.
        float   halfH = _cam.orthographicSize;
        float   halfW = halfH * _cam.aspect;
        Vector2 c     = NetArena.Center;
        float   maxX  = Mathf.Max(0f, NetArena.HalfWidth  + NetArena.WallThickness - halfW);
        float   maxY  = Mathf.Max(0f, NetArena.HalfHeight + NetArena.WallThickness - halfH);
        pos.x = Mathf.Clamp(pos.x, c.x - maxX, c.x + maxX);
        pos.y = Mathf.Clamp(pos.y, c.y - maxY, c.y + maxY);

        _cam.transform.position = new Vector3(pos.x, pos.y, z);
    }

    private NetPlayer LocalPlayer()
    {
        if (_local != null) return _local; // Unity-null이면 파괴됨 → 재탐색
        foreach (var p in FindObjectsByType<NetPlayer>(FindObjectsSortMode.None))
            if (p.HasInputAuthority) { _local = p; break; }
        return _local;
    }

    private static List<NetPlayer> AlivePlayers(NetPlayer exclude)
    {
        var list = new List<NetPlayer>();
        foreach (var p in FindObjectsByType<NetPlayer>(FindObjectsSortMode.None))
            if (p != exclude && !p.IsDead) list.Add(p);
        return list;
    }
}
