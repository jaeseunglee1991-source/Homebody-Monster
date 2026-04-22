using UnityEngine;
using Supabase;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;

// ════════════════════════════════════════════════════════════
//  데이터 모델 (UserProfile)
// ════════════════════════════════════════════════════════════
[System.Serializable]
public class UserProfile
{
    [JsonProperty("id")]         public string Id        { get; set; }
    [JsonProperty("nickname")]   public string Nickname  { get; set; }
    [JsonProperty("win_count")]  public int    WinCount  { get; set; }
    [JsonProperty("lose_count")] public int    LoseCount { get; set; }
}

/// <summary>
/// 보안이 강화된 Supabase 매니저.
/// 직접적인 테이블 INSERT 대신 RPC(서버 함수)를 사용합니다.
/// </summary>
public class SupabaseManager : MonoBehaviour
{
    public static SupabaseManager Instance { get; private set; }

    [Header("⚠️ Supabase 접속 정보 — Inspector에서 입력")]
    public string supabaseUrl;
    public string supabaseAnonKey;

    public Client Client      { get; private set; }
    public bool   IsInitialized { get; private set; }

    async void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            EnsureMainThreadDispatcher(); 
            await InitSupabase();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void EnsureMainThreadDispatcher()
    {
        if (FindObjectOfType<MainThreadDispatcher>() == null)
        {
            var go = new GameObject("[MainThreadDispatcher]");
            go.AddComponent<MainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }
    }

    private async Task InitSupabase()
    {
        if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseAnonKey))
        {
            Debug.LogError("❌ SupabaseManager: URL 또는 Key가 설정되지 않았습니다. Inspector를 확인하세요.");
            return;
        }

        try
        {
            var options = new SupabaseOptions { AutoConnectRealtime = true };
            Client = new Client(supabaseUrl, supabaseAnonKey, options);
            await Client.InitializeAsync();
            IsInitialized = true;
            Debug.Log("✅ Supabase 초기화 완료");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"❌ Supabase 초기화 실패: {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════
    //  프로필 조회
    // ════════════════════════════════════════════════════════
    public async Task<UserProfile> GetOrCreateProfile(string userId)
    {
        if (!IsInitialized) return null;

        try
        {
            var response = await Client
                .From<UserProfile>("profiles")
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, userId)
                .Single();

            return response;
        }
        catch
        {
            await Task.Delay(500); // 트리거 대기
            try
            {
                return await Client
                    .From<UserProfile>("profiles")
                    .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, userId)
                    .Single();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"⚠️ 프로필 로드 실패: {e.Message}");
                return null;
            }
        }
    }

    // ════════════════════════════════════════════════════════
    //  게임 결과 저장 (RPC 사용 — 보안성 향상)
    // ════════════════════════════════════════════════════════
    public async Task SaveMatchResult(bool isWinner, int rank, int kills, float survivedTime)
    {
        if (!IsInitialized || Client.Auth.CurrentUser == null) return;

        string roomId = GameManager.Instance?.currentRoomId ?? "unknown";

        var parameters = new Dictionary<string, object>
        {
            { "p_room_id",       roomId     },
            { "p_is_winner",     isWinner   },
            { "p_rank",          rank       },
            { "p_kill_count",    kills      },
            { "p_survived_time", survivedTime }
        };

        try
        {
            // 직접 INSERT 대신 서버 측의 save_match_result 함수를 호출
            await Client.Rpc("save_match_result", parameters);
            Debug.Log("🏆 게임 결과 서버 저장 완료 (RPC)");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 결과 저장 실패: {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════
    //  닉네임 중복 확인 (RPC 사용)
    // ════════════════════════════════════════════════════════
    public async Task<bool> IsNicknameAvailable(string nickname)
    {
        if (!IsInitialized) return false;

        try
        {
            var result = await Client.Rpc("check_nickname_available",
                new Dictionary<string, object> { { "p_nickname", nickname } });

            if (result?.Content != null)
                return bool.TryParse(result.Content.Trim('"'), out bool available) && available;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"⚠️ 닉네임 확인 실패: {e.Message}");
        }
        return false;
    }
}
