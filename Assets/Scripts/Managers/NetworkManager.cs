using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class NetworkManager : MonoBehaviour
{
    public static NetworkManager Instance;

    public Action<string> OnChatReceived;
    public Action<List<string>> OnPlayerListUpdated;
    public Action OnMatchFound;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject); // Usually managers should persist
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // 1. 로그인 로직 (Supabase Auth 등 연동)
    public void Authenticate(string id, string password)
    {
        Debug.Log($"로그인 시도: {id}");
        // 서버 검증 성공 시
        GameManager.Instance.currentPlayerId = id;
        GameManager.Instance.LoadScene("LobbyScene");
    }

    // 2. 로비 진입 및 서버 연결
    public void ConnectToLobby()
    {
        Debug.Log("로비 서버 접속 중...");
        // 서버 연결 성공 시 플레이어 리스트 갱신 요청
        UpdatePlayerList();
    }

    // 3. 랜덤 매칭 시작
    public void StartMatchmaking()
    {
        Debug.Log("랜덤 매칭 큐 등록...");
        // 서버에 매칭 요청 후 대기
        StartCoroutine(MockMatchmakingRoutine());
    }

    private IEnumerator MockMatchmakingRoutine()
    {
        // 실제로는 서버에서 일정 인원(예: 10명)이 모이면 콜백을 줍니다.
        yield return new WaitForSeconds(2.0f); 
        Debug.Log("매칭 성공! 인게임 씬으로 이동합니다.");
        
        OnMatchFound?.Invoke();
        GameManager.Instance.LoadScene("InGameScene");
    }

    // 4. 채팅 전송 (로비 및 결과 창에서 공용 사용)
    public void SendChatMessage(string message)
    {
        string formattedMsg = $"[{GameManager.Instance.currentPlayerId}]: {message}";
        // 서버로 메시지 전송 로직...
        
        // 서버에서 메시지를 브로드캐스트 받았다고 가정
        ReceiveChatMessage(formattedMsg);
    }

    public void ReceiveChatMessage(string message)
    {
        OnChatReceived?.Invoke(message);
    }

    private void UpdatePlayerList()
    {
        // 현재 접속자 리스트 더미 데이터
        List<string> players = new List<string> { GameManager.Instance.currentPlayerId, "Player_2", "Player_3" };
        OnPlayerListUpdated?.Invoke(players);
    }
}
