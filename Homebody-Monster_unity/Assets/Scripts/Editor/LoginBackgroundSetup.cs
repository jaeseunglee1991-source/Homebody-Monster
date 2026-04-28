#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class LoginBackgroundSetup
{
    private const string ArtPath = "Assets/UI/Login";
    
    [MenuItem("Tools/Homebody Monster/Apply New Login Background")]
    public static void ApplyBackground()
    {
        // 1. 이미지 임포트 설정 자동화
        ConfigureTexture($"{ArtPath}/Top_BG_1080x720.png");
        ConfigureTexture($"{ArtPath}/Center_Combat_1080x1080.png");
        ConfigureTexture($"{ArtPath}/Bottom_BG_1080x720.png");
        AssetDatabase.Refresh();

        // 2. 현재 씬에서 Canvas 찾기
        Canvas canvas = Object.FindAnyObjectByType<Canvas>();
        if (canvas == null)
        {
            EditorUtility.DisplayDialog("Error", "씬에 Canvas가 없습니다.", "OK");
            return;
        }

        // 3. 기존 배경(Background) 오브젝트가 있다면 가려지므로 비활성화
        Transform oldBg = canvas.transform.Find("Background");
        if (oldBg == null)
        {
            // 다른 이름일 수도 있으니 계층구조에서 찾아봄
            foreach(Transform child in canvas.transform)
            {
                if(child.name.ToLower().Contains("background") && child.name != "BG_Root")
                {
                    oldBg = child;
                    break;
                }
            }
        }

        if (oldBg != null)
        {
            oldBg.gameObject.SetActive(false);
            Debug.Log($"기존 '{oldBg.name}' 오브젝트를 비활성화했습니다.");
        }

        // 4. BG_Root 생성 또는 갱신
        Transform existingBg = canvas.transform.Find("BG_Root");
        if (existingBg != null)
        {
            Object.DestroyImmediate(existingBg.gameObject);
        }

        GameObject bgRoot = new GameObject("BG_Root", typeof(RectTransform));
        bgRoot.transform.SetParent(canvas.transform, false);
        bgRoot.transform.SetAsFirstSibling(); // 맨 뒤로 보냄
        SetLayerRecursive(bgRoot, LayerMask.NameToLayer("UI")); 
        
        RectTransform rootRt = bgRoot.GetComponent<RectTransform>();
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.sizeDelta = Vector2.zero;

        // 5. 각 레이어 생성 및 배치
        CreateImageLayer("Bottom_BG_Stretch", bgRoot.transform, $"{ArtPath}/Bottom_BG_1080x720.png",
            new Vector2(0, 0), new Vector2(1, 0.45f), Image.Type.Simple);

        CreateImageLayer("Top_BG_Tiled", bgRoot.transform, $"{ArtPath}/Top_BG_1080x720.png",
            new Vector2(0, 0.55f), new Vector2(1, 1), Image.Type.Tiled);

        GameObject center = CreateImageLayer("Center_Combat_Fixed", bgRoot.transform, $"{ArtPath}/Center_Combat_1080x1080.png",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Image.Type.Simple);
        RectTransform centerRt = center.GetComponent<RectTransform>();
        centerRt.sizeDelta = new Vector2(1080, 1080);
        centerRt.anchoredPosition = new Vector2(0, 120);

        EditorUtility.DisplayDialog("완료", "배경이 UI 레이어로 적용되었습니다. 기존 배경은 비활성화되었습니다.", "OK");
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
    }

    private static void ConfigureTexture(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.SaveAndReimport();
    }

    private static GameObject CreateImageLayer(string name, Transform parent, string spritePath, Vector2 anchorMin, Vector2 anchorMax, Image.Type type)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.layer = LayerMask.NameToLayer("UI");
        
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        Image img = go.AddComponent<Image>();
        img.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
        img.type = type;
        img.preserveAspect = true;

        return go;
    }
}
#endif

