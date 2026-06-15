using UnityEngine;

/// <summary>
/// [Phase 2-B·2-C] 클라 로컬 HUD (OnGUI).
///  • 좌하단: 내 체력바 + 스킬 쿨다운 (평타/1~4)
///  • 머리 위: 모든 플레이어 미니 체력바 + 닉네임 (로컬=노란색 "(나)")
///  • 사망 시: "관전 중" 안내 (Tab 대상 전환)
/// </summary>
public class NetHUD : MonoBehaviour
{
    private NetPlayer           _local;
    private NetCameraFollow     _camFollow;
    private NetCinemachineTarget _cineTarget; // [Pass B] Cinemachine 사용 시 관전 대상 출처
    private NetMatch            _match;
    private GUIStyle        _label;
    private GUIStyle        _overhead;

    private NetPlayer LocalPlayer()
    {
        if (_local != null) return _local; // Unity-null이면 파괴됨 → 재탐색
        foreach (var p in FindObjectsByType<NetPlayer>(FindObjectsSortMode.None))
            if (p.HasInputAuthority) { _local = p; break; }
        return _local;
    }

    private void EnsureStyles()
    {
        if (_label == null)
            _label = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = Color.white }
            };
        _label.fontSize = Mathf.Max(14, Screen.height / 58); // 3-D: 축소

        if (_overhead == null)
            _overhead = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.LowerCenter,
            };
        _overhead.fontSize = Mathf.Max(10, Screen.height / 88); // 3-D: 축소
    }

    private void OnGUI()
    {
        EnsureStyles();
        DrawOverheadBars();

        var p = LocalPlayer();
        if (p == null) return;

        // [Pass B] 캔버스 HUD(InGameHUD)가 체력바·스킬 쿨다운을 그리면 OnGUI 패널은 생략(중복 방지).
        if (!NetHudBridge.Active) DrawLocalPanel(p);
        DrawRerollWindow(p);     // [3-E] 매치 시작 준비 시간 — 리롤 패널

        // [Pass B] 부활 UI: 캔버스가 활성이면 NetHudBridge(InGameHUD.revivePanel)가 담당 → OnGUI 생략.
        bool reviveShown = NetHudBridge.Active ? NetHudBridge.ReviveActive : DrawReviveOffer(p);
        if (!reviveShown) DrawSpectateLabel(p);
    }

    // ── [3-E] 리롤 윈도우 패널 (매치 시작 후 15초) ──────────────
    private void DrawRerollWindow(NetPlayer local)
    {
        if (local.IsDead) return;
        if (_match == null) _match = FindFirstObjectByType<NetMatch>();
        if (_match == null || !_match.RerollOpen) return;

        int   fs = Mathf.Max(18, Screen.height / 34);
        float w  = Screen.width;
        var center = new GUIStyle(_label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize  = fs,
        };

        center.normal.textColor = new Color(1f, 0.85f, 0.2f);
        GUI.Label(new Rect(0, Screen.height * 0.22f, w, fs * 1.6f),
            $"⏳ 준비 시간 {_match.RerollRemaining:0}초 — 전투 잠금", center);

        center.normal.textColor = Color.white;
        GUI.Label(new Rect(0, Screen.height * 0.22f + fs * 1.7f, w, fs * 1.5f),
            $"내 캐릭터: {(JobType)local.Job} · HP {local.MaxHp:0}", center);

        var btn = new GUIStyle(GUI.skin.button) { fontSize = (int)(fs * 0.95f) };
        float bw = w * 0.42f, bh = fs * 2.2f;
        float by = Screen.height * 0.22f + fs * 3.4f;

        if (local.RerollUsedLocal)
        {
            GUI.enabled = false;
            GUI.Button(new Rect((w - bw) / 2f, by, bw, bh), "리롤 사용됨", btn);
            GUI.enabled = true;
        }
        else if (GUI.Button(new Rect((w - bw) / 2f, by, bw, bh),
                 $"🍕{CharacterRerollSystem.RerollCostPizza} 리롤", btn))
        {
            local.RequestRerollLocal();
        }
    }

    // ── [3-D] 부활권 선택 UI (사망 + 제안 상태에서만) ───────────
    private bool DrawReviveOffer(NetPlayer local)
    {
        if (!local.IsDead) return false;

        int   fs = Mathf.Max(18, Screen.height / 34);
        float w  = Screen.width;
        var center = new GUIStyle(_label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize  = fs,
        };

        if (local.ReviveProcessing)
        {
            center.normal.textColor = new Color(1f, 0.85f, 0.2f);
            GUI.Label(new Rect(0, Screen.height * 0.3f, w, fs * 2f), "🍕 부활 처리 중...", center);
            return true;
        }

        if (!local.ReviveOffered) return false;

        center.normal.textColor = Color.white;
        GUI.Label(new Rect(0, Screen.height * 0.26f, w, fs * 2f),
            $"부활하시겠습니까? ({local.ReviveRemaining:0}초)", center);

        var btn = new GUIStyle(GUI.skin.button) { fontSize = (int)(fs * 0.95f) };
        float bw = w * 0.34f, bh = fs * 2.2f;
        float by = Screen.height * 0.26f + fs * 2.4f;

        if (GUI.Button(new Rect(w / 2f - bw - 8f, by, bw, bh), "🍕 부활권 사용", btn))
            local.AcceptReviveRpc();
        if (GUI.Button(new Rect(w / 2f + 8f, by, bw, bh), "포기", btn))
            local.GiveUpReviveRpc();
        return true;
    }

    // ── [4-G] 상태이상 글리프 문자열 (머리 위 표시) ─────────────
    private static string StatusGlyphs(NetPlayer p)
    {
        var s = p.Status;
        if (s == null) return "";
        // 일부 이모지는 BMP 밖(서로게이트 페어)이라 char 리터럴 불가 → 문자열로 결합.
        var sb = new System.Text.StringBuilder();
        if (s.IsImmune)          sb.Append("🧊"); // 면역(얼음방패)
        if (s.IsStunned)         sb.Append("⚡"); // 스턴
        if (s.IsSlowed)          sb.Append("❄"); // 슬로우
        if (s.IsPoisoned)        sb.Append("☣"); // 독
        if (s.HasShield)         sb.Append("🛡"); // 실드
        if (s.IsInDefenseStance) sb.Append("🔰"); // 방어태세
        if (s.IsInUndyingRage)   sb.Append("🔥"); // 불굴의 분노
        if (s.HasDivineGrace)    sb.Append("✨"); // 신의 가호
        if (s.HasGuardianAngel)  sb.Append("😇"); // 수호천사
        return sb.ToString();
    }

    // ── 머리 위 미니 체력바 + 닉네임 (모든 플레이어) ─────────────
    private void DrawOverheadBars()
    {
        var cam = Camera.main;
        if (cam == null) return;

        float bw = Mathf.Max(60f, Screen.width * 0.055f);
        float bh = Mathf.Max(6f,  Screen.height * 0.008f);
        var   prev = GUI.color;

        foreach (var p in FindObjectsByType<NetPlayer>(FindObjectsSortMode.None))
        {
            Vector3 sp = cam.WorldToScreenPoint(p.transform.position + Vector3.up * 0.9f);
            if (sp.z < 0f) continue; // 카메라 뒤
            float gx = sp.x - bw / 2f;
            float gy = Screen.height - sp.y;

            bool  isLocal = p.HasInputAuthority;
            float frac    = p.MaxHp > 0f ? Mathf.Clamp01(p.Hp / p.MaxHp) : 0f;
            bool  marked  = p.Status != null && p.Status.IsDeathMarked; // [4-F] 낙인

            // 닉네임 (바 위) — 낙인 시 ☠ 접두 + 보라
            _overhead.normal.textColor = p.IsDead ? Color.gray
                : marked  ? new Color(0.8f, 0.4f, 1f)
                : isLocal ? new Color(1f, 0.9f, 0.2f) : Color.white;
            string name = (marked ? "☠ " : "") + (isLocal ? $"{p.Nickname} (나)" : p.Nickname.ToString());
            GUI.Label(new Rect(sp.x - bw, gy - bh - _overhead.fontSize * 1.6f, bw * 2f, _overhead.fontSize * 1.5f),
                name, _overhead);

            // [4-G] 상태이상 글리프 (닉네임 위 한 줄) — 사망/은신(노출 방지) 시 생략.
            if (!p.IsDead && !(p.Status != null && p.Status.IsStealthy))
            {
                string glyphs = StatusGlyphs(p);
                if (glyphs.Length > 0)
                {
                    var prevC = _overhead.normal.textColor;
                    _overhead.normal.textColor = Color.white;
                    GUI.Label(new Rect(sp.x - bw, gy - bh - _overhead.fontSize * 3.0f, bw * 2f, _overhead.fontSize * 1.5f),
                        glyphs, _overhead);
                    _overhead.normal.textColor = prevC;
                }
            }

            // 체력바 — [Fix] 적의 체력 정보 노출 차단: 본인(InputAuthority)에게만 표시.
            if (isLocal)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.DrawTexture(new Rect(gx, gy - bh, bw, bh), Texture2D.whiteTexture);
                GUI.color = p.IsDead ? Color.gray
                    : Color.Lerp(new Color(0.85f, 0.15f, 0.15f), new Color(0.25f, 0.85f, 0.3f), frac);
                GUI.DrawTexture(new Rect(gx, gy - bh, bw * frac, bh), Texture2D.whiteTexture);
                GUI.color = prev;
            }
        }
    }

    // ── 좌하단: 내 체력바 + 스킬 쿨다운 ─────────────────────────
    private void DrawLocalPanel(NetPlayer p)
    {
        float w    = Screen.width * 0.32f;
        float h    = Mathf.Max(18f, Screen.height * 0.028f);
        float x    = 24f;
        float y    = Screen.height - h - 24f;
        float frac = p.MaxHp > 0f ? Mathf.Clamp01(p.Hp / p.MaxHp) : 0f;
        var   prev = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = p.IsDead
            ? Color.gray
            : Color.Lerp(new Color(0.8f, 0.1f, 0.1f), new Color(0.2f, 0.85f, 0.3f), frac);
        GUI.DrawTexture(new Rect(x, y, w * frac, h), Texture2D.whiteTexture);
        GUI.color = prev;
        GUI.Label(new Rect(x + 8f, y, w, h), $"{p.Nickname}  {p.Hp:0}/{p.MaxHp:0}  ⚔{p.KillCount}", _label);

        // 스킬 쿨다운 (체력바 위 한 줄) — [4-C] 실제 굴린 스킬 이름
        int cols = 1 + p.LocalSkillCount; // 평타 + 스킬 슬롯
        float cy = y - h - 8f;
        float cw = w / Mathf.Max(1, cols);
        for (int i = 0; i < cols; i++)
        {
            float  cd   = p.CooldownRemaining(i);
            string name = i == 0 ? "평타" : p.LocalSkillLabel(i);
            GUI.color = cd > 0f ? new Color(0.3f, 0.3f, 0.3f, 0.7f) : new Color(0.1f, 0.45f, 0.7f, 0.85f);
            GUI.DrawTexture(new Rect(x + i * cw, cy, cw - 3f, h), Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(new Rect(x + i * cw + 4f, cy, cw, h), cd > 0f ? $"{name} {cd:0.0}" : name, _label);
        }
    }

    // ── 사망 → 관전 안내 ────────────────────────────────────────
    private void DrawSpectateLabel(NetPlayer local)
    {
        if (!local.IsDead) return;

        var style = new GUIStyle(_label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize  = Mathf.Max(18, Screen.height / 38), // 3-D: 축소
        };
        style.normal.textColor = new Color(1f, 0.55f, 0.45f);

        // [Pass B] 관전 대상 — NetCameraFollow(옵션 A) 또는 NetCinemachineTarget(Cinemachine 유지) 중 존재하는 쪽.
        NetPlayer target = null;
        if (_camFollow == null) _camFollow = FindFirstObjectByType<NetCameraFollow>();
        if (_camFollow != null) target = _camFollow.CurrentTarget;
        else
        {
            if (_cineTarget == null) _cineTarget = FindFirstObjectByType<NetCinemachineTarget>();
            if (_cineTarget != null) target = _cineTarget.CurrentTarget;
        }

        string watching = "";
        if (target != null && target != local)
            watching = $" — {target.Nickname} 관전 중 (Tab 전환)";

        GUI.Label(new Rect(0, Screen.height * 0.12f, Screen.width, style.fontSize * 2f),
            $"☠ 사망{watching}", style);
    }
}
