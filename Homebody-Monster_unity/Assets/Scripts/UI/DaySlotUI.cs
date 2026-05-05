using UnityEngine;
using UnityEngine.UI;
using TMPro;

// ════════════════════════════════════════════════════════════════
//  DaySlotUI — 출석체크 7일 칸 하나를 제어하는 컴포넌트
//
//  [Bug Fix #1] [Serializable] 제거
//  MonoBehaviour 상속 클래스에 [Serializable]을 함께 붙이면
//  Unity 직렬화 시스템과 충돌하여 Inspector 오작동 또는 경고 발생.
//
//  DailyRewardSystem.cs 에서 분리 — 파일명=클래스명 이어야
//  유니티 Add Component 메뉴에 표시됩니다.
// ════════════════════════════════════════════════════════════════
public class DaySlotUI : MonoBehaviour
{
    [Header("슬롯 내부 UI 참조")]
    public TextMeshProUGUI dayLabel;       // "1일"
    public TextMeshProUGUI pizzaLabel;     // "🍕 10"
    public Image           highlightImage; // 오늘 테두리 강조
    public Image           checkMark;      // 수령 완료 체크 아이콘
    public CanvasGroup     futureOverlay;  // 미래 슬롯 반투명 오버레이 (CanvasGroup 컴포넌트)

    [Header("색상")]
    public Color colorToday     = new Color(1f,   0.85f, 0f);   // 골드
    public Color colorCompleted = new Color(0.4f, 0.85f, 0.4f); // 초록
    public Color colorFuture    = new Color(0.5f, 0.5f,  0.5f); // 회색

    public void SetDay(int dayNumber, int pizza, bool isCompleted, bool isToday, bool isFuture)
    {
        if (dayLabel != null)
            dayLabel.text = $"{dayNumber}일";

        if (pizzaLabel != null)
        {
            pizzaLabel.text  = dayNumber == 7 ? $"🍕 {pizza}\n🎁 보너스" : $"🍕 {pizza}";
            pizzaLabel.color = isCompleted ? colorCompleted
                             : isToday     ? colorToday
                             : colorFuture;
        }

        if (highlightImage != null)
        {
            highlightImage.enabled = isToday;
            highlightImage.color   = colorToday;
        }

        if (checkMark != null)
            checkMark.enabled = isCompleted;

        if (futureOverlay != null)
        {
            futureOverlay.alpha          = isFuture ? 0.45f : 0f;
            futureOverlay.blocksRaycasts = false;
        }
    }
}
