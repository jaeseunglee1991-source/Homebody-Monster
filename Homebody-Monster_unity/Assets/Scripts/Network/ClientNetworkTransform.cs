using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// 모바일 조이스틱의 부드러운 이동을 위한 클라이언트 권한 NetworkTransform.
/// 소유권(Owner)을 가진 클라이언트가 위치를 서버로 전송합니다.
/// 플레이어 프리팹에서 기본 NetworkTransform 대신 이 컴포넌트를 사용하세요.
/// </summary>
[DisallowMultipleComponent]
public class ClientNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative()
    {
        return false; // 클라이언트 권한: Owner가 자신의 위치를 서버에 보고
    }
}
