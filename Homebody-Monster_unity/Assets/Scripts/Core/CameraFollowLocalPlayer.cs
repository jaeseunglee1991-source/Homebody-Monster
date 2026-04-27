using UnityEngine;
using Unity.Cinemachine;

/// <summary>
/// CinemachineCamera 오브젝트에 붙이는 스크립트.
/// 씬에 스폰된 로컬 플레이어(IsOwner == true)를 자동으로 추적합니다.
/// </summary>
[RequireComponent(typeof(CinemachineCamera))]
public class CameraFollowLocalPlayer : MonoBehaviour
{
    private CinemachineCamera _vcam;
    private bool _targetAssigned = false;

    private void Awake()
    {
        _vcam = GetComponent<CinemachineCamera>();
        if (_vcam == null)
            Debug.LogError("[CameraFollowLocalPlayer] ❌ CinemachineCamera 컴포넌트를 찾지 못했습니다!");
    }

    private void Update()
    {
        if (_targetAssigned || _vcam == null) return;

        var players = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

        foreach (var player in players)
        {
            if (!player.IsOwner) continue;

            // Cinemachine 3.x 호환 방식으로 Target 설정
            var t = _vcam.Target;
            t.TrackingTarget = player.transform;
            _vcam.Target = t;

            // Follow 프로퍼티도 함께 설정 (혹시 모를 호환성 확보)
            _vcam.Follow = player.transform;

            _targetAssigned = true;
            return;
        }
    }
}

