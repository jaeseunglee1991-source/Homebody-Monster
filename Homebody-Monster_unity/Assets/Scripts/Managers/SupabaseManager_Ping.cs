using UnityEngine;
using System;
using System.Threading.Tasks;

// ════════════════════════════════════════════════════════════════
//  SupabaseManager — 세션 핑 로그 확장 (partial class)
//
//  DB 테이블: session_ping_logs
//  활용:
//    1. 지역별 레이턴시 분포 분석 → 최적 서버 위치 결정
//    2. 고핑 유저 집계 → 지역 매칭 우선순위 조정
//    3. 특정 시간대 서버 부하 파악
//
//  보안:
//    RLS INSERT: auth.uid() = player_id → 본인 데이터만 삽입 가능
//    RLS SELECT: service_role 전용 (클라이언트 직접 조회 불가)
//
// ─────────────────────────────────────────────────────────────────
//  ⚠️ Supabase SQL 마이그레이션 필요 (Editor 주석 하단 참고)
// ─────────────────────────────────────────────────────────────────
//
//  Fix-8  SessionPingLog private → internal class
//         Supabase ORM Reflection이 private class에 접근 실패
//         → TypeInitializationException / JsonSerializationException 방지
//
//  Fix-9  데디케이티드 서버에서 SaveSessionPing 호출 차단.
//         서버 프로세스는 auth.uid()가 없어 RLS INSERT 거부됨.
//         IsServer && !IsClient 가드로 불필요한 DB 요청 차단.
//
//  Fix-11 GetOrDetectRegion async Task<string> → string (동기 반환).
//         Application.systemLanguage는 순수 동기 API.
// ════════════════════════════════════════════════════════════════

/*
=== Supabase SQL (SQL Editor에 붙여넣고 실행하세요) ===

CREATE TABLE IF NOT EXISTS public.session_ping_logs (
    id              BIGSERIAL    PRIMARY KEY,
    player_id       UUID         NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    room_id         TEXT         NOT NULL,
    avg_ping_ms     INT          NOT NULL CHECK (avg_ping_ms >= 0),
    packet_loss     REAL         NOT NULL DEFAULT 0
                                 CHECK (packet_loss >= 0 AND packet_loss <= 1),
    quality_grade   TEXT         NOT NULL DEFAULT 'unknown',
    region          TEXT         NOT NULL DEFAULT 'unknown',
    recorded_at     TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_ping_player   ON public.session_ping_logs (player_id);
CREATE INDEX IF NOT EXISTS idx_ping_recorded ON public.session_ping_logs (recorded_at DESC);
CREATE INDEX IF NOT EXISTS idx_ping_region   ON public.session_ping_logs (region, recorded_at DESC);

ALTER TABLE public.session_ping_logs ENABLE ROW LEVEL SECURITY;

CREATE POLICY "ping_logs_insert_own"
    ON public.session_ping_logs FOR INSERT
    WITH CHECK (auth.uid() = player_id);

=== SQL 끝 ===
*/

public partial class SupabaseManager
{
    // ── ORM 모델 ────────────────────────────────────────────────

    // [Fix-8] internal class: Supabase ORM Reflection 접근 보장
    [System.Serializable]
    [Supabase.Postgrest.Attributes.Table("session_ping_logs")]
    internal class SessionPingLog : Supabase.Postgrest.Models.BaseModel
    {
        [Supabase.Postgrest.Attributes.PrimaryKey("id", false)]
        [Newtonsoft.Json.JsonProperty("id")]
        public long Id { get; set; }

        [Supabase.Postgrest.Attributes.Column("player_id")]
        [Newtonsoft.Json.JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [Supabase.Postgrest.Attributes.Column("room_id")]
        [Newtonsoft.Json.JsonProperty("room_id")]
        public string RoomId { get; set; }

        [Supabase.Postgrest.Attributes.Column("avg_ping_ms")]
        [Newtonsoft.Json.JsonProperty("avg_ping_ms")]
        public int AvgPingMs { get; set; }

        [Supabase.Postgrest.Attributes.Column("packet_loss")]
        [Newtonsoft.Json.JsonProperty("packet_loss")]
        public float PacketLoss { get; set; }

        [Supabase.Postgrest.Attributes.Column("quality_grade")]
        [Newtonsoft.Json.JsonProperty("quality_grade")]
        public string QualityGrade { get; set; }

        [Supabase.Postgrest.Attributes.Column("region")]
        [Newtonsoft.Json.JsonProperty("region")]
        public string Region { get; set; }
    }

    // ── 지역 캐시 ────────────────────────────────────────────────
    private string _cachedRegion;

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 인게임 세션 평균 핑을 session_ping_logs 테이블에 저장합니다.
    /// NetworkPingMonitor.SaveSessionPingAsync()에서 호출됩니다.
    ///
    /// [Fix-9] 데디케이티드 서버(IsServer && !IsClient)는 auth.uid()가 없어
    /// RLS INSERT 거부되므로 사전 차단합니다.
    /// 호스트 클라이언트(IsServer && IsClient)는 정상 저장합니다.
    /// </summary>
    public async Task SaveSessionPing(string roomId, int avgPingMs, float packetLossRate)
    {
        // [Fix-9] 전용 데디케이티드 서버 → 저장 생략
        var netMgr = Unity.Netcode.NetworkManager.Singleton;
        if (netMgr != null && netMgr.IsServer && !netMgr.IsClient)
        {
            Debug.Log("[SupabaseManager] SaveSessionPing: 데디케이티드 서버 → 저장 생략.");
            return;
        }

        if (!IsInitialized || Client == null)
        {
            Debug.LogWarning("[SupabaseManager] SaveSessionPing: 초기화되지 않음.");
            return;
        }

        string userId = Client?.Auth?.CurrentUser?.Id;
        if (string.IsNullOrEmpty(userId))
        {
            Debug.LogWarning("[SupabaseManager] SaveSessionPing: 로그인 필요.");
            return;
        }

        string grade = avgPingMs < 60  ? "excellent"
                     : avgPingMs < 120 ? "good"
                     : avgPingMs < 200 ? "poor"
                                       : "critical";

        // [Fix-11] 동기 메서드로 변경된 GetOrDetectRegion 호출
        string region = GetOrDetectRegion();

        var record = new SessionPingLog
        {
            PlayerId     = userId,
            RoomId       = roomId ?? "unknown",
            AvgPingMs    = Mathf.Clamp(avgPingMs, 0, 9999),
            PacketLoss   = Mathf.Clamp01(packetLossRate),
            QualityGrade = grade,
            Region       = region
        };

        try
        {
            await Client.From<SessionPingLog>().Insert(record);
            Debug.Log($"[SupabaseManager] 세션 핑 로그 저장: {avgPingMs}ms / {grade} / {region}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SupabaseManager] 세션 핑 저장 실패 (무시): {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  내부 — 지역 감지 (동기, 캐시 우선)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 디바이스 언어 설정 기반으로 지역 코드를 추정합니다.
    /// 앱 실행 중 한 번만 감지해 캐시합니다.
    ///
    /// [Fix-11] async Task 제거 — Application.systemLanguage는 순수 동기 API.
    /// </summary>
    private string GetOrDetectRegion()
    {
        if (!string.IsNullOrEmpty(_cachedRegion)) return _cachedRegion;

        _cachedRegion = Application.systemLanguage switch
        {
            SystemLanguage.Korean             => "KR",
            SystemLanguage.Japanese           => "JP",
            SystemLanguage.ChineseSimplified  => "CN",
            SystemLanguage.ChineseTraditional => "TW",
            SystemLanguage.English            => "US",
            SystemLanguage.German             => "DE",
            SystemLanguage.French             => "FR",
            _                                 => "UNKNOWN"
        };

        return _cachedRegion;
    }
}
