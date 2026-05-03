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

        if (dt > 0f)
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

                if (rec.violationCount >= _kickThreshold)
                {
                    _ = BanAndKickAsync(clientId, sync, type);
                    return;
                }
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
    }

    // ════════════════════════════════════════════════════════════
    //  Ban & Kick
    // ════════════════════════════════════════════════════════════

    private async System.Threading.Tasks.Task BanAndKickAsync(
        ulong clientId, PlayerNetworkSync sync, string reason)
    {
        string userId   = GameManager.Instance?.currentPlayerId ?? sync?.ServerData?.playerName ?? "unknown";
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
