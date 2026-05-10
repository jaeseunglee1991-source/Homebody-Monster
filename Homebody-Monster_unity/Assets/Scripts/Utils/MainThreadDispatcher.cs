using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>
/// Supabase Realtime 콜백은 백그라운드 스레드에서 옵니다.
/// Unity API는 메인 스레드에서만 호출 가능하므로 이 디스패처를 통해 전달합니다.
/// </summary>
public class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher _instance;
    private readonly Queue<Action> _actions = new Queue<Action>();
    private readonly object _lock = new object();

    // CRITICAL-01: Instance getter에서 new GameObject() 자동 생성 제거.
    // Supabase Realtime 콜백은 백그라운드 스레드에서 발생하는데, Unity API(GameObject 생성/AddComponent)는
    // 메인 스레드 전용. 이전 구현은 백그라운드 스레드의 Enqueue → Instance getter → new GameObject() 경로로
    // Unity API 규칙 위반 크래시 가능성. RuntimeInitializeOnLoadMethod로 메인 스레드에서 미리 부트스트랩.
    public static MainThreadDispatcher Instance => _instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null) return;
        var go = new GameObject("[MainThreadDispatcher]");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<MainThreadDispatcher>();
    }

    private void Awake()
    {
        // M-15: Inspector에 수동 배치된 인스턴스가 있을 경우 중복을 방지한다.
        if (_instance != null && _instance != this)
        {
            Destroy(this);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    /// <summary>메인 스레드에서 실행할 액션을 큐에 추가합니다. (스레드 안전)</summary>
    public static void Enqueue(Action action)
    {
        if (action == null) return;
        // CRITICAL-01: Instance getter 호출(새 인스턴스 생성 시도)을 피하고 필드 스냅샷만 사용.
        var inst = _instance;
        if (inst == null)
        {
            Debug.LogWarning("[MainThreadDispatcher] 미초기화 상태 — 액션 무시 (Bootstrap 이전 호출)");
            return;
        }
        lock (inst._lock)
        {
            inst._actions.Enqueue(action);
        }
    }

    private readonly List<Action> _pending = new List<Action>();

    private void Update()
    {
        lock (_lock)
        {
            while (_actions.Count > 0)
                _pending.Add(_actions.Dequeue());
        }

        // BUG-14: try/catch로 잡히지 않는 예외(StackOverflow 등) 발생 시에도 _pending.Clear()를 보장
        // 하여 다음 프레임에 동일 Action들이 중복 실행되지 않도록 함.
        try
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                try { _pending[i]?.Invoke(); }
                catch (Exception e) { Debug.LogError($"[MainThreadDispatcher] 오류: {e.Message}"); }
            }
        }
        finally
        {
            _pending.Clear();
        }
    }
}
