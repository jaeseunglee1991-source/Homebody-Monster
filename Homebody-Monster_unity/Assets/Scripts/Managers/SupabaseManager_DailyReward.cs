using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;

// ════════════════════════════════════════════════════════════════
//  SupabaseManager — 일일 출석 보상 확장 (partial class)
//
//  DB RPC: claim_daily_reward() → JSON
//  반환 예시 (수령 성공):
//    { "already_claimed": false, "streak": 3, "reward_pizza": 20 }
//  반환 예시 (이미 수령):
//    { "already_claimed": true,  "streak": 3, "reward_pizza": 0  }
//
//  RLS: auth.uid() 기반 — 로그인된 유저 본인만 자신의 보상 수령 가능.
//  RPC는 SECURITY DEFINER로 서버 측에서 중복 수령 방지를 보장합니다.
//
//  Bug Fix #5  ParseRpcJson<T> 공통 헬퍼로 이중 직렬화 대응
//  Bug Fix #8  FetchTodayStatus 추가 (streak+claimed 동시 수신, 다른 기기 수령 감지)
//  Bug Fix #9  FetchTodayStatus 실패 시 Streak=0 반환 (로컬 캐시 보호)
// ════════════════════════════════════════════════════════════════
public partial class SupabaseManager
{
    // ════════════════════════════════════════════════════════════
    //  ClaimDailyReward
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 출석 보상을 수령합니다.
    /// SECURITY DEFINER RPC로 중복 방지·streak 계산·피자 지급을 원자적으로 처리합니다.
    ///
    /// [Bug Fix #5] RPC Content 파싱 개선
    /// Supabase C# SDK 버전에 따라 JSON 응답이
    ///   · 단순 JSON     : {"already_claimed":false,...}
    ///   · 이중 직렬화   : "{\"already_claimed\":false,...}"
    /// 두 형태로 내려올 수 있으므로 ParseRpcJson 헬퍼로 모두 처리합니다.
    ///
    /// 반환값:
    ///   null                        → 네트워크 / 서버 오류
    ///   AlreadyClaimed = true       → 오늘 이미 수령
    ///   AlreadyClaimed = false      → 수령 성공
    /// </summary>
    public async Task<DailyRewardResult> ClaimDailyReward()
    {
        if (!IsInitialized || Client == null)
        {
            Debug.LogWarning("[SupabaseManager] ClaimDailyReward: 초기화되지 않음.");
            return null;
        }

        if (Client.Auth?.CurrentUser == null)
        {
            Debug.LogWarning("[SupabaseManager] ClaimDailyReward: 로그인 필요.");
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
    //  FetchTodayStatus
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 오늘의 streak + claimed 여부를 함께 조회합니다 (보상 수령 없음).
    ///
    /// [Bug Fix #8] 다른 기기 수령 후 진입 시 버튼 오활성화 방지.
    /// [Bug Fix #9] 실패 시 Streak = 0 반환.
    ///   DB가 반환하는 최솟값은 항상 1이므로
    ///   0은 실패 신호로 안전하게 사용 — 호출부에서 로컬 캐시를 유지합니다.
    /// </summary>
    public async Task<DailyLoginStatus> FetchTodayStatus()
    {
        // [Bug Fix #9] 실패 기본값 Streak = 0 (DB 반환 최솟값 1과 구분)
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
    //  ParseRpcJson<T> — 내부 헬퍼
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// [Bug Fix #5] RPC JSON Content 파싱 공통 헬퍼.
    ///
    /// Supabase C# SDK 버전에 따라 JSON 객체 반환 RPC의 Content가
    ///   · 단순 JSON         : {"key":value}
    ///   · 이중 직렬화(string 래핑) : "{\"key\":value}"
    /// 두 가지로 내려올 수 있습니다.
    ///
    /// 1차: 이중 직렬화 unquote 후 파싱
    /// 2차: 단순 JSON 직접 파싱 (폴백)
    /// </summary>
    private static T ParseRpcJson<T>(string raw, string rpcName) where T : class
    {
        // 1차: 이중 직렬화 형태 처리
        try
        {
            string unquoted = raw.Trim().Trim('"').Replace("\\\"", "\"");
            var r1 = JsonConvert.DeserializeObject<T>(unquoted);
            if (r1 != null) return r1;
        }
        catch { /* 2차 폴백 */ }

        // 2차: 단순 JSON 형태 처리
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
