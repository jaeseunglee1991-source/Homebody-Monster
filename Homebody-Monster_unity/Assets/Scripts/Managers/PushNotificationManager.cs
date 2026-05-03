using UnityEngine;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;

/// <summary>
/// Firebase Cloud Messaging(FCM) 기반 푸시 알림 매니저.
///
/// ─ 의존 패키지 ───────────────────────────────────────────────
///  1. Firebase Unity SDK v11+  (#define: FIREBASE_MESSAGING)
///  2. Unity Mobile Notifications  (#define: UNITY_MOBILE_NOTIFICATIONS)
///
/// ─ Supabase 요구사항 ────────────────────────────────────────
///  profiles.fcm_token 컬럼 → SupabaseManager_Shop.SaveFcmToken() 으로 저장
/// </summary>
public class PushNotificationManager : MonoBehaviour
{
    public static PushNotificationManager Instance { get; private set; }

    private const string ChannelIdGeneral = "homebody_general";
    private const string ChannelIdReward  = "homebody_reward";

    private const int NotifIdDailyReward      = 1001;
    private const int NotifIdBackgroundReturn = 1002;

    private const float BackgroundReturnDelaySecs = 300f;

    private string _fcmToken = null;
    public  string FcmToken  => _fcmToken;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        InitializeNotificationChannels();
        InitializeFirebaseMessaging();
        ScheduleDailyRewardNotification();
        CheckLaunchFromNotification();
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
            ScheduleBackgroundReturnNotification();
        else
            CancelBackgroundReturnNotification();
    }

    private void OnApplicationQuit()
    {
        ScheduleBackgroundReturnNotification();
    }

    // ════════════════════════════════════════════════════════════
    //  Firebase Messaging 초기화
    // ════════════════════════════════════════════════════════════

    private void InitializeFirebaseMessaging()
    {
#if FIREBASE_MESSAGING
        Firebase.Messaging.FirebaseMessaging.TokenReceived   += OnTokenReceived;
        Firebase.Messaging.FirebaseMessaging.MessageReceived += OnMessageReceived;
        Firebase.Messaging.FirebaseMessaging.RequestPermissionAsync().ContinueWith(task =>
        {
            if (task.IsCompleted && !task.IsFaulted)
                Debug.Log("[Push] FCM 권한 요청 완료");
            else
                Debug.LogWarning("[Push] FCM 권한 요청 실패");
        });
        Debug.Log("[Push] Firebase Messaging 초기화 완료");
#else
        Debug.Log("[Push] FIREBASE_MESSAGING 심볼 없음 — 푸시 알림 비활성화 상태");
#endif
    }

#if FIREBASE_MESSAGING
    private void OnTokenReceived(object sender, Firebase.Messaging.TokenReceivedEventArgs e)
    {
        _fcmToken = e.Token;
        Debug.Log($"[Push] FCM 토큰 수신: {_fcmToken.Substring(0, Mathf.Min(10, _fcmToken.Length))}...");
        _ = SaveTokenToSupabase(_fcmToken);
    }

    private void OnMessageReceived(object sender, Firebase.Messaging.MessageReceivedEventArgs e)
    {
        var msg = e.Message;
        if (msg.Notification == null) return;
        Debug.Log($"[Push] 포그라운드 메시지: {msg.Notification.Title}");

        MainThreadDispatcher.Enqueue(() =>
        {
            Debug.Log($"[Push] 인앱 알림 표시: {msg.Notification.Title} / {msg.Notification.Body}");
        });
    }
#endif

    // ════════════════════════════════════════════════════════════
    //  Android 알림 채널 (Android 8.0+)
    // ════════════════════════════════════════════════════════════

    private void InitializeNotificationChannels()
    {
#if UNITY_MOBILE_NOTIFICATIONS && UNITY_ANDROID
        var generalChannel = new Unity.Notifications.Android.AndroidNotificationChannel
        {
            Id          = ChannelIdGeneral,
            Name        = "일반 알림",
            Importance  = Unity.Notifications.Android.Importance.Default,
            Description = "게임 복귀 유도 알림"
        };
        var rewardChannel = new Unity.Notifications.Android.AndroidNotificationChannel
        {
            Id          = ChannelIdReward,
            Name        = "보상 알림",
            Importance  = Unity.Notifications.Android.Importance.Default,
            Description = "일일 출석 보상 알림"
        };
        Unity.Notifications.Android.AndroidNotificationCenter.RegisterNotificationChannel(generalChannel);
        Unity.Notifications.Android.AndroidNotificationCenter.RegisterNotificationChannel(rewardChannel);
        Debug.Log("[Push] Android 알림 채널 등록 완료");
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  로컬 알림 — 일일 보상 리마인더
    // ════════════════════════════════════════════════════════════

    public void ScheduleDailyRewardNotification()
    {
#if UNITY_MOBILE_NOTIFICATIONS && UNITY_ANDROID
        Unity.Notifications.Android.AndroidNotificationCenter.CancelNotification(NotifIdDailyReward);

        DateTime now          = DateTime.Now;
        DateTime nextMidnight = now.Date.AddDays(1).AddMinutes(5);
        TimeSpan delay        = nextMidnight - now;

        var notification = new Unity.Notifications.Android.AndroidNotification
        {
            Title          = "🍕 오늘의 출석 보상이 기다리고 있어요!",
            Text           = "홈바디 몬스터에 접속해서 피자와 부활권을 받아가세요.",
            FireTime       = DateTime.Now.Add(delay),
            RepeatInterval = TimeSpan.FromDays(1),
            SmallIcon      = "ic_notification",
            LargeIcon      = "ic_launcher"
        };

        Unity.Notifications.Android.AndroidNotificationCenter.SendNotificationWithExplicitID(
            notification, ChannelIdReward, NotifIdDailyReward);

        Debug.Log($"[Push] 일일 보상 알림 예약 완료: {nextMidnight:MM/dd HH:mm}");
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  로컬 알림 — 백그라운드 복귀 유도
    // ════════════════════════════════════════════════════════════

    private void ScheduleBackgroundReturnNotification()
    {
#if UNITY_MOBILE_NOTIFICATIONS && UNITY_ANDROID
        Unity.Notifications.Android.AndroidNotificationCenter.CancelNotification(NotifIdBackgroundReturn);

        var notification = new Unity.Notifications.Android.AndroidNotification
        {
            Title     = "🎮 게임으로 돌아오세요!",
            Text      = "다른 몬스터들이 기다리고 있어요. 지금 바로 매칭을 시작해보세요!",
            FireTime  = DateTime.Now.AddSeconds(BackgroundReturnDelaySecs),
            SmallIcon = "ic_notification",
            LargeIcon = "ic_launcher"
        };

        Unity.Notifications.Android.AndroidNotificationCenter.SendNotificationWithExplicitID(
            notification, ChannelIdGeneral, NotifIdBackgroundReturn);
#endif
    }

    private void CancelBackgroundReturnNotification()
    {
#if UNITY_MOBILE_NOTIFICATIONS && UNITY_ANDROID
        Unity.Notifications.Android.AndroidNotificationCenter.CancelNotification(NotifIdBackgroundReturn);
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  알림에서 앱 진입 처리
    // ════════════════════════════════════════════════════════════

    public void CheckLaunchFromNotification()
    {
#if UNITY_MOBILE_NOTIFICATIONS && UNITY_ANDROID
        var intent = Unity.Notifications.Android.AndroidNotificationCenter.GetLastNotificationIntent();
        if (intent != null)
        {
            Debug.Log($"[Push] 알림으로 앱 진입 감지 (채널: {intent.Channel})");
            if (intent.Channel == ChannelIdReward)
                PlayerPrefs.SetInt("OpenDailyReward_OnLaunch", 1);
        }
#endif
    }

    // ════════════════════════════════════════════════════════════
    //  서버사이드 푸시 — Supabase Edge Function 경유
    // ════════════════════════════════════════════════════════════

    public static async Task SendMatchFoundPushAsync(string targetFcmToken)
    {
        if (string.IsNullOrEmpty(targetFcmToken)) return;
        if (SupabaseManager.Instance == null || !SupabaseManager.Instance.IsInitialized) return;

        try
        {
            var payload = new Dictionary<string, object>
            {
                { "token", targetFcmToken },
                { "title", "🎮 매칭 완료!" },
                { "body",  "홈바디 몬스터 배틀이 곧 시작됩니다. 지금 접속하세요!" }
            };
            string jsonBody = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            await SupabaseManager.Instance.Client.Functions.Invoke("send_push", jsonBody);
            Debug.Log("[Push] 매칭 완료 푸시 전송 성공");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Push] 매칭 완료 푸시 전송 실패 (무시 가능): {e.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  FCM 토큰 → Supabase 저장
    // ════════════════════════════════════════════════════════════

    private async Task SaveTokenToSupabase(string token)
    {
        try
        {
            for (int i = 0; i < 3; i++)
            {
                if (SupabaseManager.Instance != null && SupabaseManager.Instance.IsInitialized
                    && SupabaseManager.Instance.Client?.Auth?.CurrentUser != null)
                {
                    await SupabaseManager.Instance.SaveFcmToken(token);
                    return;
                }
                await Task.Delay(2000);
            }
            Debug.LogWarning("[Push] Supabase 초기화 대기 타임아웃 — FCM 토큰 저장 스킵");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Push] FCM 토큰 저장 실패 (무시): {e.Message}");
        }
    }
}
