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

    [Header("게임 시작 조건")]
    [Tooltip("예상 접속 인원. MatchmakingManager.maxPlayers와 동일하게 설정.")]
    public int expectedPlayerCount = 2;

    [Tooltip("접속 대기 타임아웃(초). 초과 시 현재 인원으로 강제 시작.")]
    public float startTimeout = 30f;

    // ── 서버 전용 상태 ─────────────────────────────────────────
    private readonly Dictionary<ulong, PlayerNetworkSync> _players = new();
    private int   _usedSpawnPoints = 0;
    private bool  _gameStarted     = false;

    // ════════════════════════════════════════════════════════════
    //  초기화
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback  += HandleClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;

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

    private Vector3 GetNextSpawnPoint()
    {
        if (spawnPoints == null || spawnPoints.Length == 0) return Vector3.zero;

        // 순환 배정 (8명까지 서로 다른 위치에 배치)
        var point = spawnPoints[_usedSpawnPoints % spawnPoints.Length];
        _usedSpawnPoints++;
        return point != null ? point.position : Vector3.zero;
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
            yield return new WaitForSeconds(1f);
            elapsed += 1f;
        }

        if (_gameStarted) yield break;
        _gameStarted = true;

        int connected = _players.Count;
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
