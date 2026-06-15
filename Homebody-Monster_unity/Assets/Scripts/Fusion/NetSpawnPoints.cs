using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [Phase 6-A] 배치된 스폰 지점 제공 (실 InGameScene의 디자인된 스폰 포인트 재사용용).
/// 씬에 1개 두고 points에 Transform들을 배선하거나, 자식으로 "Point*" 오브젝트를 두면 자동 수집.
/// 스폰은 호스트(StateAuthority)에서만 호출되므로 비네트워크 씬 싱글톤으로 충분.
///
/// 실 cutover: InGameScene의 기존 NetworkSpawnManager.spawnPoints 배열을 이 컴포넌트 points에 그대로 연결.
/// 없으면 NetArena.RandomSpawn() 폴백.
/// </summary>
public class NetSpawnPoints : MonoBehaviour
{
    public static NetSpawnPoints Instance { get; private set; }

    [Tooltip("스폰 위치 Transform들. 비워두면 자식 중 이름이 'Point'로 시작하는 오브젝트를 자동 수집.")]
    public Transform[] points;

    private readonly List<int> _shuffled = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        if (points == null || points.Length == 0)
        {
            var list = new List<Transform>();
            foreach (Transform c in transform)
                if (c.name.StartsWith("Point")) list.Add(c);
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            points = list.ToArray();
        }
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    /// <summary>셔플 순회로 다음 스폰 위치 (겹침 최소화 — NetworkSpawnManager.GetNextSpawnPoint 방식).</summary>
    public Vector3 Next()
    {
        if (points == null || points.Length == 0) return NetArena.RandomSpawn();

        if (_shuffled.Count == 0)
        {
            for (int i = 0; i < points.Length; i++) _shuffled.Add(i);
            for (int i = _shuffled.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (_shuffled[i], _shuffled[j]) = (_shuffled[j], _shuffled[i]);
            }
        }

        int idx = _shuffled[0];
        _shuffled.RemoveAt(0);
        return points[idx] != null ? points[idx].position : NetArena.RandomSpawn();
    }

    /// <summary>씬에 NetSpawnPoints가 있으면 그 지점, 없으면 아레나 랜덤.</summary>
    public static Vector3 Spawn() => Instance != null ? Instance.Next() : NetArena.RandomSpawn();
}
