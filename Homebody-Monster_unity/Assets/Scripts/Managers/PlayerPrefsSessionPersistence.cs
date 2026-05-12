using UnityEngine;
using Newtonsoft.Json;
using Supabase.Gotrue;
using Supabase.Gotrue.Interfaces;

/// <summary>
/// Supabase Gotrue 세션을 Unity PlayerPrefs에 영속 저장하는 SessionHandler 구현체.
///
/// ─ 도입 배경 ─────────────────────────────────────────────────
/// 기존 SupabaseOptions는 SessionHandler를 지정하지 않아 Gotrue SDK가
/// 세션을 자동 저장/복원하지 않았음. 결과:
///   • 앱 재실행 시 항상 로그인 화면 → 사용자 경험 저하
///   • AutoRefreshToken=true 설정이 무의미 (저장된 세션이 없으므로 갱신 대상 없음)
///   • 익명 계정 1회 사용 후 동일 기기 재실행 시 새 계정 생성
///     → 피자/부활권/전적/닉네임 등 사용자 데이터 분실 위험
///
/// ─ 동작 ──────────────────────────────────────────────────────
///   SaveSession  : 로그인 성공 시 SDK가 호출 → Session을 JSON 직렬화 후 PlayerPrefs에 저장
///   LoadSession  : Client.InitializeAsync()에서 자동 호출 → PlayerPrefs에서 복원
///   DestroySession: SignOut 또는 토큰 만료 시 SDK가 호출 → 키 삭제
///
/// ─ 보안 메모 ──────────────────────────────────────────────────
/// PlayerPrefs는 평문 저장(Windows Registry, Android SharedPreferences, iOS NSUserDefaults).
/// access_token / refresh_token이 디바이스 내에서 평문으로 노출되는 점은 모바일 게임
/// 표준 트레이드오프(대부분의 Unity 게임이 동일 방식 사용). 출시 후 한 단계 더 강화하려면
/// Android: EncryptedSharedPreferences, iOS: Keychain을 별도 native plugin으로 연동.
/// </summary>
public class PlayerPrefsSessionPersistence : IGotrueSessionPersistence<Session>
{
    private const string SessionKey = "homebody.supabase.session.v1";

    public void SaveSession(Session session)
    {
        if (session == null) { DestroySession(); return; }
        try
        {
            string json = JsonConvert.SerializeObject(session);
            PlayerPrefs.SetString(SessionKey, json);
            PlayerPrefs.Save();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[SessionPersistence] SaveSession 실패: {e.Message}");
        }
    }

    public void DestroySession()
    {
        if (PlayerPrefs.HasKey(SessionKey))
        {
            PlayerPrefs.DeleteKey(SessionKey);
            PlayerPrefs.Save();
        }
    }

    public Session LoadSession()
    {
        if (!PlayerPrefs.HasKey(SessionKey)) return null;
        try
        {
            string json = PlayerPrefs.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            return JsonConvert.DeserializeObject<Session>(json);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[SessionPersistence] LoadSession 실패(키 제거): {e.Message}");
            DestroySession();
            return null;
        }
    }
}
