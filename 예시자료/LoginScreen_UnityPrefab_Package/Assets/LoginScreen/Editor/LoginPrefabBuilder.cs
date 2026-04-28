#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using System.IO;

public static class LoginPrefabBuilder
{
    const string RootPath = "Assets/LoginScreen";
    const string ArtPath = RootPath + "/Art";
    const string PrefabPath = RootPath + "/Prefabs/LoginScreenPrefab.prefab";

    [MenuItem("Tools/Login Screen/Create Login Screen Prefab")]
    public static void CreatePrefab()
    {
        Directory.CreateDirectory(RootPath + "/Prefabs");
        ConfigureTexture(ArtPath + "/Top_BG_1080x720.png");
        ConfigureTexture(ArtPath + "/Center_Combat_1080x1080.png");
        ConfigureTexture(ArtPath + "/Bottom_BG_1080x720.png");
        AssetDatabase.Refresh();

        GameObject root = new GameObject("LoginScreenPrefab", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        RectTransform rootRt = root.GetComponent<RectTransform>();
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = Vector2.zero;
        rootRt.offsetMax = Vector2.zero;

        GameObject bgRoot = CreateRect("BG_Root", root.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        CreateImage("Top_BG_Tiled", bgRoot.transform, ArtPath + "/Top_BG_1080x720.png",
            new Vector2(0, 0.55f), new Vector2(1, 1), Vector2.zero, Vector2.zero, Image.Type.Tiled);

        CreateImage("Bottom_BG_Stretch", bgRoot.transform, ArtPath + "/Bottom_BG_1080x720.png",
            new Vector2(0, 0), new Vector2(1, 0.45f), Vector2.zero, Vector2.zero, Image.Type.Simple);

        GameObject center = CreateImage("Center_Combat_Fixed", bgRoot.transform, ArtPath + "/Center_Combat_1080x1080.png",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1080, 1080), Vector2.zero, Image.Type.Simple);
        center.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 120);

        GameObject safeArea = CreateRect("SafeArea_UI", root.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        safeArea.AddComponent<SafeAreaFitter>();

        CreateText("Logo_Placeholder", safeArea.transform, "SURVIVAL\nDEATHMATCH", 72, FontStyle.Bold,
            new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(900, 220), new Vector2(0, -160));

        CreateButton("GoogleLoginButton", safeArea.transform, "G   Google 로그인",
            new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(760, 104), new Vector2(0, 245), Color.white, Color.black);

        CreateButton("GuestLoginButton", safeArea.transform, "●   게스트 로그인",
            new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(760, 104), new Vector2(0, 115), new Color(0.08f,0.09f,0.10f,0.95f), Color.white);

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Login Screen", "Prefab created:\n" + PrefabPath, "OK");
    }

    static void ConfigureTexture(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.filterMode = FilterMode.Point;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }

    static GameObject CreateRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta, Vector2 anchoredPos)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.sizeDelta = sizeDelta;
        rt.anchoredPosition = anchoredPos;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return go;
    }

    static GameObject CreateImage(string name, Transform parent, string spritePath, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta, Vector2 anchoredPos, Image.Type type)
    {
        GameObject go = CreateRect(name, parent, anchorMin, anchorMax, sizeDelta, anchoredPos);
        Image img = go.AddComponent<Image>();
        img.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
        img.type = type;
        img.preserveAspect = true;
        return go;
    }

    static void CreateText(string name, Transform parent, string text, int fontSize, FontStyle style, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta, Vector2 anchoredPos)
    {
        GameObject go = CreateRect(name, parent, anchorMin, anchorMax, sizeDelta, anchoredPos);
        Text t = go.AddComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.alignment = TextAnchor.MiddleCenter;
        t.fontSize = fontSize;
        t.fontStyle = style;
        t.color = Color.white;
    }

    static void CreateButton(string name, Transform parent, string label, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta, Vector2 anchoredPos, Color bgColor, Color textColor)
    {
        GameObject go = CreateImage(name, parent, "", anchorMin, anchorMax, sizeDelta, anchoredPos, Image.Type.Simple);
        Image img = go.GetComponent<Image>();
        img.sprite = null;
        img.color = bgColor;
        Button btn = go.AddComponent<Button>();

        GameObject txt = CreateRect("Text", go.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        Text t = txt.AddComponent<Text>();
        t.text = label;
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.alignment = TextAnchor.MiddleCenter;
        t.fontSize = 40;
        t.color = textColor;
    }
}
#endif
