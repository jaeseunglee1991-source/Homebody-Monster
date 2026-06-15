using UnityEngine;
using UnityEngine.InputSystem;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

/// <summary>
/// [Phase 4-B] 모바일 입력 레이어 — 가상 조이스틱 + 터치 타겟팅 + 스킬 버튼.
/// IMGUI는 멀티터치를 지원하지 않으므로 Input System(Touchscreen)으로 직접 처리하고
/// OnGUI는 시각화만 담당한다. 에디터에서는 마우스 폴백으로 동일하게 동작(WASD/키보드 1~4도 유지).
///
/// 포인터 다운 판정 순서: ① 스킬 버튼 → ② 적(타겟) 탭 = 평타 → ③ 좌측 영역 = 조이스틱 앵커.
/// 죽었거나 준비시간(전투 잠금)에는 버튼/평타를 막고, 이동(조이스틱)은 항상 허용.
/// </summary>
public class NetMobileInput : MonoBehaviour
{
    /// <summary>가상 조이스틱 방향(크기 0~1). PoCNetworkCallbacks.OnInput이 WASD와 합성한다.</summary>
    public static Vector2 JoystickDir { get; private set; }

    [Tooltip("조이스틱 인식 영역 — 화면 왼쪽 비율")]
    public float joystickZone = 0.4f;

    private NetPlayer _local;
    private bool      _joyActive;
    private int       _joyTouchId = int.MinValue; // 마우스는 -1
    private Vector2   _joyAnchor;                 // 화면좌표 (좌하 원점)

    private readonly Rect[] _btnRects = new Rect[4]; // GUI 좌표 (좌상 원점)

    private GUIStyle _btnStyle;

    private float JoyRadius => Screen.height * 0.10f;

    private NetPlayer Local()
    {
        if (_local != null) return _local; // Unity-null이면 파괴됨 → 재탐색
        foreach (var p in FindObjectsByType<NetPlayer>(FindObjectsSortMode.None))
            if (p.HasInputAuthority) { _local = p; break; }
        return _local;
    }

    private void Update()
    {
        var local = Local();
        if (local == null) { JoystickDir = Vector2.zero; _joyActive = false; return; }

        LayoutButtons();

        // ── 터치 (모바일, 멀티터치) ──────────────────────────────
        var ts = Touchscreen.current;
        bool anyTouch = false;
        if (ts != null)
        {
            foreach (var t in ts.touches)
            {
                var phase = t.phase.ReadValue();
                if (phase == TouchPhase.None) continue;
                anyTouch = true;

                int     id  = t.touchId.ReadValue();
                Vector2 pos = t.position.ReadValue();

                switch (phase)
                {
                    case TouchPhase.Began:                          OnPointerDown(id, pos, local); break;
                    case TouchPhase.Moved:
                    case TouchPhase.Stationary:                     OnPointerMove(id, pos);        break;
                    case TouchPhase.Ended:
                    case TouchPhase.Canceled:                       OnPointerUp(id);               break;
                }
            }
        }

        // ── 마우스 폴백 (에디터/PC) — 터치가 전혀 없을 때만 ──────
        if (!anyTouch && Mouse.current != null)
        {
            Vector2 pos = Mouse.current.position.ReadValue();
            if      (Mouse.current.leftButton.wasPressedThisFrame)  OnPointerDown(-1, pos, local);
            else if (Mouse.current.leftButton.isPressed)            OnPointerMove(-1, pos);
            else if (Mouse.current.leftButton.wasReleasedThisFrame) OnPointerUp(-1);
        }
    }

    private void OnPointerDown(int id, Vector2 posBL, NetPlayer local)
    {
        // [Pass B] 캔버스 UI(InGameHUD 스킬버튼/부활·포기 패널) 위 탭은 UGUI(EventSystem)가 처리한다.
        // 원시 터치를 그대로 월드 입력(평타/조이스틱)으로 흘리면 버튼을 누를 때 캐릭터가 움직이거나
        // 공격하는 충돌이 생기므로 무시.
        if (IsPointerOverUI(id)) return;

        bool combatAllowed = !local.IsDead && !local.CombatLocked;

        // ① 스킬 버튼 (GUI 좌표로 변환해 판정 — 굴린 슬롯 수만큼만)
        //    캔버스 HUD가 활성이면 InGameHUD의 UGUI 버튼이 스킬을 담당하므로 OnGUI 버튼 판정은 생략.
        if (combatAllowed && !NetHudBridge.Active)
        {
            Vector2 guiPos = new Vector2(posBL.x, Screen.height - posBL.y);
            int     slots  = Mathf.Min(_btnRects.Length, local.LocalSkillCount);
            for (int i = 0; i < slots; i++)
            {
                if (_btnRects[i].Contains(guiPos))
                {
                    local.UseSkillAimed(i + 1);
                    return;
                }
            }
        }

        // ② 적 탭 = 평타 (조이스틱 영역이라도 적이 손가락 밑에 있으면 공격 우선)
        if (combatAllowed && local.TryAttackAt(posBL)) return;

        // ③ 좌측 영역 = 조이스틱 앵커 (사망/준비시간에도 이동은 허용)
        if (!_joyActive && posBL.x < Screen.width * joystickZone)
        {
            _joyActive  = true;
            _joyTouchId = id;
            _joyAnchor  = posBL;
            JoystickDir = Vector2.zero;
        }
    }

    /// <summary>해당 포인터(터치 id 또는 마우스=-1)가 UGUI 요소 위에 있는지.</summary>
    private static bool IsPointerOverUI(int id)
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return false;
        return id >= 0 ? es.IsPointerOverGameObject(id) : es.IsPointerOverGameObject();
    }

    private void OnPointerMove(int id, Vector2 posBL)
    {
        if (!_joyActive || id != _joyTouchId) return;
        JoystickDir = Vector2.ClampMagnitude((posBL - _joyAnchor) / JoyRadius, 1f);
    }

    private void OnPointerUp(int id)
    {
        if (!_joyActive || id != _joyTouchId) return;
        _joyActive  = false;
        _joyTouchId = int.MinValue;
        JoystickDir = Vector2.zero;
    }

    // ── 레이아웃 / 시각화 ────────────────────────────────────────
    private void LayoutButtons()
    {
        float s      = Mathf.Max(56f, Screen.height * 0.085f);
        float gap    = s * 0.18f;
        float right  = Screen.width  - 16f;
        float bottom = Screen.height - 16f;

        // 우하단 2×2 그리드: [3실드][4충격] / [1돌진][2화염]
        _btnRects[0] = new Rect(right - s * 2f - gap, bottom - s, s, s);            // 1돌진 (좌하)
        _btnRects[1] = new Rect(right - s,            bottom - s, s, s);            // 2화염 (우하)
        _btnRects[2] = new Rect(right - s * 2f - gap, bottom - s * 2f - gap, s, s); // 3실드 (좌상)
        _btnRects[3] = new Rect(right - s,            bottom - s * 2f - gap, s, s); // 4충격 (우상)
    }

    private void OnGUI()
    {
        var local = Local();
        if (local == null) return;

        if (_btnStyle == null)
            _btnStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white }
            };
        _btnStyle.fontSize = Mathf.Max(13, Screen.height / 64);

        var prev = GUI.color;

        // 스킬 버튼 (실제 굴린 스킬 — 쿨다운/잠금 표시).
        // [Pass B] 캔버스 HUD가 활성이면 InGameHUD의 UGUI 버튼이 그리므로 OnGUI 버튼은 생략.
        if (!NetHudBridge.Active)
        {
            bool combatAllowed = !local.IsDead && !local.CombatLocked;
            int  slots = Mathf.Min(_btnRects.Length, local.LocalSkillCount);
            for (int i = 0; i < slots; i++)
            {
                float  cd    = local.CooldownRemaining(i + 1);
                string label = local.LocalSkillLabel(i + 1);
                GUI.color = (!combatAllowed || cd > 0f)
                    ? new Color(0.25f, 0.25f, 0.25f, 0.75f)
                    : new Color(0.12f, 0.45f, 0.72f, 0.9f);
                GUI.DrawTexture(_btnRects[i], Texture2D.whiteTexture);
                GUI.color = prev;
                GUI.Label(_btnRects[i], cd > 0f ? $"{label}\n{cd:0.0}" : label, _btnStyle);
            }
        }

        // 조이스틱 (활성 시)
        if (_joyActive)
        {
            float   r    = JoyRadius;
            Vector2 baseG = new Vector2(_joyAnchor.x, Screen.height - _joyAnchor.y);
            Vector2 knobG = baseG + new Vector2(JoystickDir.x, -JoystickDir.y) * r;

            GUI.color = new Color(1f, 1f, 1f, 0.15f);
            GUI.DrawTexture(new Rect(baseG.x - r, baseG.y - r, r * 2f, r * 2f), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.45f);
            float kr = r * 0.35f;
            GUI.DrawTexture(new Rect(knobG.x - kr, knobG.y - kr, kr * 2f, kr * 2f), Texture2D.whiteTexture);
            GUI.color = prev;
        }
    }
}
