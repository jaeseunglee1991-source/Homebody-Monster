using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 투사체 프리팹 캐시 레지스트리.
/// GameManager 또는 씬이 파괴되지 않는(DontDestroyOnLoad) 오브젝트에 부착하세요.
/// </summary>
public class ProjectileRegistry : MonoBehaviour
{
    [System.Serializable]
    public struct PrefabEntry
    {
        public string key;           // 예: "Projectiles/Fireball"
        public GameObject prefab;
    }

    [Header("투사체 프리팹 목록 (Inspector에서 할당)")]
    [SerializeField] private PrefabEntry[] prefabEntries;

    private static ProjectileRegistry _instance;
    private static readonly Dictionary<string, GameObject> _cache = new Dictionary<string, GameObject>();

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        BuildCache();
    }

    private void BuildCache()
    {
        _cache.Clear();
        if (prefabEntries == null) return;

        foreach (var entry in prefabEntries)
        {
            if (string.IsNullOrEmpty(entry.key) || entry.prefab == null) continue;
            if (!_cache.ContainsKey(entry.key)) _cache[entry.key] = entry.prefab;
        }
        Debug.Log($"[ProjectileRegistry] {_cache.Count}개의 투사체 프리팹 캐시 완료.");
    }

    public static GameObject GetPrefab(string key)
    {
        if (_cache.TryGetValue(key, out GameObject prefab) && prefab != null)
            return prefab;

        // Inspector에 등록하지 않았을 경우를 대비한 안전 장치 (Resources 폴더에서 동적 로드)
        GameObject loaded = Resources.Load<GameObject>(key);
        if (loaded != null)
        {
            _cache[key] = loaded;
            return loaded;
        }
        return null;
    }
}
