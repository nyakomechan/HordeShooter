using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharpEditor;

/// <summary>
/// One-click gun UI + muzzle FX setup. Only touches the Gun object — safe to run
/// after manual HUD edits (does NOT rebuild the HUD canvas).
/// Menu: HordeShooter/Setup Gun UI + Muzzle FX
/// </summary>
public static class HordeGunFxSetup
{
    [MenuItem("HordeShooter/Setup Gun UI + Muzzle FX")]
    public static void Setup()
    {
        var gunGo = GameObject.Find("Gun");
        if (gunGo == null)
        {
            Debug.LogError("[HordeGunFxSetup] Gun object not found (run HordeSceneSetup first)");
            return;
        }
        var gun = gunGo.GetComponent<GunController>();
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // ---- world-space canvas behind the gun (faces the holder) ----
        var oldCanvas = gunGo.transform.Find("GunCanvas");
        if (oldCanvas != null) Object.DestroyImmediate(oldCanvas.gameObject);
        var canvasGo = new GameObject("GunCanvas");
        canvasGo.transform.SetParent(gunGo.transform, false);
        canvasGo.transform.localPosition = new Vector3(0f, 0.14f, -0.42f);
        canvasGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // front faces the holder
        // the Gun cube is scaled 0.12 — compensate so the canvas is ~13 cm wide in world space
        canvasGo.transform.localScale = new Vector3(0.0042f, 0.0042f, 0.0042f);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(260, 90);

        Text ammoText = MakeText(canvasGo.transform, "AmmoText", font,
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(260, 50), 40, TextAnchor.MiddleCenter, Color.white);
        ammoText.text = "30 / 30";
        Text reloadText = MakeText(canvasGo.transform, "ReloadText", font,
            new Vector2(0.5f, 0.5f), new Vector2(0f, -34f), new Vector2(260, 30), 24, TextAnchor.MiddleCenter, new Color(1f, 0.35f, 0.25f));
        reloadText.text = "RELOADING...";
        reloadText.gameObject.SetActive(false);
        Text loadoutText = MakeText(canvasGo.transform, "LoadoutText", font,
            new Vector2(0.5f, 0.5f), new Vector2(0f, 34f), new Vector2(260, 26), 20, TextAnchor.MiddleCenter, new Color(0.7f, 0.9f, 1f));
        loadoutText.text = "BASIC";

        // ---- barrel visuals (extra barrels beyond the first, toggled by loadout) ----
        var visuals = new GameObject[4];
        float[] slotX = new float[] { -0.3f, -0.1f, 0.1f, 0.3f };
        for (int i = 0; i < 4; i++)
        {
            string slotName = "BarrelSlot" + (i + 1);
            var slotGo = gunGo.transform.Find(slotName)?.gameObject;
            if (slotGo == null)
            {
                slotGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                slotGo.name = slotName;
                slotGo.transform.SetParent(gunGo.transform, false);
                slotGo.transform.localPosition = new Vector3(slotX[i], 0.62f, 0.05f);
                slotGo.transform.localScale = new Vector3(0.35f, 0.35f, 0.75f);
                var col = slotGo.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
            }
            slotGo.SetActive(false);
            visuals[i] = slotGo;
        }

        // ---- muzzle flash ----
        var oldMuzzle = gunGo.transform.Find("Muzzle");
        if (oldMuzzle != null) Object.DestroyImmediate(oldMuzzle.gameObject);
        var muzzleGo = new GameObject("Muzzle");
        muzzleGo.transform.SetParent(gunGo.transform, false);
        muzzleGo.transform.localPosition = new Vector3(0f, 0f, 0.3f);
        var ps = muzzleGo.AddComponent<ParticleSystem>();
        var main0 = ps.main;
        main0.playOnAwake = false;
        var main = ps.main;
        main.loop = false;
        main.duration = 0.25f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.06f, 0.15f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.05f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.35f), new Color(1f, 0.45f, 0.1f));
        main.gravityModifier = 0.3f;
        main.maxParticles = 64;
        var em = ps.emission;
        em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);   // burst-only via Emit()
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.012f;

        // ---- wire ----
        gun.ammoText = ammoText;
        gun.reloadText = reloadText;
        gun.loadoutText = loadoutText;
        gun.barrelVisuals = visuals;
        gun.muzzlePs = ps;
        UdonSharpEditorUtility.CopyProxyToUdon(gun);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        Debug.Log("[HordeGunFxSetup] gun UI + muzzle FX wired and scene saved");
    }

    private static Text MakeText(Transform parent, string name, Font font, Vector2 anchor, Vector2 pos, Vector2 size, int fontSize, TextAnchor align, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var t = go.AddComponent<Text>();
        t.font = font;
        t.fontSize = fontSize;
        t.alignment = align;
        t.color = color;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }
}
