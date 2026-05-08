using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// 서버 측 안티치트 검증기.
/// Inspector 값이 0이면 GameBalanceConfig 값을 사용하고,
/// 명시적으로 입력된 값이 있으면 그것을 우선합니다.
/// </summary>
public class ServerValidator : MonoBehaviour
{
    public static ServerValidator Instance { get; private set; }

    [Header("임계값 (0이면 GameBalanceConfig 값 사용)")]
    [Tooltip("이 값 이상의 이동 속도(유닛/초)는 속도핵으로 판정합니다. 0 = Config 값 사용")]
    public float maxSpeedThreshold   = 0f;
    [Tooltip("한 프레임에 이 거리 이상 이동하면 텔레포트로 판정합니다. 0 = Config 값 사용")]
    public float teleportThreshold   = 0f;
    [Tooltip("한 번에 가할 수 있는 최대 데미지 배수(baseAtk 기준). 0 = Config 값 사용")]
    public float maxDamageMultiplier = 0f;
    [Tooltip("이 횟수만큼 위반하면 강제 추방. 0 = Config 값 사용")]
    public int   kickThreshold       = 0;

    // ── 실제 적용 값 (Awake에서 결정) ───────────────────────
    private float _maxSpeed;
    private float _teleportDist;
    private float _maxDamageMult;
    private int   _kickThreshold;

    private struct PlayerRecord
    {
        public Vector2 lastPosition;
        public float   lastTime;
        public int     violationCount;
    }

    private readonly Dictionary<ulong, PlayerRecord> _records = new();

    // [Fix] 서버 권한 스킬 텔레포트 면제 윈도우(클라이언트 동기화 RTT 흡수용).
    // ShadowRaid 등 ForcePositionClientRpc 후 ClientNetworkTransform이 새 위치를
    // 서버에 동기화하기까지 RTT만큼 지연되어 정상 사용자가 텔레포트로 오판정됨.
    private readonly Dictionary<ulong, float> _skillTeleportUntil = new();
    private const float SkillTeleportGraceSeconds = 1.0f;

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        ApplyConfig();
    }

    private void ApplyConfig()
    {
        var cfg = GameBalanceConfig.Get();

        _maxSpeed      = (maxSpeedThreshold   > 0f) ? maxSpeedThreshold   : (cfg != null ? cfg.AntiCheat_MaxSpeed            : 12f);
        _teleportDist  = (teleportThreshold   > 0f) ? teleportThreshold   : (cfg != null ? cfg.AntiCheat_TeleportThreshold   : 8f);
        _maxDamageMult = (maxDamageMultiplier > 0f) ? maxDamageMultiplier : (cfg != null ? cfg.AntiCheat_MaxDamageMultiplier  : 5f);
        _kickThreshold = (kickThreshold       > 0)  ? kickThreshold       : (cfg != null ? cfg.AntiCheat_KickThreshold       : 5);

        Debug.Log($"[ServerValidator] 설정 적용: maxSpeed={_maxSpeed}, teleport={_teleportDist}, " +
                  $"maxDmgMult={_maxDamageMult}, kick={_kickThreshold}");
    }

    // ════════════════════════════════════════════════════════════
    //  공개 API
    // ════════════════════════════════════════════════════════════

    public void RecordAndValidatePosition(PlayerNetworkSync sync, Vector2 newPosition)
    {
        if (sync == null || !NetworkManager.Singleton.IsServer) return;

        ulong clientId = sync.OwnerClientId;

        if (!_records.TryGetValue(clientId, out PlayerRecord rec))
        {
            _records[clientId] = new PlayerRecord
            {
                lastPosition   = newPosition,
                lastTime       = Time.time,
                violationCount = 0
            };
            return;
        }

        float dt       = Time.time - rec.lastTime;
        float distance = Vector2.Distance(rec.lastPosition, newPosition);

        // [Fix] 서버 권한 스킬 텔레포트(ShadowRaid 등) 면제 처리.
        // 면제 윈도우 내에서는 위반 카운트를 증가시키지 않고 위치만 갱신.
        bool skillTpExempt = _skillTeleportUntil.TryGetValue(clientId, out float until)
                             && Time.time < until;

        if (dt > 0f && !skillTpExempt)
        {
            float speed    = distance / dt;
            bool  teleport = distance > _teleportDist && dt < 0.2f;
            bool  speedHack = speed > _maxSpeed;

            if (teleport || speedHack)
            {
                string type = teleport ? "텔레포트" : "속도핵";
                Debug.LogWarning($"[ServerValidator] {type} 감지: clientId={clientId}, speed={speed:F1}, dist={distance:F2}");
                rec.violationCount++;

                sync.ForcePositionClientRpc(rec.lastPosition);
                _records[clientId] = rec; // struct 복사본을 딕셔너리에 즉시 저장

                if (rec.violationCount >= _kickThreshold)
                {
                    _ = BanAndKickAsync(clientId, sync, type);
                    return;
                }
                return; // 위반 시 lastPosition/lastTime 갱신하지 않음
            }
        }

        rec.lastPosition   = newPosition;
        rec.lastTime       = Time.time;
        _records[clientId] = rec;
    }

    public float ValidateDamage(PlayerNetworkSync attacker, float rawDamage)
    {
        if (attacker == null || attacker.ServerData == null) return rawDamage;

        float maxAllowed = attacker.ServerData.baseAtk * _maxDamageMult;
        if (rawDamage > maxAllowed)
        {
            Debug.LogWarning($"[ServerValidator] 비정상 데미지 감지: clientId={attacker.OwnerClientId}, " +
                             $"raw={rawDamage:F1}, max={maxAllowed:F1}");
            ulong clientId = attacker.OwnerClientId;
            if (_records.TryGetValue(clientId, out PlayerRecord rec))
            {
                rec.violationCount++;
                _records[clientId] = rec;
                if (rec.violationCount >= _kickThreshold)
                    _ = BanAndKickAsync(clientId, attacker, "데미지핵");
            }
            return maxAllowed;
        }
        return rawDamage;
    }

    public void RemovePlayer(ulong clientId)
    {
        _records.Remove(clientId);
        _skillTeleportUntil.Remove(clientId);
    }

    /// <summary>
    /// [Fix] 스킬에 의한 서버 권한 텔레포트를 안티치트에 통보합니다.
    /// 이후 SkillTeleportGraceSeconds 동안 텔레포트/속도핵 검사를 건너뜁니다.
    /// SkillSystem.ShadowRaid 등 ForcePositionClientRpc 직전에 호출하세요.
    /// </summary>
    public void RegisterSkillTeleport(ulong clientId)
    {
        _skillTeleportUntil[clientId] = Time.time + SkillTeleportGraceSeconds;
    }

    // ════════════════════════════════════════════════════════════
    //  Ban & Kick
    // ════════════════════════════════════════════════════════════

    private async System.Threading.Tasks.Task BanAndKickAsync(
        ulong clientId, PlayerNetworkSync sync, string reason)
    {
        // [버그 수정] userId를 GameManager.currentPlayerId(서버 자신의 계정)에서
        // PlayerNetworkSync._serverUserId(클라이언트가 SubmitCharacterDataServerRpc로 전달한 ID)로 변경.
        // 데디케이티드 서버의 GameManager는 로그인 계정이 없으므로 currentPlayerId가 빈값이거나
        // 서버 관리자 계정 ID가 들어있어 치트 플레이어 ID가 ban_logs에 기록되지 않는 버그.
        string userId   = (!string.IsNullOrEmpty(sync?.ServerUserId) ? sync.ServerUserId : null)
                          ?? sync?.ServerData?.playerName
                          ?? "unknown";
        string nickname = sync?.NetworkNickname.Value.ToString() ?? "unknown";

        Debug.LogError($"[ServerValidator] BAN: clientId={clientId}, userId={userId}, 사유={reason}");

        if (SupabaseManager.Instance != null)
        {
            try
            {
                await SupabaseManager.Instance.LogCheatBan(userId, nickname, reason);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ServerValidator] ban_logs 기록 실패: {e.Message}");
            }
        }

        var netMgr = NetworkManager.Singleton;
        if (netMgr != null && netMgr.IsServer)
            netMgr.DisconnectClient(clientId);

        _records.Remove(clientId);
    }
}
