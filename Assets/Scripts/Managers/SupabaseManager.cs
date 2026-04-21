using UnityEngine;
using Supabase;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;

// 1. 데이터 모델 정의 (유지보수를 위해 한 곳에서 관리)
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

    [Header("Supabase Settings")]
    [Tooltip("Supabase 프로젝트의 Project Settings > API에서 URL을 복사해 넣으세요.")]
    public string supabaseUrl = "YOUR_SUPABASE_URL"; 
    [Tooltip("Supabase 프로젝트의 Project Settings > API에서 anon key를 복사해 넣으세요.")]
    public string supabaseKey = "YOUR_SUPABASE_ANON_KEY";

    public Client Client { get; private set; }
    public bool IsInitialized { get; private set; }

    async void Awake()
    {
        // 싱글톤 패턴 설정
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
            var options = new SupabaseOptions { AutoConnectRealtime = true };
            Client = new Client(supabaseUrl, supabaseKey, options);
            
            // 안정적인 초기화 (제미나이 Pro 방식의 메커니즘 활용)
            await Client.InitializeAsync();
            
            IsInitialized = true;
            Debug.Log("✅ Supabase 시스템 초기화 완료!");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"❌ Supabase 연결 실패: {e.Message}");
        }
    }

    // --- 비즈니스 로직 유틸리티 ---

    // 유저 프로필 정보 가져오기 (없으면 자동 생성)
    public async Task<UserProfile> GetOrCreateProfile(string userId)
    {
        if (!IsInitialized) return null;

        var response = await Client.From<UserProfile>().Where(x => x.Id == userId).Get();
        
        if (response.Models.Count > 0)
        {
            return response.Models[0];
        }
        else
        {
            // 신규 유저일 경우 기본 프로필 생성
            var newProfile = new UserProfile 
            { 
                Id = userId, 
                Nickname = "Player_" + Random.Range(1000, 9999).ToString() 
            };
            
            var insertResponse = await Client.From<UserProfile>().Insert(newProfile);
            return insertResponse.Models[0];
        }
    }

    // 게임 결과 저장 (매치 전적 테이블 연동)
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
            Debug.Log("🏆 매치 결과가 DB에 성공적으로 저장되었습니다.");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 결과 저장 중 오류 발생: {e.Message}");
        }
    }
}
