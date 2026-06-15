using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// [Pass B] 기존 캔버스 HUD(InGameHUD, TMP)를 Fusion 상태(NetPlayer/NetMatch)로 구동하는 어댑터.
/// NGO 전용 InitPlayerUI/PlayerController 경로를 타지 않고, 매 프레임 NetPlayer/NetMatch에서
/// 값을 읽어 InGameHUD의 표시 메서드(UpdateHealthBar/UpdateSurvivorCount/UpdateTimer/킬피드/배너)와
/// 스킬 버튼(라벨·쿨다운·클릭→Fusion 스킬)을 직접 배선한다.
///
/// 역할 분담(중복 방지):
///  • InGameHUD(캔버스): 체력바·생존자·타이머·스킬버튼+쿨다운·킬피드·종료배너 ← 이 스크립트가 구동
///  • NetHUD/NetMatch/NetFx/NetMobileInput(OnGUI): <see cref="Active"/>가 true면 겹치는 부분을 스스로 숨김
///    (머리위 미니체력바·리롤·부활·관전·종료버튼·데미지팝업·조이스틱·탭공격은 그대로 유지)
///
/// InGameHUD가 씬에 없으면 Active=false → 모든 OnGUI HUD가 패스 A처럼 그대로 동작(안전 폴백).
/// </summary>
public class NetHudBridge : MonoBehaviour
{
    /// <summary>캔버스 HUD가 활성으로 구동 중인지 — OnGUI HUD들이 중복 표시를 피하기 위해 참조.</summary>
    public static bool Active { get; private set; }

    /// <summary>캔버스 부활 패널이 표시 중인지 — NetHUD가 OnGUI 부활/관전 라벨 중복을 피하기 위해 참조.</summary>
    public static bool ReviveActive { get; private set; }

    [Tooltip("비우면 InGameHUD.Instance 자동 사용")]
    public InGameHUD hud;

    private NetPlayer _local;
    private NetMatch  _match;

    private float[]   _cdMax;            // 슬롯별 쿨다운 최대값(쿨다운 fill 비율용)
    private NetPlayer _wiredFor;         // 스킬 버튼을 배선한 로컬 플레이어
    private int       _wiredSkillVersion = -1; // 리롤 감지(스킬셋 바뀌면 재배선)
    private bool      _bannerShown;
    private NetPlayer _reviveWiredFor;   // 부활 패널 버튼을 배선한 로컬 플레이어

    private void OnDisable() { Active = false; ReviveActive = false; }
    private void OnDestroy() { Active = false; ReviveActive = false; }

    private void Update()
    {
        if (hud == null) hud = InGameHUD.Instance;
        if (hud == null) { Active = false; return; }
        Active = true;

        var match = Match();
        if (match != null)
        {
            hud.UpdateSurvivorCount(match.AliveCount, CountPlayers());

            // 시간제한이 있으면 남은 시간(카운트다운). 준비/리롤 윈도우(15초) 동안엔 CombatElapsed=0이라
            // 만시간(02:00)에 멈춰 있다가 전투 시작과 함께 감소. 무제한이면 전투 경과 시간 표시.
            if (match.timeLimitSeconds > 0f)
            {
                float t = match.HasStarted
                    ? Mathf.Max(0f, match.timeLimitSeconds - match.CombatElapsed)
                    : match.timeLimitSeconds;
                hud.UpdateTimer(t);
            }
            else if (match.HasStarted)
            {
                hud.UpdateTimer(match.CombatElapsed);
            }
            DriveEndBanner(match);
        }

        var local = LocalPlayer();

        // 조작 UI(스킬버튼 루트) — 사망/관전 중엔 숨김(NGO SpectatorManager 대체).
        if (hud.controlsRoot != null)
            hud.controlsRoot.SetActive(local != null && !local.IsDead);

        DriveRevive(local);          // 사망+제안 시 캔버스 부활 패널
        HandleSurrenderInput(local); // Esc/Android Back → 항복 확인 패널

        if (local == null) return;

        hud.UpdateHealthBar(local.Hp, local.MaxHp);
        EnsureSkillButtons(local);
        DriveSkillCooldowns(local);
    }

    // ── 부활 패널 (사망 + 제안/처리 중) ─────────────────────────
    private void DriveRevive(NetPlayer local)
    {
        var panel = hud.revivePanel;
        if (panel == null) { ReviveActive = false; return; }

        bool show = local != null && local.IsDead &&
                    (local.ReviveOffered || local.ReviveProcessing);

        if (!show)
        {
            if (panel.activeSelf) panel.SetActive(false);
            _reviveWiredFor = null;
            ReviveActive    = false;
            return;
        }

        if (!panel.activeSelf) panel.SetActive(true);
        ReviveActive = true;

        // 버튼 배선(이 사망/제안에 1회)
        if (_reviveWiredFor != local)
        {
            _reviveWiredFor = local;
            var captured = local;
            if (hud.reviveButton != null)
            {
                hud.reviveButton.onClick.RemoveAllListeners();
                hud.reviveButton.onClick.AddListener(() => captured.AcceptReviveRpc());
            }
            if (hud.giveUpButton != null)
            {
                hud.giveUpButton.onClick.RemoveAllListeners();
                hud.giveUpButton.onClick.AddListener(() => captured.GiveUpReviveRpc());
            }
        }

        if (local.ReviveProcessing)
        {
            if (hud.reviveTimerText != null) hud.reviveTimerText.text = "";
            if (hud.reviveInfoText  != null) hud.reviveInfoText.text  = "🍕 부활 처리 중...";
            if (hud.reviveButton != null) hud.reviveButton.interactable = false;
            if (hud.giveUpButton != null) hud.giveUpButton.interactable = false;
        }
        else // ReviveOffered
        {
            if (hud.reviveTimerText != null)
                hud.reviveTimerText.text = Mathf.CeilToInt(local.ReviveRemaining).ToString();

            // 보유 부활권은 로그인 시점 캐시(부정확할 수 있음 — DB가 진실). 안내만 하고
            // 버튼은 항상 활성: 실제 차감 가부는 호스트/DB가 판정(무료 부활 금지 로직 유지).
            int owned     = GameManager.Instance != null ? GameManager.Instance.reviveTicketCount : 0;
            int remaining = NetMatch.MaxReviveCount - (_match != null ? _match.ReviveUsedCount : 0);
            if (hud.reviveInfoText != null)
                hud.reviveInfoText.text = owned > 0
                    ? $"보유 즉시부활권: {owned}장  |  매치 잔여: {remaining}/{NetMatch.MaxReviveCount}"
                    : "보유 부활권이 없을 수 있습니다(로비에서 획득).\n사용 시도는 가능하나 차감 실패 시 부활되지 않습니다.";
            if (hud.reviveButton != null) hud.reviveButton.interactable = true;
            if (hud.giveUpButton != null) hud.giveUpButton.interactable = true;
        }
    }

    // ── 항복(Surrender) — Esc/Android Back으로 확인 패널 토글 ────
    private void HandleSurrenderInput(NetPlayer local)
    {
        var panel = hud.surrenderConfirmPanel;
        if (panel == null) return;

        var kb = UnityEngine.InputSystem.Keyboard.current;
        bool back = kb != null && kb.escapeKey.wasPressedThisFrame;
        if (!back) return;

        // 이미 떠 있으면 닫기(토글).
        if (panel.activeSelf) { panel.SetActive(false); return; }

        // 살아있고 전투 잠금(준비시간)이 아닐 때만, 부활 패널이 없을 때만.
        if (local == null || local.IsDead || local.CombatLocked) return;
        if (ReviveActive) return;

        panel.SetActive(true);
        var captured = local;
        if (hud.surrenderConfirmButton != null)
        {
            hud.surrenderConfirmButton.onClick.RemoveAllListeners();
            hud.surrenderConfirmButton.onClick.AddListener(() =>
            {
                captured.RequestSurrenderRpc();
                if (hud.surrenderConfirmPanel != null) hud.surrenderConfirmPanel.SetActive(false);
            });
        }
        if (hud.surrenderCancelButton != null)
        {
            hud.surrenderCancelButton.onClick.RemoveAllListeners();
            hud.surrenderCancelButton.onClick.AddListener(() =>
            {
                if (hud.surrenderConfirmPanel != null) hud.surrenderConfirmPanel.SetActive(false);
            });
        }
    }

    // ── 종료 배너 (WINNER + 내 결과) ────────────────────────────
    private void DriveEndBanner(NetMatch match)
    {
        if (match.Phase != 1)
        {
            // 재시작 등으로 Phase가 0으로 돌아오면 배너를 닫는다(이전: 닫지 않아 새 게임에 잔존).
            if (_bannerShown)
            {
                _bannerShown = false;
                if (hud.endBannerPanel != null) hud.endBannerPanel.SetActive(false);
            }
            return;
        }

        string winner = match.WinnerName.ToString();
        string head   = string.IsNullOrEmpty(winner) ? "매치 종료 (무승부)" : $"🏆 WINNER: {winner}";
        string mine   = NetMatch.LocalResultText;
        string msg    = string.IsNullOrEmpty(mine) ? head : head + "\n" + mine;

        if (!_bannerShown)
        {
            _bannerShown = true;
            hud.ShowGameEndBanner(msg, playResultBGM: true);
        }
        else if (hud.endBannerText != null)
        {
            // 결과 RPC가 배너 표시보다 한 틱 늦게 도착할 수 있으므로 계속 갱신.
            hud.endBannerText.text = msg;
        }
    }

    // ── 스킬 버튼 배선 (로컬/캐릭터가 바뀔 때 1회) ───────────────
    private void EnsureSkillButtons(NetPlayer local)
    {
        int count = local.LocalSkillCount;
        // 리롤로 스킬셋이 바뀌면(버전 변화) 같은 NetPlayer라도 재배선 — 라벨 stale 방지.
        if (_wiredFor == local && _wiredSkillVersion == local.LocalSkillVersion) return;
        _wiredFor          = local;
        _wiredSkillVersion = local.LocalSkillVersion;

        var btns  = hud.skillButtons;
        var names = hud.skillNameTexts;
        var fills = hud.skillCooldownFills;
        if (btns == null) return;

        _cdMax = new float[btns.Length];
        for (int i = 0; i < btns.Length; i++)
        {
            if (btns[i] == null) continue;

            if (i < count)
            {
                int slot = i + 1; // NetPlayer 슬롯은 1~4 (0=평타)
                btns[i].gameObject.SetActive(true);
                btns[i].onClick.RemoveAllListeners();
                var captured = local;
                btns[i].onClick.AddListener(() => captured.UseSkillAimed(slot));

                if (names != null && i < names.Length && names[i] != null)
                    names[i].text = local.LocalSkillLabel(slot);

                _cdMax[i] = local.LocalSkillCooldownMax(slot);
                if (fills != null && i < fills.Length && fills[i] != null)
                    fills[i].fillAmount = 0f;
            }
            else
            {
                btns[i].gameObject.SetActive(false);
            }
        }
    }

    // ── 쿨다운 fill + 버튼 활성 상태 ────────────────────────────
    private void DriveSkillCooldowns(NetPlayer local)
    {
        var btns  = hud.skillButtons;
        var fills = hud.skillCooldownFills;
        if (btns == null || _cdMax == null) return;

        int  count  = local.LocalSkillCount;
        bool locked = local.IsDead || local.CombatLocked;

        for (int i = 0; i < btns.Length && i < _cdMax.Length; i++)
        {
            if (btns[i] == null || i >= count) continue;
            int   slot = i + 1;
            float cd   = local.CooldownRemaining(slot);

            if (fills != null && i < fills.Length && fills[i] != null)
                fills[i].fillAmount = _cdMax[i] > 0f ? Mathf.Clamp01(cd / _cdMax[i]) : 0f;

            btns[i].interactable = !locked && cd <= 0f;
        }
    }

    // ── 조회 헬퍼 ───────────────────────────────────────────────
    private NetPlayer LocalPlayer()
    {
        if (_local != null) return _local; // Unity-null이면 파괴됨 → 재탐색
        foreach (var p in FindObjectsByType<NetPlayer>(FindObjectsSortMode.None))
            if (p.HasInputAuthority) { _local = p; break; }
        return _local;
    }

    private NetMatch Match()
    {
        if (_match == null) _match = FindFirstObjectByType<NetMatch>();
        return _match;
    }

    private static int CountPlayers()
        => FindObjectsByType<NetPlayer>(FindObjectsSortMode.None).Length;
}
