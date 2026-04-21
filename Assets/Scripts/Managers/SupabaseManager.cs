using UnityEngine;
using Supabase;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;

// --- 데이터 모델 (DB 테이블과 1:1 매칭) ---
[System.Serializable]
public class UserProfile
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("nickname")] public string Nickname { get; set; }
    [JsonProperty("win_count")] public int WinCount { get; set; }
    [JsonProperty("lose_count")] public int LoseCount { get; set; }
}

[System.Serializable]
public class MatchEntry
{
    [JsonProperty("player_id")] public string PlayerId { get; set; }
    [JsonProperty("is_winner")] public bool IsWinner { get; set; }
    [JsonProperty("rank")] public int Rank { get; set; }
    [JsonProperty("kill_count")] public int KillCount { get; set; }
    [JsonProperty("survived_time")] public float SurvivedTime { get; set; }
}

public class SupabaseManager : MonoBehaviour
{
    public static SupabaseManager Instance { get; private set; }

    [Header("Supabase Security Credentials")]
    // 사용자님이 제공해주신 실제 접속 정보 적용
    public string supabaseUrl = "https://khvimlswbbmxcxpjkkes.supabase.co"; 
    public string supabaseKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImtodmltbHN3YmJteGN4cGpra2VzIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzY3Nzk4NzQsImV4cCI6MjA5MjM1NTg3NH0.Y6cAJKph0YMKCIsibNwJiN3t5IFm39iSQzWalbK5zPE";

    public Client Client { get; private set; }
    public bool IsInitialized { get; private set; }

    async void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            await InitSupabase();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private async Task InitSupabase()
    {
        try
        {
            // SDK 내부 호환성을 위해 /rest/v1/ 등이 붙어있다면 제거된 루트 URL 사용 권장
            var options = new SupabaseOptions { AutoConnectRealtime = true };
            Client = new Client(supabaseUrl, supabaseKey, options);
            
            // 안정적인 초기화 메커니즘
            await Client.InitializeAsync();
            
            IsInitialized = true;
            Debug.Log("✅ Supabase 보안 연결 및 초기화 완료!");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"❌ Supabase 연결 실패: {e.Message}");
        }
    }

    // --- 출시용 핵심 서버 로직 ---

    /// <summary>
    /// 유저 프로필 정보를 가져오거나, 없으면 자동 생성합니다. (SQL 트리거와 연동)
    /// </summary>
    public async Task<UserProfile> GetOrCreateProfile(string userId)
    {
        if (!IsInitialized) return null;

        try
        {
            var response = await Client.From<UserProfile>().Where(x => x.Id == userId).Get();
            
            if (response.Models.Count > 0)
            {
                return response.Models[0];
            }
            else
            {
                // SQL 트리거가 프로필을 생성할 시간을 위해 잠깐 대기 후 재시도
                await Task.Delay(1000);
                response = await Client.From<UserProfile>().Where(x => x.Id == userId).Get();
                return response.Models.Count > 0 ? response.Models[0] : null;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 프로필 로드 중 오류: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 게임 결과를 DB에 저장합니다. (RLS 보안 적용됨)
    /// </summary>
    public async Task SaveMatchResult(bool isWinner, int rank, int kills, float time)
    {
        if (!IsInitialized || Client.Auth.CurrentUser == null) return;

        var entry = new MatchEntry
        {
            PlayerId = Client.Auth.CurrentUser.Id,
            IsWinner = isWinner,
            Rank = rank,
            KillCount = kills,
            SurvivedTime = time
        };

        try
        {
            await Client.From<MatchEntry>().Insert(entry);
            Debug.Log("🏆 게임 결과가 성공적으로 서버에 기록되었습니다.");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 결과 저장 실패: {e.Message}");
        }
    }
}
