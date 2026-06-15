using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// [Pass B] 기존 Cinemachine 카메라가 로컬(입력권한) NetPlayer를 런타임에 추적하도록 타겟을 지정.
/// 로컬 사망 시 생존자 관전(Tab 순환)으로 전환. 아레나 클램프/구도는 Cinemachine 본체
/// (Confiner/Framing) 설정에 맡긴다.
///
/// NetCameraFollow(카메라 transform 직접 이동)의 대체물 — 둘을 동시에 쓰면 카메라 제어가
/// 충돌하므로 InGameScene에서는 둘 중 하나만 사용한다(Cinemachine 유지 시 이 스크립트).
/// </summary>
public class NetCinemachineTarget : MonoBehaviour
{
    [Tooltip("비우면 같은 오브젝트 → 씬에서 CinemachineCamera 자동 탐색")]
    public CinemachineCamera vcam;

    private NetPlayer _local;
    private int       _spectateIdx;

    /// <summary>현재 카메라가 따라가는 대상(NetHUD 관전 라벨용).</summary>
    public NetPlayer CurrentTarget { get; private set; }

    private void Awake()
    {
        if (vcam == null) vcam = GetComponent<CinemachineCamera>();
        if (vcam == null) vcam = FindFirstObjectByType<CinemachineCamera>();
    }

    private void LateUpdate()
    {
        if (vcam == null)
        {
            vcam = FindFirstObjectByType<CinemachineCamera>();
            if (vcam == null) return;
        }

        var       local  = LocalPlayer();
        NetPlayer target = null;

        if (local != null && !local.IsDead)
        {
            target = local;
        }
        else
        {
            // 관전: 생존자 목록에서 선택(Tab 순환) — NetCameraFollow와 동일 규칙.
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

        var tr = target.transform;
        if (vcam.Target.TrackingTarget != tr)
        {
            var t = vcam.Target;
            t.TrackingTarget = tr;
            vcam.Target = t;
        }
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
