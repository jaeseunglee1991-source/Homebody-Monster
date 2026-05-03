using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 핑 기반 공격 판정 보정 시스템 (서버사이드 Lag Compensation).
///
/// 서버가 매 FixedUpdate마다 모든 플레이어 위치 스냅샷을 보관합니다.
/// 공격 요청 도착 시 공격자 RTT/2 이전 스냅샷으로 피격자 히트박스를 복원해 판정합니다.
///
/// [보안]
///  • 최대 롤백: MAX_ROLLBACK_MS(200ms) 하드 캡
///  • RecordSnapshot/UpdateClientRtt에 서버 가드(IsServer) 적용 (Fix-6)
///  • 스냅샷 데이터 자체가 ServerValidator를 통과한 검증된 위치
///
/// [배치] InGameScene의 빈 GameObject에 부착. NetworkObject 불필요(서버 전용 MonoBehaviour).
///
/// Fix-6  RecordSnapshot/UpdateClientRtt 서버 가드 추가.
///        클라이언트에서 Instance 접근 시 클라이언트 메모리에 스냅샷이 쌓이는 문제.
///        NetworkManager.Singleton null 안전 처리 포함.
/// </summary>
public class PingAdaptiveCombat : MonoBehaviour
{
    public static PingAdaptiveCombat Instance { get; private set; }

    /// <summary>최대 롤백 윈도우 (ms). 이 값을 초과하는 보정은 거부됩니다.</summary>
    public const int MAX_ROLLBACK_MS = 200;

    private const int RTT_BUFFER_MS = 80;

    private struct PositionSnapshot
    {
        public float   Timestamp;
        public Vector2 Position;
    }

    private readonly Dictionary<ulong, List<PositionSnapshot>> _snapshots
        = new Dictionary<ulong, List<PositionSnapshot>>();

    private readonly Dictionary<ulong, int> _clientRttCache
        = new Dictionary<ulong, int>();

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    // ════════════════════════════════════════════════════════════
    //  서버 전용 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 서버 FixedUpdate마다 각 플레이어 위치를 스냅샷으로 기록합니다.
    /// PlayerNetworkSync.FixedUpdate() 서버 분기에서 호출합니다.
    ///
    /// [Fix-6] 서버 가드: 클라이언트에서 호출해도 즉시 return.
    /// </summary>
    public void RecordSnapshot(ulong clientId, Vector2 position)
    {
        // [Fix-6] 서버 가드 — NetworkManager null 안전 처리 포함
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        if (!_snapshots.TryGetValue(clientId, out var list))
        {
            list = new List<PositionSnapshot>(64);
            _snapshots[clientId] = list;
        }

        list.Add(new PositionSnapshot { Timestamp = Time.time, Position = position });

        float cutoff = Time.time - (MAX_ROLLBACK_MS + RTT_BUFFER_MS) * 0.001f;
        while (list.Count > 0 && list[0].Timestamp < cutoff)
            list.RemoveAt(0);
    }

    /// <summary>
    /// 클라이언트의 최신 SmoothedRttMs를 서버 캐시에 업데이트합니다.
    /// NetworkPingMonitor.PingServerRpc에서 호출됩니다.
    ///
    /// [Fix-6] 서버 가드 포함.
    /// </summary>
    public void UpdateClientRtt(ulong clientId, int rttMs)
    {
        // [Fix-6] 서버 가드
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        _clientRttCache[clientId] = Mathf.Clamp(rttMs, 0, MAX_ROLLBACK_MS * 2);
    }

    /// <summary>
    /// 공격자의 핑을 고려해 피격자의 과거 위치를 반환합니다.
    /// SkillSystem의 범위 판정 보정에 활용합니다.
    /// </summary>
    public Vector2 GetRolledBackPosition(
        ulong attackerClientId, ulong targetClientId, Vector2 currentPosition)
    {
        if (!_clientRttCache.TryGetValue(attackerClientId, out int rtt))
            return currentPosition;

        float rollbackSec = Mathf.Clamp(rtt * 0.5f * 0.001f, 0f, MAX_ROLLBACK_MS * 0.001f);
        if (rollbackSec <= 0f) return currentPosition;

        if (!_snapshots.TryGetValue(targetClientId, out var list) || list.Count == 0)
            return currentPosition;

        return InterpolateSnapshot(list, Time.time - rollbackSec, currentPosition);
    }

    /// <summary>
    /// 원형 히트박스 기준 공격 판정 검증.
    /// 롤백 위치 → 현재 위치 순서로 이중 검증해 고속 이동 캐릭터도 커버합니다.
    /// </summary>
    public bool ValidateHit(
        ulong attackerClientId, ulong targetClientId,
        Vector2 attackOrigin, Vector2 targetCurrentPos, float hitRadius)
    {
        Vector2 rolledBack = GetRolledBackPosition(attackerClientId, targetClientId, targetCurrentPos);

        return Vector2.Distance(attackOrigin, rolledBack)       <= hitRadius
            || Vector2.Distance(attackOrigin, targetCurrentPos) <= hitRadius;
    }

    /// <summary>
    /// 플레이어 퇴장 시 스냅샷·RTT 데이터를 정리합니다.
    /// NetworkSpawnManager.HandleClientDisconnected 끝에서 호출하세요.
    /// </summary>
    public void RemovePlayer(ulong clientId)
    {
        _snapshots.Remove(clientId);
        _clientRttCache.Remove(clientId);
    }

    // ════════════════════════════════════════════════════════════
    //  내부 유틸
    // ════════════════════════════════════════════════════════════

    private static Vector2 InterpolateSnapshot(
        List<PositionSnapshot> list, float targetTime, Vector2 fallback)
    {
        if (targetTime <= list[0].Timestamp) return list[0].Position;
        if (targetTime >= list[list.Count - 1].Timestamp) return fallback;

        int lo = 0, hi = list.Count - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (list[mid].Timestamp <= targetTime) lo = mid;
            else hi = mid;
        }

        float duration = list[hi].Timestamp - list[lo].Timestamp;
        if (duration <= 0f) return list[lo].Position;

        float t = (targetTime - list[lo].Timestamp) / duration;
        return Vector2.Lerp(list[lo].Position, list[hi].Position, t);
    }

    // ════════════════════════════════════════════════════════════
    //  디버그 유틸 (개발 빌드 전용)
    // ════════════════════════════════════════════════════════════

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public int GetSnapshotCount(ulong clientId)
        => _snapshots.TryGetValue(clientId, out var list) ? list.Count : 0;

    public IReadOnlyDictionary<ulong, int> GetAllClientRtts() => _clientRttCache;
#endif
}
