using Fusion;
using UnityEngine;

/// <summary>
/// [Phase 1] 클라이언트 입력 구조체. PoCNetworkCallbacks.OnInput에서 채워 서버로 전달.
/// </summary>
public struct NetInputData : INetworkInput
{
    public Vector2        Direction;
    public NetworkButtons Buttons;
}

/// <summary>입력 버튼 인덱스. 1-A는 디버그 자해만, 1-B에서 Attack/Skill 등으로 확장.</summary>
public enum NetButton
{
    DebugDamage = 0,
}
