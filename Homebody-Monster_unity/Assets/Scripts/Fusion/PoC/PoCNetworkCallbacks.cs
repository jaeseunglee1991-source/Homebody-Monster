using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// [Fusion 2 마이그레이션 — Phase 0b]
/// 러너 프리팹에 부착하는 콜백 핸들러. (NetworkRunner가 자기 오브젝트의
/// INetworkRunnerCallbacks 구현 컴포넌트를 자동 등록한다.)
///
///  • OnInput        : 로컬 키보드(WASD/화살표 + Space) → NetInputData 로 서버 전달
///  • OnPlayerJoined : (StateAuthority 전용) 접속 플레이어용 NetPlayer 스폰
///  • OnPlayerLeft   : 이탈 플레이어 Despawn
///
/// ※ 이 파일은 INetworkRunnerCallbacks 전체 멤버를 구현한다. Fusion 빌드별로
///   콜백 시그니처가 미세하게 다를 수 있으므로, 컴파일 에러(CS0535/시그니처 불일치)가
///   나면 그 메시지를 알려주면 즉시 맞춰 수정한다. (IDE의 "Implement Interface"로도 보정 가능)
/// </summary>
public class PoCNetworkCallbacks : MonoBehaviour, INetworkRunnerCallbacks
{
    [Tooltip("스폰할 플레이어 프리팹 (NetworkObject + NetPlayer)")]
    public NetworkObject PlayerPrefab;

    [Tooltip("매치 관리자 프리팹 (NetworkObject + NetMatch)")]
    public NetworkObject MatchPrefab;

    private readonly Dictionary<PlayerRef, NetworkObject> _spawned = new();
    private bool _matchSpawned;

    // [5-A] 세션 종료 시 로비 복귀 — 중복 LoadScene 방지 가드(새 러너 인스턴스마다 리셋).
    private static bool _returning;
    private void Awake() => _returning = false;

    /// <summary>호스트 이탈/연결 끊김 → 멈추지 않고 로비로 복귀.</summary>
    private void ReturnToLobby(string reason)
    {
        if (_returning) return;
        _returning = true;
        Debug.LogWarning($"[PoCNetworkCallbacks] 세션 종료({reason}) → 로비 복귀");
        UnityEngine.SceneManagement.SceneManager.LoadScene("LobbyScene");
    }

    // ── 입력 수집 (모든 피어의 로컬에서 호출) ─────────────────────
    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        var data = new NetInputData();
        Vector2 dir = Vector2.zero;

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    dir.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  dir.y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) dir.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  dir.x -= 1f;
        }

        dir += NetMobileInput.JoystickDir; // [4-B] 가상 조이스틱 합성
        data.Direction = Vector2.ClampMagnitude(dir, 1f);
        input.Set(data);
    }

    // ── 스폰 / 디스폰 (StateAuthority 전용) ──────────────────────
    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        if (!runner.IsServer) return;

        // 매치 관리자 1회 스폰 (호스트).
        if (!_matchSpawned && MatchPrefab != null)
        {
            runner.Spawn(MatchPrefab);
            _matchSpawned = true;
        }

        if (PlayerPrefab == null) return;

        Vector3 pos = NetSpawnPoints.Spawn(); // [6-A] 배치 스폰 지점(없으면 아레나 랜덤)

        var obj = runner.Spawn(PlayerPrefab, pos, Quaternion.identity, player);
        _spawned[player] = obj;
    }

    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (_spawned.TryGetValue(player, out var obj) && obj != null)
            runner.Despawn(obj);
        _spawned.Remove(player);
    }

    // ── 나머지 콜백 (PoC에서는 미사용 스텁) ───────────────────────
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    // [5-A] 세션 종료(호스트가 Shutdown/이탈, 세션 실패 등) → 로비 복귀.
    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        => ReturnToLobby($"Shutdown: {shutdownReason}");
    public void OnConnectedToServer(NetworkRunner runner) { }
    // [5-A] 클라가 호스트와의 연결을 잃음 → 로비 복귀.
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        => ReturnToLobby($"Disconnected: {reason}");
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner runner) { }
    public void OnSceneLoadStart(NetworkRunner runner) { }
}
