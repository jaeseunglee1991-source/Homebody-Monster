using UnityEngine;
using System.Collections;
using Unity.Netcode;

/// <summary>
/// 모바일 배틀로얄 전용 시야 제한(Fog of War) 시스템.
/// 거리, 벽(장애물), 은신 스킬 상태를 계산하여 로컬 화면의 가시성과 타겟팅을 제어합니다.
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class PlayerVisibility : NetworkBehaviour
{
    [Header("시야 설정")]
    public float viewRadius = 8.0f;       // 캐릭터 시야 반경
    public LayerMask obstacleLayer;       // 시야를 가리는 벽/장애물 레이어

    private PlayerController _pc;
    private WaitForSeconds _waitCache;
    private bool _isVisible = true;

    private void Awake()
    {
        _pc = GetComponent<PlayerController>();
        _waitCache = new WaitForSeconds(0.1f); // 1초에 10번 연산 (모바일 최적화)
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // 💡 내 캐릭터(Owner)가 조작 중일 때만 다른 플레이어들의 가시성을 계산합니다.
        if (IsOwner)
        {
            StartCoroutine(VisibilityUpdateRoutine());
        }
    }

    private IEnumerator VisibilityUpdateRoutine()
    {
        while (true)
        {
            if (_pc == null || _pc.IsDead) 
            { 
                yield return _waitCache; 
                continue; 
            }

            Vector2 myPos = transform.position;
            
            // 씬 내의 모든 플레이어를 검색 (클라이언트 기기 호환성 해결)
            PlayerController[] allPlayers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

            foreach (var enemyPc in allPlayers)
            {
                if (enemyPc == null || enemyPc == _pc) continue;

                // 1. 죽은 시체는 정보 공유를 위해 항상 보이게 유지
                if (enemyPc.IsDead)
                {
                    ApplyVisibilityToEnemy(enemyPc, true);
                    continue;
                }

                Vector2 enemyPos = enemyPc.transform.position;
                float dist = Vector2.Distance(myPos, enemyPos);
                bool shouldBeVisible = true;

                // 2. 은신 상태 체크 (암살자 등 스킬 연동)
                if (enemyPc.StatusFX != null && enemyPc.StatusFX.IsStealthy)
                {
                    shouldBeVisible = false;
                }
                // 3. 시야 반경 밖 체크
                else if (dist > viewRadius)
                {
                    shouldBeVisible = false;
                }
                // 4. 벽/장애물 뒤에 숨었는지 체크 (Raycast)
                else if (Physics2D.Linecast(myPos, enemyPos, obstacleLayer))
                {
                    shouldBeVisible = false;
                }

                ApplyVisibilityToEnemy(enemyPc, shouldBeVisible);
            }

            // [로컬 비주얼] 내가 은신 중일 때 내 캐릭터를 반투명하게 표시하여 상태 인지
            if (_pc.spriteRenderer != null && _pc.StatusFX != null)
            {
                if (_pc.StatusFX.IsStealthy)
                    _pc.spriteRenderer.color = new Color(1f, 1f, 1f, 0.4f);
                else 
                    _pc.spriteRenderer.color = Color.white;
            }

            yield return _waitCache;
        }
    }

    private void ApplyVisibilityToEnemy(PlayerController enemyPc, bool isVisible)
    {
        var enemyVis = enemyPc.GetComponent<PlayerVisibility>();
        // 불필요한 레이어 변경 연산을 방지하기 위해 상태가 바뀔 때만 실행
        if (enemyVis != null && enemyVis._isVisible != isVisible) 
        {
            enemyVis.SetVisible(isVisible);
        }
    }

    /// <summary>
    /// 실제 렌더링과 터치 레이어를 변경합니다. (클라이언트 로컬 전용)
    /// </summary>
    public void SetVisible(bool isVisible)
    {
        _isVisible = isVisible;

        // 1. 스프라이트 활성화/비활성화
        if (_pc.spriteRenderer != null)
            _pc.spriteRenderer.enabled = isVisible;

        // 2. 체력바 및 닉네임 UI 차단
        var canvas = GetComponentInChildren<Canvas>();
        if (canvas != null) canvas.enabled = isVisible;

        // 3. 터치 타겟팅 레이어 변경 (IgnorePointer 레이어 필요)
        // 데디케이티드 서버 환경이므로 로컬 레이어 변경은 서버 판정에 영향을 주지 않습니다.
        gameObject.layer = isVisible ? LayerMask.NameToLayer("Enemy") : LayerMask.NameToLayer("IgnorePointer");
    }
}
