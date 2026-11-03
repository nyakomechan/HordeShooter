using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UdonSharpEditor;
using VRC.SDK3.Components;

/// <summary>
/// One-click roguelite shop space (M7c). Builds a small platform OUTSIDE the
/// arena (z = -48) with a world-space UI Canvas: three clickable card panels
/// (uGUI Button -> ShopCardPick._Pick) plus physical pedestals as a fallback
/// (Interact). Fully generated — safe to rebuild any time.
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

        EnsureEventSystem();

        var root = new GameObject("ShopSpace");
        root.transform.position = origin;

        // floor (top at local y = 0)
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.SetParent(root.transform, false);
        floor.transform.localPosition = new Vector3(0f, -0.25f, 0f);
        floor.transform.localScale = new Vector3(16f, 0.5f, 10f);

        // back sign (TextMesh readable face is -Z; flip toward the warp-in side)
        var signGo = new GameObject("Sign");
        signGo.transform.SetParent(root.transform, false);
        signGo.transform.localPosition = new Vector3(0f, 3f, -4.5f);
        signGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        var sign = signGo.AddComponent<TextMesh>();
        sign.text = "SHOP";
        sign.fontSize = 120;
        sign.characterSize = 0.12f;
        sign.anchor = TextAnchor.MiddleCenter;
        sign.alignment = TextAlignment.Center;
        sign.color = new Color(1f, 0.85f, 0.4f);

        // ---- shop UI canvas (world space, clickable card panels) ----
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var canvasGo = new GameObject("ShopCanvas");
        canvasGo.transform.SetParent(root.transform, false);
        canvasGo.transform.localPosition = new Vector3(0f, 2.4f, 0f);
        // the readable face of a world-space canvas points along -Z; the player
        // warps in on the +Z side, so flip the canvas to face them
        canvasGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        canvasGo.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<GraphicRaycaster>();
        // VRChat pointer interaction with world-space UI (requires World Space
        // render mode + Default layer + an EventSystem in the scene)
        canvasGo.AddComponent<VRCUiShape>();
        var canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(1200, 400);

        // three card pedestals (fallback Interact) + clickable card panels
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

            // clickable card panel: Image background + Button + Text
            var panelGo = new GameObject("CardPanel" + (i + 1));
            panelGo.transform.SetParent(canvasGo.transform, false);
            var prt = panelGo.AddComponent<RectTransform>();
            prt.anchorMin = new Vector2(0.5f, 0.5f); prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(340, 320);
            prt.anchoredPosition = new Vector3(xs[i] * 100f, 0f, 0f);
            var bg = panelGo.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.07f, 0.1f, 0.85f);
            bg.raycastTarget = true;   // the Button needs the raycast
            var btn = panelGo.AddComponent<Button>();
            var nav = new Navigation();
            nav.mode = Navigation.Mode.None;
            btn.navigation = nav;

            var labelGo = new GameObject("CardText");
            labelGo.transform.SetParent(panelGo.transform, false);
            var lrt = labelGo.AddComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(10, 10); lrt.offsetMax = new Vector2(-10, -10);
            var label = labelGo.AddComponent<Text>();
            label.font = font;
            label.fontSize = 30;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;   // clicks must reach the panel Button
            label.text = "...";

            // Button.onClick -> ShopCardPick._Pick (custom method, exact name)
            var pickUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(pick);
            UnityEventTools.AddStringPersistentListener(btn.onClick, pickUdon.SendCustomEvent, "_Pick");

            stands[i] = stand.transform;
            labels[i] = label;
        }

        // warp-in point (entrance side, facing the pedestals)
        var spawnGo = new GameObject("ShopSpawn");
        spawnGo.transform.SetParent(root.transform, false);
        spawnGo.transform.localPosition = new Vector3(0f, 0f, 3.5f);
        spawnGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

        // ---- game start UI (same room, eye-level panel between the player and the card canvas) ----
        var gsCanvasGo = new GameObject("GameStartCanvas");
        gsCanvasGo.transform.SetParent(root.transform, false);
        gsCanvasGo.transform.localPosition = new Vector3(0f, 1.5f, 0.8f);
        gsCanvasGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        gsCanvasGo.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);
        var gsCanvas = gsCanvasGo.AddComponent<Canvas>();
        gsCanvas.renderMode = RenderMode.WorldSpace;
        gsCanvasGo.AddComponent<GraphicRaycaster>();
        gsCanvasGo.AddComponent<VRCUiShape>();
        var gsRt = gsCanvasGo.GetComponent<RectTransform>();
        gsRt.sizeDelta = new Vector2(400, 120);

        var gsPanel = new GameObject("GameStartPanel");
        gsPanel.transform.SetParent(gsCanvasGo.transform, false);
        var gsPrt = gsPanel.AddComponent<RectTransform>();
        gsPrt.anchorMin = Vector2.zero; gsPrt.anchorMax = Vector2.one;
        gsPrt.offsetMin = Vector2.zero; gsPrt.offsetMax = Vector2.zero;
        var gsBg = gsPanel.AddComponent<Image>();
        gsBg.color = new Color(0.05f, 0.07f, 0.1f, 0.85f);
        gsBg.raycastTarget = true;

        var gsBtnGo = new GameObject("StartButton");
        gsBtnGo.transform.SetParent(gsPanel.transform, false);
        var gsBtnRt = gsBtnGo.AddComponent<RectTransform>();
        gsBtnRt.anchorMin = new Vector2(0f, 0.5f); gsBtnRt.anchorMax = new Vector2(0f, 0.5f);
        gsBtnRt.pivot = new Vector2(0f, 0.5f);
        gsBtnRt.anchoredPosition = new Vector2(15f, 0f);
        gsBtnRt.sizeDelta = new Vector2(230, 130);
        var gsBtnImg = gsBtnGo.AddComponent<Image>();
        gsBtnImg.color = new Color(0.15f, 0.55f, 0.25f, 0.95f);
        gsBtnImg.raycastTarget = true;
        var gsBtn = gsBtnGo.AddComponent<Button>();
        var gsNav = new Navigation();
        gsNav.mode = Navigation.Mode.None;
        gsBtn.navigation = gsNav;
        var gsBtnTextGo = new GameObject("Text");
        gsBtnTextGo.transform.SetParent(gsBtnGo.transform, false);
        var gsBtnTextRt = gsBtnTextGo.AddComponent<RectTransform>();
        gsBtnTextRt.anchorMin = Vector2.zero; gsBtnTextRt.anchorMax = Vector2.one;
        gsBtnTextRt.offsetMin = Vector2.zero; gsBtnTextRt.offsetMax = Vector2.zero;
        var gsBtnText = gsBtnTextGo.AddComponent<Text>();
        gsBtnText.font = font;
        gsBtnText.fontSize = 40;
        gsBtnText.alignment = TextAnchor.MiddleCenter;
        gsBtnText.color = Color.white;
        gsBtnText.raycastTarget = false;
        gsBtnText.text = "GAME START";

        var gsLabelGo = new GameObject("OwnerLabel");
        gsLabelGo.transform.SetParent(gsPanel.transform, false);
        var gsLabelRt = gsLabelGo.AddComponent<RectTransform>();
        gsLabelRt.anchorMin = new Vector2(1f, 0.5f); gsLabelRt.anchorMax = new Vector2(1f, 0.5f);
        gsLabelRt.pivot = new Vector2(1f, 0.5f);
        gsLabelRt.anchoredPosition = new Vector2(-15f, 0f);
        gsLabelRt.sizeDelta = new Vector2(230, 130);
        var gsLabel = gsLabelGo.AddComponent<Text>();
        gsLabel.font = font;
        gsLabel.fontSize = 30;
        gsLabel.alignment = TextAnchor.MiddleCenter;
        gsLabel.color = new Color(1f, 0.85f, 0.4f);
        gsLabel.raycastTarget = false;
        gsLabel.text = "START: ...";

        // game start behaviour (own GameObject: MultiSim bundles vars per GameObject)
        var gsUiGo = new GameObject("GameStartUI");
        gsUiGo.transform.position = origin;
        var gsUi = gsUiGo.AddUdonSharpComponent<HordeGameStartUI>();
        gsUi.hordeNet = net;
        gsUi.ownerLabel = gsLabel;
        gsUi.panelRoot = gsPanel;
        UdonSharpEditorUtility.CopyProxyToUdon(gsUi);
        // Button.onClick -> HordeGameStartUI._TryStart (custom method, exact name)
        var gsUiUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(gsUi);
        UnityEventTools.AddStringPersistentListener(gsBtn.onClick, gsUiUdon.SendCustomEvent, "_TryStart");

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
        Debug.Log("[HordeShopSetup] shop space built at (0,0,-48) with clickable card UI and scene saved");
    }

    /// <summary>World-space UI interaction needs an EventSystem in the scene.</summary>
    private static void EnsureEventSystem()
    {
        if (Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>() != null) return;
        var esGo = new GameObject("EventSystem");
        esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
        esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        Debug.Log("[HordeShopSetup] EventSystem created for the shop UI");
    }
}
