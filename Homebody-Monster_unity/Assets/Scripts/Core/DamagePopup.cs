using UnityEngine;
using TMPro;
using System.Collections;

public class DamagePopup : MonoBehaviour
{
    [Header("References")]
    public TextMeshProUGUI displayText;

    [Header("Animation Settings")]
    public float riseSpeed    = 1.2f;
    public float fadeDuration = 0.8f;
    public float lifetime     = 1.0f;

    private Color     _textColor;
    private Coroutine _animRoutine;

    // ── 풀 연동 ───────────────────────────────────────────────

    public void OnGetFromPool()
    {
        gameObject.SetActive(true);
    }

    public void OnReleaseToPool()
    {
        if (_animRoutine != null)
        {
            StopCoroutine(_animRoutine);
            _animRoutine = null;
        }
        gameObject.SetActive(false);
    }

    // ── 공개 API ──────────────────────────────────────────────

    public void Setup(string text, Color color)
    {
        if (displayText != null)
        {
            displayText.text  = text;
            displayText.color = color;
            _textColor        = color;
        }

        float scale = (text.Contains("!") || text == "MISS") ? 1.3f : 1.0f;
        transform.localScale = Vector3.one * scale;

        if (_animRoutine != null) StopCoroutine(_animRoutine);
        _animRoutine = StartCoroutine(AnimateRoutine());
    }

    // ── 내부 애니메이션 ───────────────────────────────────────

    private IEnumerator AnimateRoutine()
    {
        float elapsed = 0f;

        while (elapsed < lifetime)
        {
            elapsed += Time.deltaTime;
            transform.position += Vector3.up * riseSpeed * Time.deltaTime;

            float fadeStart = lifetime - fadeDuration;
            if (elapsed > fadeStart && displayText != null)
            {
                float alpha = 1f - ((elapsed - fadeStart) / fadeDuration);
                _textColor.a      = Mathf.Clamp01(alpha);
                displayText.color = _textColor;
            }

            yield return null;
        }

        _animRoutine = null;
        if (DamagePopupPool.Instance != null)
            DamagePopupPool.Instance.Release(this);
        else
            // BUG-16: 씬 전환 시 풀이 먼저 파괴된 경우 SetActive(false)는 풀 미반환 + 활성 팝업 잔류.
            // 명시적 Destroy로 정리하여 다음 씬에서 풀 용량 부족/누수 방지.
            Destroy(gameObject);
    }
}
