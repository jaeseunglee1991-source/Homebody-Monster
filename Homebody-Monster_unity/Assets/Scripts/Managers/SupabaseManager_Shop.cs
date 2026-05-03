using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ════════════════════════════════════════════════════════════════
//  SupabaseManager — 신규 기능 확장 (partial class)
//
//  이 파일이 추가하는 메서드:
//   1. UpdateLastLogin()   — last_login_at 갱신
//   2. SaveFcmToken()      — fcm_token 저장
//   3. SetTutorialDone()   — tutorial_done 저장
//   4. StartPrivateRoom()  — 방 시작 RPC
//   5. ClosePrivateRoom()  — 방 닫기 RPC
//   6. SetMemberReady()    — 준비 상태 변경 RPC
//
//  ⚠️ 아래 SQL을 Supabase SQL Editor에서 먼저 실행하세요.
// ════════════════════════════════════════════════════════════════
public partial class SupabaseManager
{
    // ════════════════════════════════════════════════════════════
    //  last_login_at 갱신
    // ════════════════════════════════════════════════════════════

    public async Task UpdateLastLogin()
    {
        if (!IsInitialized || Client?.Auth?.CurrentUser == null) return;
        try
        {
            await Client.Rpc("update_last_login", null);
            Debug.Log("[Supabase] last_login_at 갱신 완료");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Supabase] update_last_login 실패 (무시): {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  FCM 토큰 저장
    // ════════════════════════════════════════════════════════════

    public async Task SaveFcmToken(string token)
    {
        if (!IsInitialized || Client?.Auth?.CurrentUser == null
            || string.IsNullOrEmpty(token)) return;
        try
        {
            var param = new Dictionary<string, object> { { "p_token", token } };
            await Client.Rpc("save_fcm_token", param);
            Debug.Log("[Supabase] FCM 토큰 저장 완료");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Supabase] save_fcm_token 실패 (무시): {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  튜토리얼 완료 플래그 저장
    // ════════════════════════════════════════════════════════════

    public async Task SetTutorialDone()
    {
        if (!IsInitialized || Client?.Auth?.CurrentUser == null) return;
        try
        {
            await Client.Rpc("set_tutorial_done", null);
            Debug.Log("[Supabase] tutorial_done 저장 완료");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Supabase] set_tutorial_done 실패 (무시): {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  private_rooms 방 상태 변경 (NetworkRoomManager용)
    // ════════════════════════════════════════════════════════════

    public async Task<bool> StartPrivateRoom(string roomId, string serverIp)
    {
        if (!IsInitialized || Client?.Auth?.CurrentUser == null) return false;
        try
        {
            var param = new Dictionary<string, object>
            {
                { "p_room_id",   roomId   },
                { "p_server_ip", serverIp }
            };
            var result = await Client.Rpc("start_private_room", param);
            bool ok = result?.Content != null
                      && bool.TryParse(result.Content.Trim('"'), out bool v) && v;
            Debug.Log($"[Supabase] start_private_room: {ok}");
            return ok;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Supabase] start_private_room 실패: {e.Message}");
            return false;
        }
    }

    public async Task ClosePrivateRoom(string roomId)
    {
        if (!IsInitialized || Client?.Auth?.CurrentUser == null) return;
        try
        {
            var param = new Dictionary<string, object> { { "p_room_id", roomId } };
            await Client.Rpc("close_private_room", param);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Supabase] close_private_room 실패 (무시): {e.Message}");
        }
    }

    public async Task SetMemberReady(string roomId, bool isReady)
    {
        if (!IsInitialized || Client?.Auth?.CurrentUser == null) return;
        try
        {
            var param = new Dictionary<string, object>
            {
                { "p_room_id",  roomId  },
                { "p_is_ready", isReady }
            };
            await Client.Rpc("set_member_ready", param);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Supabase] set_member_ready 실패: {e.Message}");
        }
    }
}
