using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "JobVisualRegistry", menuName = "Game/JobVisualRegistry")]
public class JobVisualRegistry : ScriptableObject
{
    [System.Serializable]
    public struct JobVisual
    {
        public JobType job;
        public RuntimeAnimatorController animatorController;
        public Sprite defaultSprite;
    }

    public List<JobVisual> visuals = new List<JobVisual>();

    private static JobVisualRegistry _instance;
    private Dictionary<JobType, JobVisual> _cache;

    public static JobVisualRegistry Instance
    {
        get
        {
            if (_instance == null)
            {
                // Resources 폴더에서 JobVisualRegistry 파일을 로드합니다.
                _instance = Resources.Load<JobVisualRegistry>("JobVisualRegistry");
                if (_instance == null)
                    Debug.LogWarning("[JobVisualRegistry] Resources/JobVisualRegistry 파일을 찾을 수 없습니다.");
            }
            return _instance;
        }
    }

    private void OnEnable()
    {
        InitializeCache();
    }

    public void InitializeCache()
    {
        if (_cache != null) return;
        _cache = new Dictionary<JobType, JobVisual>();
        foreach (var v in visuals)
        {
            if (!_cache.ContainsKey(v.job))
                _cache.Add(v.job, v);
        }
    }

    public bool TryGetVisual(JobType job, out JobVisual visual)
    {
        InitializeCache();
        return _cache.TryGetValue(job, out visual);
    }
}
