using UnityEngine;
using Unity.Cinemachine;
using System.Collections;

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

    private void Start()
    {
        StartCoroutine(FindLocalPlayerRoutine());
    }

    private IEnumerator FindLocalPlayerRoutine()
    {
        var wait = new WaitForSeconds(0.2f);

        while (!_targetAssigned)
        {
            if (_vcam == null) yield break;

            var players = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            foreach (var player in players)
            {
                if (!player.IsOwner) continue;

                var t = _vcam.Target;
                t.TrackingTarget = player.transform;
                _vcam.Target = t;
                _vcam.Follow = player.transform;

                _targetAssigned = true;
                Debug.Log($"[CameraFollowLocalPlayer] ✅ 로컬 플레이어 추적 시작: {player.name}");
                yield break;
            }

            yield return wait;
        }
    }
}

