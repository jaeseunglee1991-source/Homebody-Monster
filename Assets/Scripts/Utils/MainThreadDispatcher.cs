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

    public static MainThreadDispatcher Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("[MainThreadDispatcher]");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MainThreadDispatcher>();
            }
            return _instance;
        }
    }

    /// <summary>메인 스레드에서 실행할 액션을 큐에 추가합니다.</summary>
    public static void Enqueue(Action action)
    {
        if (action == null) return;
        lock (Instance._lock)
        {
            Instance._actions.Enqueue(action);
        }
    }

    private void Update()
    {
        lock (_lock)
        {
            while (_actions.Count > 0)
            {
                try { _actions.Dequeue()?.Invoke(); }
                catch (Exception e) { Debug.LogError($"[MainThreadDispatcher] 오류: {e.Message}"); }
            }
        }
    }
}
