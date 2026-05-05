using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;

// ════════════════════════════════════════════════════════════════
//  SupabaseManager — 일일 보상 확장 (partial class)
//
//  이 파일이 추가하는 메서드:
//   1. ClaimDailyReward()   — 출석 보상 수령 RPC
//   2. FetchTodayStatus()   — 오늘 streak + claimed 조회 RPC
//   3. ParseRpcJson<T>()    — RPC JSON 파싱 헬퍼 (private, 이 partial 전체에서 공유)
//
//  대응 DB 함수:
//   · claim_daily_reward()        — INSERT DO NOTHING + ROW_COUNT (레이스 컨디션 수정 완료)
//   · fetch_today_login_status()  — 오늘/어제 기준 streak + claimed 반환
// ════════════════════════════════════════════════════════════════
public partial class SupabaseManager
{
    // ════════════════════════════════════════════════════════════
    //  출석 보상 수령
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 출석 보상을 수령합니다.
    /// SECURITY DEFINER RPC — 중복 방지·streak·피자 지급 원자 처리.
    /// [Bug Fix #10] SQL: INSERT DO NOTHING + ROW_COUNT로 레이스 컨디션 완전 방지.
    ///
    /// 반환값:
    ///   null                   → 네트워크 / 서버 오류
    ///   AlreadyClaimed = true  → 오늘 이미 수령 (피자 미지급)
    ///   AlreadyClaimed = false → 수령 성공 (피자 지급)
    /// </summary>
    public async Task<DailyRewardResult> ClaimDailyReward()
    {
        if (!IsInitialized || Client == null)
        {
            Debug.LogWarning("[SupabaseManager] ClaimDailyReward: 초기화되지 않음.");
            return null;
        }

        try
        {
            var response = await Client.Rpc("claim_daily_reward", new Dictionary<string, object>());
            if (response?.Content == null)
            {
                Debug.LogWarning("[SupabaseManager] claim_daily_reward: 응답 없음.");
                return null;
            }
            return ParseRpcJson<DailyRewardResult>(response.Content, "claim_daily_reward");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SupabaseManager] claim_daily_reward RPC 실패: {e.Message}");
            return null;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  오늘 상태 조회
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 오늘의 streak + claimed 여부를 함께 조회합니다 (보상 수령 없음).
    /// [Bug Fix #8] FetchCurrentStreak(int) 대체 — claimed 포함.
    /// [Bug Fix #9] 실패 기본값 Streak = 0 (DB 반환 최솟값 1과 구분).
    /// </summary>
    public async Task<DailyLoginStatus> FetchTodayStatus()
    {
        // [Bug Fix #9] Streak=0 = 실패 신호 (DB 반환 최솟값 1과 구분)
        var defaultStatus = new DailyLoginStatus { Streak = 0, Claimed = false };

        if (!IsInitialized || Client == null) return defaultStatus;
        if (string.IsNullOrEmpty(CurrentUserId)) return defaultStatus;

        try
        {
            var response = await Client.Rpc("fetch_today_login_status", new Dictionary<string, object>());
            if (response?.Content == null) return defaultStatus;

            var result = ParseRpcJson<DailyLoginStatus>(response.Content, "fetch_today_login_status");
            return result ?? defaultStatus;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SupabaseManager] FetchTodayStatus 실패: {e.Message}");
            return defaultStatus;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  RPC JSON 파싱 헬퍼
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// [Bug Fix #5] RPC JSON Content 파싱 공통 헬퍼.
    /// Supabase C# SDK 버전에 따라 두 가지 형태로 내려올 수 있습니다:
    ///   · 단순 JSON       : {"key":value}
    ///   · 이중 직렬화     : "{\"key\":value}"
    /// 1차(unquote 후 파싱) → 2차(직접 파싱) 폴백으로 모두 처리합니다.
    /// private static — SupabaseManager partial class 전체에서 공유됩니다.
    /// </summary>
    private static T ParseRpcJson<T>(string raw, string rpcName) where T : class
    {
        // 1차: 이중 직렬화 형태 처리 ("{\"key\":value}")
        try
        {
            string unquoted = raw.Trim().Trim('"').Replace("\\\"", "\"");
            var r1 = JsonConvert.DeserializeObject<T>(unquoted);
            if (r1 != null) return r1;
        }
        catch { /* 2차 폴백 */ }

        // 2차: 단순 JSON 형태 처리 ({"key":value})
        try
        {
            var r2 = JsonConvert.DeserializeObject<T>(raw);
            if (r2 != null) return r2;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SupabaseManager] {rpcName} JSON 파싱 실패: {e.Message}\nContent={raw}");
        }

        return null;
    }
}
