using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 데디케이티드 서버 권한으로 플레이어를 스폰하고 게임 시작을 조율합니다.
///
/// 동작 순서:
///  1. 서버: 클라이언트 접속마다 SpawnPlayer() 호출
///  2. 서버: expectedPlayerCount 모두 접속(또는 startTimeout 경과) 시 게임 시작
///  3. 서버: BeginGameClientRpc로 모든 클라이언트에 게임 시작 알림
///  4. 서버: 접속 해제된 클라이언트는 즉시 사망 처리 (게임 진행 유지)
/// </summary>
public class NetworkSpawnManager : NetworkBehaviour
{
    public static NetworkSpawnManager Instance { get; private set; }

    [Header("스폰 설정")]
    [Tooltip("NetworkObject + PlayerNetworkSync + PlayerController 가 붙은 플레이어 프리팹")]
    public GameObject playerPrefab;

    [Tooltip("플레이어 스폰 위치 배열 (maxPlayers만큼 배치 권장)")]
    public Transform[] spawnPoints;

    [Header("핑 모니터")]
    [Tooltip("NetworkObject + NetworkPingMonitor 컴포넌트가 붙은 프리팹. Assets/Prefabs/Network/PingMonitor_Host.prefab")]
    public GameObject pingMonitorPrefab;

    [Header("게임 시작 조건")]
    [Tooltip("예상 접속 인원. MatchmakingManager.maxPlayers와 동일하게 설정. 매칭 성사 시 PendingExpectedPlayerCount로 런타임 덮어쓰기 됩니다.")]
    public int expectedPlayerCount = 2;

    /// <summary>
    /// [Fix] 매칭이 성사된 실제 인원수. MatchmakingManager.ExecuteServerMatch가
    /// LoadScene 직전에 설정하고, OnNetworkSpawn에서 expectedPlayerCount로 적용한다.
    /// 0 이하면 Inspector 기본값을 그대로 사용.
    /// 정적 필드를 사용하는 이유: NetworkSpawnManager는 InGameScene 로드 후에만 존재하므로
    /// MatchmakingManager가 직접 인스턴스에 접근할 수 없다.
    /// </summary>
    public static int PendingExpectedPlayerCount = 0;

    [Tooltip("접속 대기 타임아웃(초). 초과 시 현재 인원으로 강제 시작.")]
    public float startTimeout = 30f;

    // ── 서버 전용 상태 ─────────────────────────────────────────
    private readonly Dictionary<ulong, PlayerNetworkSync> _players = new();
    private readonly List<int> _shuffledSpawnIndices = new();
    private bool  _gameStarted = false;

    // ════════════════════════════════════════════════════════════
    //  초기화
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        Debug.Log("[NetworkSpawnManager] Awake 호출됨");
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        // [추가] NetworkManager의 자동 플레이어 스폰 기능을 비활성화합니다.
        // 이렇게 해야 NetworkSpawnManager의 SpawnPlayer() 로직만 작동하여 중복 생성을 막고 정확한 위치에 스폰됩니다.
        var netManager = Unity.Netcode.NetworkManager.Singleton;
        if (netManager != null)
            netManager.NetworkConfig.PlayerPrefab = null;
    }

    public override void OnNetworkSpawn()
    {
        Debug.Log($"[NetworkSpawnManager] OnNetworkSpawn 호출됨 (IsServer: {IsServer})");
        if (!IsServer) return;

        // [Fix] 매칭 성사 인원으로 expectedPlayerCount 덮어쓰기.
        if (PendingExpectedPlayerCount > 0)
        {
            Debug.Log($"[NetworkSpawnManager] expectedPlayerCount {expectedPlayerCount} → {PendingExpectedPlayerCount} (매칭 성사 인원)");
            expectedPlayerCount = PendingExpectedPlayerCount;
            PendingExpectedPlayerCount = 0; // 다음 매치를 위해 초기화
        }

        NetworkManager.Singleton.OnClientConnectedCallback  += HandleClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;

        // [C] PingAdaptiveCombat 배치 누락 조기 감지
        if (PingAdaptiveCombat.Instance == null)
            Debug.LogWarning("[NetworkSpawnManager] ⚠ PingAdaptiveCombat이 씬에 없습니다. " +
                             "InGameScene Hierarchy에 GameObject를 추가하고 컴포넌트를 붙이세요.");

        // [Fix-6] 글로벌 단일 PingMonitor 스폰 제거.
        //   기존: 서버가 단일 NetworkObject를 Spawn() → 서버가 Owner가 되어
        //         IsOwner=true인 PingRoutine이 서버에서만 동작 → 클라이언트 RTT 미수집.
        //   수정: 클라이언트별로 SpawnPlayer()에서 SpawnWithOwnership(clientId) 한다.

        Debug.Log($"[NetworkSpawnManager] ☁️ 서버 준비. {expectedPlayerCount}명 대기 시작.");
        StartCoroutine(WaitForPlayersRoutine());
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;
        NetworkManager.Singleton.OnClientConnectedCallback  -= HandleClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    // ════════════════════════════════════════════════════════════
    //  서버 전용 — 접속/해제 처리
    // ════════════════════════════════════════════════════════════

    private void HandleClientConnected(ulong clientId)
    {
        if (!IsServer) return;

        // [FIX] 데디케이티드 서버가 자기 자신(서버)을 위해 플레이어를 스폰하는 버그 방지
        if (clientId == Unity.Netcode.NetworkManager.ServerClientId && !Unity.Netcode.NetworkManager.Singleton.IsHost)
        {
            Debug.Log("[NetworkSpawnManager] 데디케이티드 서버 본인 접속 감지 -> 플레이어 스폰 생략");
            return;
        }

        // [Fix #3] 게임 시작 후 접속한 클라이언트는 스폰하지 않고 즉시 연결 차단
        if (_gameStarted)
        {
            Debug.LogWarning($"[NetworkSpawnManager] 게임 진행 중 접속 시도(clientId={clientId}) → 연결 차단");
            NetworkManager.Singleton.DisconnectClient(clientId);
            return;
        }

        SpawnPlayer(clientId);
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        if (!IsServer || !_players.TryGetValue(clientId, out var sync)) return;

        _players.Remove(clientId);

        if (sync == null || sync.NetworkIsDead.Value) return;

        // 접속 해제 = 즉시 사망 처리 (남은 플레이어들의 게임이 계속되도록)
        sync.NetworkIsDead.Value = true;
        InGameManager.Instance?.OnPlayerDied(sync.GetComponent<PlayerController>());
        Debug.LogWarning($"[NetworkSpawnManager] 클라이언트 {clientId} 접속 해제 → 사망 처리");

        // [B] 핑 보정 시스템에서 이탈 클라이언트 데이터 정리
        PingAdaptiveCombat.Instance?.RemovePlayer(clientId);
    }

    // ════════════════════════════════════════════════════════════
    //  서버 전용 — 스폰 로직
    // ════════════════════════════════════════════════════════════

    private void SpawnPlayer(ulong clientId)
    {
        if (playerPrefab == null)
        {
            Debug.LogError("[NetworkSpawnManager] playerPrefab이 Inspector에 할당되지 않았습니다.");
            return;
        }

        Vector3    spawnPos = GetNextSpawnPoint();
        Quaternion spawnRot = Quaternion.identity;

        var obj    = Instantiate(playerPrefab, spawnPos, spawnRot);
        var netObj = obj.GetComponent<NetworkObject>();

        if (netObj == null)
        {
            Debug.LogError("[NetworkSpawnManager] playerPrefab에 NetworkObject 컴포넌트가 없습니다.");
            Destroy(obj);
            return;
        }

        // 클라이언트에게 소유권 부여 (씬 전환 시 자동 제거)
        netObj.SpawnAsPlayerObject(clientId, destroyWithScene: true);

        // [Fix-6] 클라이언트별 NetworkPingMonitor 스폰 (해당 클라이언트가 Owner).
        // 서버는 비-Owner 인스턴스로서 RPC만 라우팅하고, 측정은 각 클라이언트에서 수행한다.
        if (pingMonitorPrefab != null)
        {
            var pingObj    = Instantiate(pingMonitorPrefab);
            var pingNetObj = pingObj.GetComponent<NetworkObject>();
            if (pingNetObj != null)
            {
                pingNetObj.SpawnWithOwnership(clientId, destroyWithScene: true);
                Debug.Log($"[NetworkSpawnManager] PingMonitor 스폰 (owner clientId={clientId})");
            }
            else
            {
                Debug.LogError("[NetworkSpawnManager] pingMonitorPrefab에 NetworkObject 없음");
                Destroy(pingObj);
            }
        }

        var sync = obj.GetComponent<PlayerNetworkSync>();
        if (sync != null)
        {
            _players[clientId] = sync;
        }
        else
        {
            // [Fix #9] sync가 null이면 WaitForPlayersRoutine의 카운트 집계가 어긋나므로 명시적으로 기록
            Debug.LogError($"[NetworkSpawnManager] playerPrefab에 PlayerNetworkSync 컴포넌트가 없습니다! " +
                           $"(clientId={clientId}) → _players에 등록되지 않음. WaitForPlayersRoutine 카운트 불일치 발생 가능.");
        }

        Debug.Log($"[NetworkSpawnManager] 🎮 플레이어 스폰: clientId={clientId}, pos={spawnPos}");
    }

    public Vector3 GetNextSpawnPoint()
    {
        if (spawnPoints == null || spawnPoints.Length == 0) return Vector3.zero;

        // 셔플된 인덱스가 소진되면 다시 채워서 셔플 (Fisher-Yates)
        if (_shuffledSpawnIndices.Count == 0)
        {
            for (int i = 0; i < spawnPoints.Length; i++)
                _shuffledSpawnIndices.Add(i);

            for (int i = _shuffledSpawnIndices.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (_shuffledSpawnIndices[i], _shuffledSpawnIndices[j])
                    = (_shuffledSpawnIndices[j], _shuffledSpawnIndices[i]);
            }
        }

        int idx = _shuffledSpawnIndices[0];
        _shuffledSpawnIndices.RemoveAt(0);
        return spawnPoints[idx] != null ? spawnPoints[idx].position : Vector3.zero;
    }

    // ════════════════════════════════════════════════════════════
    //  서버 전용 — 게임 시작 조율
    // ════════════════════════════════════════════════════════════

    private IEnumerator WaitForPlayersRoutine()
    {
        float elapsed = 0f;

        // 모든 플레이어가 접속하거나 타임아웃이 될 때까지 대기
        while (_players.Count < expectedPlayerCount && elapsed < startTimeout)
        {
            // [보완] 실제 InGameManager에 등록된 인원까지 함께 체크하여 안정성 강화
            int totalRegistered = InGameManager.Instance != null ? InGameManager.Instance.AliveCount : _players.Count;
            if (totalRegistered >= expectedPlayerCount) break;

            yield return new WaitForSeconds(1f);
            elapsed += 1f;
        }

        if (_gameStarted) yield break;
        _gameStarted = true;

        // 실제 등록된 인원수를 기준으로 모든 클라이언트에 시작 신호 전송
        int connected = InGameManager.Instance != null ? InGameManager.Instance.AliveCount : _players.Count;
        if (connected < expectedPlayerCount)
            Debug.LogWarning($"[NetworkSpawnManager] 타임아웃 ({elapsed}초): " +
                             $"{connected}/{expectedPlayerCount}명 접속. 강제 시작.");
        else
            Debug.Log($"[NetworkSpawnManager] ✅ 전원 접속 완료 ({connected}명). 게임 시작!");

        BeginGameClientRpc(connected);
    }

    // ════════════════════════════════════════════════════════════
    //  ClientRpc — 서버 → 모든 클라이언트
    // ════════════════════════════════════════════════════════════

    [ClientRpc]
    private void BeginGameClientRpc(int totalPlayers)
    {
        Debug.Log($"[NetworkSpawnManager] 🚀 게임 시작! 총 {totalPlayers}명");
        InGameHUD.Instance?.SetGameStarted(totalPlayers);
    }

    /// <summary>서버 → 클라이언트: 카운트다운 메시지를 HUD 배너에 표시합니다.</summary>
    [ClientRpc]
    public void ShowCountdownClientRpc(string message)
    {
        InGameHUD.Instance?.ShowGameEndBanner(message);
    }

    /// <summary>
    /// 서버 → 클라이언트: 게임이 정식 시작됨을 알리고 서버 시작 시간을 전달합니다.
    /// InGameManager.ClientReceiveGameStart()를 호출해 클라이언트 상태를 동기화합니다.
    /// </summary>
    [ClientRpc]
    public void NotifyGameStartedClientRpc(float serverStartTime)
    {
        InGameManager.Instance?.ClientReceiveGameStart(serverStartTime);
    }

    /// <summary>서버 → 클라이언트: 카운트다운 배너를 숨깁니다.</summary>
    [ClientRpc]
    public void HideCountdownClientRpc()
    {
        if (InGameHUD.Instance?.endBannerPanel != null)
            InGameHUD.Instance.endBannerPanel.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════
    //  유틸 (InGameManager에서 접근 가능)
    // ════════════════════════════════════════════════════════════

    public int GetAliveCount()
    {
        int count = 0;
        foreach (var s in _players.Values)
            if (s != null && !s.NetworkIsDead.Value) count++;
        return count;
    }

    public IReadOnlyCollection<PlayerNetworkSync> GetAllPlayers()
        => _players.Values.ToList();
}
