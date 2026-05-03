using UnityEngine;
using UnityEngine.Pool;

public class DamagePopupPool : MonoBehaviour
{
    public static DamagePopupPool Instance { get; private set; }

    [Header("Pool Settings")]
    [Tooltip("DamagePopup 컴포넌트가 붙은 프리팹")]
    public DamagePopup popupPrefab;

    [Tooltip("풀 오브젝트를 담을 부모 Transform (null이면 자동 생성)")]
    public Transform poolParent;

    [Tooltip("풀 초기 용량 — 한 전투에서 동시에 보일 최대 팝업 수로 설정하세요")]
    public int defaultCapacity = 20;

    [Tooltip("풀 최대 크기 — 초과 시 즉시 Destroy됩니다")]
    public int maxSize = 50;

    private ObjectPool<DamagePopup> _pool;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (poolParent == null)
        {
            var go = new GameObject("DamagePopupPool_Container");
            go.transform.SetParent(transform);
            poolParent = go.transform;
        }

        _pool = new ObjectPool<DamagePopup>(
            createFunc:      CreatePopup,
            actionOnGet:     popup => popup.OnGetFromPool(),
            actionOnRelease: popup => popup.OnReleaseToPool(),
            actionOnDestroy: popup => Destroy(popup.gameObject),
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            collectionCheck: true,
#else
            collectionCheck: false,
#endif
            defaultCapacity: defaultCapacity,
            maxSize:         maxSize
        );
    }

    private void OnDestroy()
    {
        _pool?.Dispose();
    }

    public DamagePopup Spawn(Vector3 worldPosition, string text, Color color)
    {
        if (popupPrefab == null)
        {
            Debug.LogError("[DamagePopupPool] popupPrefab이 Inspector에 할당되지 않았습니다.");
            return null;
        }

        DamagePopup popup = _pool.Get();
        popup.transform.SetPositionAndRotation(worldPosition, Quaternion.identity);
        popup.Setup(text, color);
        return popup;
    }

    public void Release(DamagePopup popup)
    {
        _pool.Release(popup);
    }

    private DamagePopup CreatePopup()
    {
        DamagePopup popup = Instantiate(popupPrefab, poolParent);
        popup.gameObject.SetActive(false);
        return popup;
    }
}
