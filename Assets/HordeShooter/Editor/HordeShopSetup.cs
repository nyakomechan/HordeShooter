using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharpEditor;

/// <summary>
/// One-click roguelite shop space (M7c). Builds a small platform OUTSIDE the
/// arena (z = -48) with three interactable card pedestals + floating labels and
/// wires the ShopController. Fully generated — safe to rebuild any time.
/// Menu: HordeShooter/Setup Shop Space
/// </summary>
public static class HordeShopSetup
{
    [MenuItem("HordeShooter/Setup Shop Space")]
    public static void Setup()
    {
        var net = Object.FindObjectOfType<HordeNet>();
        var gunTemplate = GameObject.Find("Gun");
        if (net == null || gunTemplate == null)
        {
            Debug.LogError("[HordeShopSetup] run HordeSceneSetup first (HordeNet / Gun missing)");
            return;
        }

        Vector3 origin = new Vector3(0f, 0f, -48f);

        // rebuild from scratch (fully generated, no manual edits to preserve)
        var old = GameObject.Find("ShopSpace");
        if (old != null) Object.DestroyImmediate(old);
        // also destroy any orphan ShopController objects left by earlier failed runs
        var oldCtrls = Object.FindObjectsOfType<ShopController>(true);
        foreach (var c in oldCtrls)
        {
            Object.DestroyImmediate(c.gameObject);
        }

        var root = new GameObject("ShopSpace");
        root.transform.position = origin;

        // floor (top at local y = 0)
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.SetParent(root.transform, false);
        floor.transform.localPosition = new Vector3(0f, -0.25f, 0f);
        floor.transform.localScale = new Vector3(16f, 0.5f, 10f);

        // back sign
        var signGo = new GameObject("Sign");
        signGo.transform.SetParent(root.transform, false);
        signGo.transform.localPosition = new Vector3(0f, 3f, -4.5f);
        var sign = signGo.AddComponent<TextMesh>();
        sign.text = "SHOP";
        sign.fontSize = 120;
        sign.characterSize = 0.12f;
        sign.anchor = TextAnchor.MiddleCenter;
        sign.alignment = TextAlignment.Center;
        sign.color = new Color(1f, 0.85f, 0.4f);

        // card label canvas (uGUI Text — TextMesh.text is not Udon-exposed)
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var canvasGo = new GameObject("ShopCanvas");
        canvasGo.transform.SetParent(root.transform, false);
        canvasGo.transform.localPosition = new Vector3(0f, 2.4f, 0f);
        canvasGo.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(1200, 400);

        // three card pedestals + labels
        var stands = new Transform[3];
        var labels = new Text[3];
        float[] xs = new float[] { -3f, 0f, 3f };
        for (int i = 0; i < 3; i++)
        {
            var stand = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stand.name = "CardStand" + (i + 1);
            stand.transform.SetParent(root.transform, false);
            stand.transform.localPosition = new Vector3(xs[i], 0.5f, 0f);
            stand.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
            var pick = stand.AddUdonSharpComponent<ShopCardPick>();
            pick.shop = null;   // wired below (controller created after stands)
            pick.cardIndex = i;

            var labelGo = new GameObject("CardLabel" + (i + 1));
            labelGo.transform.SetParent(canvasGo.transform, false);
            var rt = labelGo.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(340, 320);
            rt.anchoredPosition = new Vector3(xs[i] * 100f, 0f, 0f);
            var label = labelGo.AddComponent<Text>();
            label.font = font;
            label.fontSize = 30;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = "...";

            stands[i] = stand.transform;
            labels[i] = label;
        }

        // warp-in point (entrance side, facing the pedestals)
        var spawnGo = new GameObject("ShopSpawn");
        spawnGo.transform.SetParent(root.transform, false);
        spawnGo.transform.localPosition = new Vector3(0f, 0f, 3.5f);
        spawnGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

        // controller
        var ctrlGo = new GameObject("ShopController");
        ctrlGo.transform.position = origin;
        var ctrl = ctrlGo.AddUdonSharpComponent<ShopController>();
        ctrl.hordeNet = net;
        ctrl.gunTemplate = gunTemplate.GetComponent<GunController>();
        ctrl.shopSpawn = spawnGo.transform;
        ctrl.cardLabels = labels;
        // wire the stands' pick behaviour back to the controller
        for (int i = 0; i < 3; i++)
        {
            var pick = stands[i].GetComponent<ShopCardPick>();
            pick.shop = ctrl;
            UdonSharpEditorUtility.CopyProxyToUdon(pick);
        }
        UdonSharpEditorUtility.CopyProxyToUdon(ctrl);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        Debug.Log("[HordeShopSetup] shop space built at (0,0,-48) and scene saved");
    }
}
