using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class DamagePopup : MonoBehaviour
{
    [Header("References")]
    public Text displayText; 

    [Header("Animation Settings")]
    public float riseSpeed   = 1.2f;   
    public float fadeDuration = 0.8f;  
    public float lifetime    = 1.0f;   

    private Color textColor;

    public void Setup(string text, Color color)
    {
        if (displayText != null)
        {
            displayText.text = text;
            displayText.color = color;
            textColor = color;
        }

        float scale = (text.Contains("!") || text == "MISS") ? 1.3f : 1.0f;
        transform.localScale = Vector3.one * scale;

        StartCoroutine(AnimateRoutine());
    }

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
                textColor.a = Mathf.Clamp01(alpha);
                displayText.color = textColor;
            }

            yield return null;
        }

        Destroy(gameObject);
    }
}
