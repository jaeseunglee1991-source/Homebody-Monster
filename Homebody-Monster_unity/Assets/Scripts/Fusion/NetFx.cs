using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [Phase 4-D] 클라 로컬 연출 — 데미지 팝업 + 킬피드 (OnGUI).
/// 실 게임의 DamagePopupPool/킬피드 HUD의 PoC 단순판(프리팹/TMP 배선 없이 OnGUI로 렌더).
/// 호스트(StateAuthority)가 NetPlayer에서 RPC로 이벤트를 모든 피어에 브로드캐스트하면 여기서 표시.
/// </summary>
public class NetFx : MonoBehaviour
{
    public static NetFx Instance { get; private set; }

    private struct Popup { public Vector3 worldPos; public string text; public Color color; public float born; }
    private struct Feed  { public string text; public float born; }

    private readonly List<Popup> _popups = new();
    private readonly List<Feed>  _feed   = new();

    private const float PopupLife = 1.0f;
    private const float FeedLife  = 5.0f;
    private const int   FeedMax   = 6;

    private GUIStyle _popupStyle;
    private GUIStyle _feedStyle;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    // ── 이벤트 추가 (NetPlayer RPC에서 호출) ────────────────────
    public static void AddDamagePopup(Vector3 worldPos, float amount)
    {
        if (Instance == null || amount <= 0f) return;
        Instance._popups.Add(new Popup
        {
            worldPos = worldPos,
            text     = Mathf.RoundToInt(amount).ToString(),
            color    = new Color(1f, 0.92f, 0.4f), // 노란 데미지 숫자
            born     = Time.time
        });
    }

    public static void AddKillFeed(string killer, string victim)
    {
        // [Pass B] 캔버스 HUD가 활성이면 InGameHUD의 TMP 킬피드로 위임(OnGUI 중복 방지).
        // InGameHUD.ShowKillFeed는 로컬 플레이어가 공격자면 노란색으로 강조한다.
        if (NetHudBridge.Active && InGameHUD.Instance != null)
        {
            InGameHUD.Instance.ShowKillFeed(string.IsNullOrEmpty(killer) ? "☠" : killer, victim);
            return;
        }

        if (Instance == null) return;
        string txt = string.IsNullOrEmpty(killer) ? $"[자멸] {victim}" : $"{killer} ☠ {victim}";
        Instance._feed.Add(new Feed { text = txt, born = Time.time });
        if (Instance._feed.Count > FeedMax) Instance._feed.RemoveAt(0);
    }

    // ── 렌더 ────────────────────────────────────────────────────
    private void OnGUI()
    {
        EnsureStyles();
        DrawPopups();
        DrawFeed();
    }

    private void EnsureStyles()
    {
        if (_popupStyle == null)
            _popupStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        _popupStyle.fontSize = Mathf.Max(16, Screen.height / 40);

        if (_feedStyle == null)
            _feedStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        _feedStyle.fontSize = Mathf.Max(14, Screen.height / 52);
    }

    private void DrawPopups()
    {
        var cam = Camera.main;
        var prev = GUI.color;
        for (int i = _popups.Count - 1; i >= 0; i--)
        {
            float age = Time.time - _popups[i].born;
            if (age > PopupLife) { _popups.RemoveAt(i); continue; }
            if (cam == null) continue;

            float   t  = age / PopupLife;
            Vector3 wp = _popups[i].worldPos + Vector3.up * (0.6f + t * 1.4f); // 상승
            Vector3 sp = cam.WorldToScreenPoint(wp);
            if (sp.z < 0f) continue;

            var col = _popups[i].color; col.a = 1f - t; // 페이드
            _popupStyle.normal.textColor = col;
            GUI.Label(new Rect(sp.x - 80f, Screen.height - sp.y - 20f, 160f, 40f), _popups[i].text, _popupStyle);
        }
        GUI.color = prev;
    }

    private void DrawFeed()
    {
        float w  = Mathf.Min(Screen.width * 0.4f, 520f);
        float lh = _feedStyle.fontSize * 1.6f;
        float x  = Screen.width - w - 16f;
        float y  = Screen.height * 0.12f;

        for (int i = _feed.Count - 1; i >= 0; i--)
        {
            float age = Time.time - _feed[i].born;
            if (age > FeedLife) { _feed.RemoveAt(i); continue; }
            float a = Mathf.Clamp01((FeedLife - age) / 0.8f);
            _feedStyle.normal.textColor = new Color(1f, 0.85f, 0.85f, a);
            GUI.Label(new Rect(x, y, w, lh), _feed[i].text, _feedStyle);
            y += lh;
        }
    }
}
