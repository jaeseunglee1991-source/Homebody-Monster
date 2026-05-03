using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

// ════════════════════════════════════════════════════════════════
//  ORM 모델 — match_history, leaderboard_kills view, ban_logs
//
//  ⚠️ 컬럼명은 실제 Supabase DB 스키마와 정확히 일치해야 합니다.
//     match_history: player_id / is_winner / kill_count / survived_time / created_at
//     leaderboard_kills: user_id / nickname / total_kills / wins  (VIEW)
//     ban_logs: user_id / nickname / reason / banned_at
// ════════════════════════════════════════════════════════════════

[System.Serializable]
[Supabase.Postgrest.Attributes.Table("match_history")]
public class MatchHistoryRecord : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [JsonProperty("id")]               public int      Id          { get; set; }

    // DB 컬럼명: player_id (profiles.id 참조)
    [Supabase.Postgrest.Attributes.Column("player_id")]
    [JsonProperty("player_id")]        public string   PlayerId    { get; set; }

    // DB 컬럼명: is_winner
    [Supabase.Postgrest.Attributes.Column("is_winner")]
    [JsonProperty("is_winner")]        public bool     IsWin       { get; set; }

    [Supabase.Postgrest.Attributes.Column("rank")]
    [JsonProperty("rank")]             public int      Rank        { get; set; }

    // DB 컬럼명: kill_count
    [Supabase.Postgrest.Attributes.Column("kill_count")]
    [JsonProperty("kill_count")]       public int      Kills       { get; set; }

    // DB 컬럼명: survived_time (real/float, 초 단위)
    [Supabase.Postgrest.Attributes.Column("survived_time")]
    [JsonProperty("survived_time")]    public float    SurvivalSeconds { get; set; }

    // DB 컬럼명: created_at
    [Supabase.Postgrest.Attributes.Column("created_at")]
    [JsonProperty("created_at")]       public DateTime PlayedAt    { get; set; }
}

[System.Serializable]
[Supabase.Postgrest.Attributes.Table("leaderboard_kills")]
public class LeaderboardRecord : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("user_id", false)]
    [JsonProperty("user_id")]         public string UserId      { get; set; }
    [Supabase.Postgrest.Attributes.Column("nickname")]
    [JsonProperty("nickname")]        public string Nickname    { get; set; }
    [Supabase.Postgrest.Attributes.Column("total_kills")]
    [JsonProperty("total_kills")]     public int    TotalKills  { get; set; }
    [Supabase.Postgrest.Attributes.Column("wins")]
    [JsonProperty("wins")]            public int    Wins        { get; set; }
}

[System.Serializable]
[Supabase.Postgrest.Attributes.Table("ban_logs")]
public class BanLogRecord : Supabase.Postgrest.Models.BaseModel
{
    [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
    [JsonProperty("id")]              public long   Id        { get; set; }
    [Supabase.Postgrest.Attributes.Column("user_id")]
    [JsonProperty("user_id")]         public string UserId    { get; set; }
    [Supabase.Postgrest.Attributes.Column("nickname")]
    [JsonProperty("nickname")]        public string Nickname  { get; set; }
    [Supabase.Postgrest.Attributes.Column("reason")]
    [JsonProperty("reason")]          public string Reason    { get; set; }
    [Supabase.Postgrest.Attributes.Column("banned_at")]
    [JsonProperty("banned_at")]       public DateTime BannedAt { get; set; }
}

// ════════════════════════════════════════════════════════════════
//  SupabaseManager — 리더보드 / 전적 / 밴 확장 메서드 (partial class)
// ════════════════════════════════════════════════════════════════

public partial class SupabaseManager
{
    // ── CurrentUserId 헬퍼 ──────────────────────────────────────
    /// <summary>현재 로그인된 사용자의 UUID를 반환합니다.</summary>
    public string CurrentUserId => Client?.Auth?.CurrentUser?.Id;

    // ════════════════════════════════════════════════════════════
    //  match_history
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 매치 결과를 저장합니다.
    ///
    /// ⚠️ match_history 테이블은 INSERT RLS가 service_role 전용으로 잠겨 있습니다.
    ///    직접 INSERT 대신 기존 save_match_result RPC를 통해 저장합니다.
    ///    (save_match_result는 SECURITY DEFINER로 auth.uid() 검증 후 INSERT 수행)
    ///
    /// NotifyMatchResultClientRpc → SaveMatchResultAsync 경로에서 이미 호출되므로
    /// LeaderboardManager에서 이 메서드를 직접 호출하면 이중 저장이 됩니다.
    /// LeaderboardManager.SubmitMatchResult()는 DB 저장을 하지 않으며,
    /// 이 메서드는 별도 경로(로비 전적 새로고침 등)에서만 사용하세요.
    /// </summary>
    public async Task InsertMatchHistory(string userId, bool isWin, int rank, int kills, int survivalSeconds)
    {
        if (!IsInitialized || Client == null)
        {
            Debug.LogWarning("[SupabaseManager] InsertMatchHistory: 초기화되지 않음.");
            return;
        }

        // save_match_result RPC 위임 (직접 INSERT는 RLS로 차단됨)
        string roomId = GameManager.Instance?.currentRoomId
                        ?? $"manual_{DateTime.UtcNow:yyyyMMddHHmmss}";

        var parameters = new Dictionary<string, object>
        {
            { "p_room_id",       roomId       },
            { "p_is_winner",     isWin        },
            { "p_rank",          rank         },
            { "p_kill_count",    kills        },
            { "p_survived_time", (double)survivalSeconds }
        };

        try
        {
            await Client.Rpc("save_match_result", parameters);
            Debug.Log($"[SupabaseManager] match_history(RPC) 저장 완료: rank={rank}, kills={kills}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SupabaseManager] InsertMatchHistory(RPC) 실패: {e.Message}");
            throw;
        }
    }

    /// <summary>
    /// 내 매치 전적을 최신순으로 20개 조회합니다.
    /// RLS: player_id = auth.uid() 조건으로 본인 데이터만 반환됩니다.
    /// </summary>
    public async Task<List<MatchHistoryRecord>> FetchMyMatchHistory(string userId)
    {
        if (!IsInitialized || Client == null) return new List<MatchHistoryRecord>();

        try
        {
            // DB 컬럼명 player_id 기준 필터 (기존 RLS SELECT 정책과 일치)
            var response = await Client.From<MatchHistoryRecord>()
                .Filter("player_id", Supabase.Postgrest.Constants.Operator.Equals, userId)
                .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(20)
                .Get();
            return response.Models.ToList();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SupabaseManager] FetchMyMatchHistory 실패: {e.Message}");
            throw;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  leaderboard_kills (View)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 리더보드 Top 50을 조회합니다. (leaderboard_kills VIEW 사용)
    /// </summary>
    public async Task<List<LeaderboardRecord>> FetchLeaderboard()
    {
        if (!IsInitialized || Client == null) return new List<LeaderboardRecord>();

        try
        {
            var response = await Client.From<LeaderboardRecord>()
                .Order("total_kills", Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(50)
                .Get();
            return response.Models.ToList();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SupabaseManager] FetchLeaderboard 실패: {e.Message}");
            throw;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  ban_logs (서버 전용 — Service Role Key 환경에서만 동작)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 치트 감지 시 ban_logs 테이블에 기록합니다.
    /// 데디케이티드 서버에서 SUPABASE_SERVICE_ROLE_KEY로 초기화된 경우에만 INSERT됩니다.
    /// 일반 클라이언트(anon/authenticated)는 RLS에 의해 차단됩니다.
    /// </summary>
    public async Task LogCheatBan(string userId, string nickname, string reason)
    {
        if (!IsInitialized || Client == null) return;

        var record = new BanLogRecord
        {
            UserId   = userId,
            Nickname = nickname,
            Reason   = reason,
            BannedAt = DateTime.UtcNow
        };

        try
        {
            await Client.From<BanLogRecord>().Insert(record);
            Debug.Log($"[SupabaseManager] ban_logs 기록: userId={userId}, reason={reason}");
        }
        catch (Exception e)
        {
            // 일반 클라이언트(비 service_role)에서는 정상적으로 실패 — Error 대신 Warning
            Debug.LogWarning($"[SupabaseManager] ban_logs INSERT 실패 (서버 전용): {e.Message}");
        }
    }
}
