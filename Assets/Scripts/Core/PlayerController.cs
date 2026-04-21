using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class PlayerController : MonoBehaviour
{
    public CharacterData myData;
    
    // Note: 'Joystick' requires a third-party asset like "Floating Joystick" or "Variable Joystick".
    // Ensure you have a Joystick script in your project or replace this with your input system.
    public VariableJoystick movementJoystick; 
    
    private Rigidbody2D rb;
    private Vector2 movementInfo;

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        // 씬 진입 시 로비에서 넘어온 데이터로 스탯 초기화 진행
        // myData = GameManager.Instance.GetMyCharacterData();
    }

    void Update()
    {
        // 내 캐릭터일 경우에만 조작 가능 (멀티플레이어 분기점)
        // if (!photonView.IsMine) return;

        if (movementJoystick != null)
        {
            movementInfo.x = movementJoystick.Horizontal;
            movementInfo.y = movementJoystick.Vertical;
        }

        // 이동 애니메이션 방향 전환 처리 등
    }

    void FixedUpdate()
    {
        // 로우스탯 밸런스에 맞춰 설정된 이동속도로 물리 이동
        rb.MovePosition(rb.position + movementInfo * myData.moveSpeed * Time.fixedDeltaTime);
    }

    public void OnAttackButtonPressed()
    {
        // 타격 범위 내의 적을 찾아 CombatSystem.CalculateDamage 호출
        // 결과값을 RPC를 통해 모든 클라이언트에 동기화 (데미지 텍스트 팝업 등)
    }
}
