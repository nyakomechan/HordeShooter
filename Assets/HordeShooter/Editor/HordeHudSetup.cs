using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharpEditor;

/// <summary>
/// One-click M5 setup, kept in sync with the current scene state:
///  - HUD canvas: wave/alive texts, player HP bar, boss HP bar, DOWNED banner
///    (no fullscreen blackout), one-shot horn source. If the canvas already
///    exists it is UPDATED IN PLACE so manual edits (e.g. a Source Image on
///    PlayerHPFill) are preserved.
///  - Audio: procedural .wav clips (GunShot/WaveHorn/HordeAmbient).
///  - WaveStart + GameRestart cubes, debug auto-spawn off.
///  - Gun: audio + world-space ammo canvas + muzzle FX + VRCPlayerObject +
///    VRCObjectSync (+ RespawnAnchor scratch transform for TeleportTo).
/// Menu: HordeShooter/Setup M5 (HUD + Audio + Wave)
/// </summary>
public static class HordeHudSetup
{
    [MenuItem("HordeShooter/Setup M5 (HUD + Audio + Wave)")]
    public static void Setup()
    {
        var sim = Object.FindObjectOfType<HordeSim>();
        var net = Object.FindObjectOfType<HordeNet>();
        if (sim == null || net == null)
        {
            Debug.LogError("[HordeHudSetup] run HordeSceneSetup first (HordeSim/HordeNet missing)");
            return;
        }

        HordeAudioBuilder.BuildAll();

        // ---- wave start cube ----
        RecreateCube("WaveStart", new Vector3(-1.5f, 1.0f, 3.5f), (go) =>
        {
            var start = go.AddUdonSharpComponent<HordeWaveStart>();
            start.hordeNet = net;
            UdonSharpEditorUtility.CopyProxyToUdon(start);
        });

        // ---- game restart cube ----
        RecreateCube("GameRestart", new Vector3(-3.0f, 1.0f, 3.5f), (go) =>
        {
            var restart = go.AddUdonSharpComponent<HordeGameRestart>();
            restart.hordeNet = net;
            UdonSharpEditorUtility.CopyProxyToUdon(restart);
        });

        // ---- debug auto-spawn off (wave director replaces it) ----
        var dbg = GameObject.Find("DebugSpawn200");
        if (dbg != null)
        {
            var dsp = dbg.GetComponent<HordeDebugSpawner>();
            if (dsp != null) { dsp.autoSpawnOnJoin = false; UdonSharpEditorUtility.CopyProxyToUdon(dsp); }
        }

        // ---- gun: audio + UI/FX + PlayerObject components ----
        SetupGunAudio();
        HordeGunFxSetup.Setup();
        EnsureGunPlayerObject();

        // ---- HUD (update in place when present) ----
        SetupHud(net, sim);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        Debug.Log("[HordeHudSetup] M5 setup complete (HUD + audio + wave + gun FX) and scene saved");
    }

    // ---------------- HUD ----------------

    private static void SetupHud(HordeNet net, HordeSim sim)
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var canvasGo = GameObject.Find("HordeHUDCanvas");
        bool fresh = canvasGo == null;
        if (fresh)
        {
            canvasGo = new GameObject("HordeHUDCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            MakeText(canvasGo.transform, "WaveText", font,
                new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(400, 44), 34, TextAnchor.UpperCenter, Color.white);
            MakeText(canvasGo.transform, "AliveText", font,
                new Vector2(1f, 1f), new Vector2(-30f, -20f), new Vector2(200, 36), 26, TextAnchor.UpperRight, Color.white);

            var phpBg = MakeBar(canvasGo.transform, "PlayerHPBg", new Vector2(0f, 0f), new Vector2(30f, 30f), new Vector2(320, 26), new Color(0, 0, 0, 0.55f));
            MakeFill(phpBg.transform, "PlayerHPFill", new Color(0.9f, 0.25f, 0.2f));

            var bossRoot = MakeBar(canvasGo.transform, "BossHPRoot", new Vector2(0.5f, 0f), new Vector2(0f, 70f), new Vector2(560, 34), new Color(0, 0, 0, 0.55f));
            bossRoot.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0f);
            MakeFill(bossRoot.transform, "BossHPFill", new Color(0.75f, 0.1f, 0.1f));
            Text bossLabel = MakeText(bossRoot.transform, "BossLabel", font,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400, 30), 20, TextAnchor.MiddleCenter, Color.white);
            bossLabel.text = "BOSS";
            bossRoot.SetActive(false);
        }

        Text waveText = FindText(canvasGo.transform, "WaveText");
        Text aliveText = FindText(canvasGo.transform, "AliveText");
        // QuotaText (M7): created on demand so in-place updates stay non-destructive
        Text quotaText = FindText(canvasGo.transform, "QuotaText");
        if (quotaText == null)
            quotaText = MakeText(canvasGo.transform, "QuotaText", font,
                new Vector2(0.5f, 1f), new Vector2(0f, -64f), new Vector2(500, 36), 26, TextAnchor.UpperCenter, new Color(1f, 0.85f, 0.4f));
        Image playerHpFill = FindFill(canvasGo.transform, "PlayerHPBg", "PlayerHPFill");
        GameObject bossHpRoot = canvasGo.transform.Find("BossHPRoot")?.gameObject;
        Image bossHpFill = bossHpRoot != null ? FindFill(bossHpRoot.transform, "BossHPFill", "BossHPFill") : null;

        // DOWNED banner (no blackout): centered strip, non-blocking
        GameObject downOverlay = canvasGo.transform.Find("DownOverlay")?.gameObject;
        if (downOverlay == null)
        {
            downOverlay = new GameObject("DownOverlay");
            downOverlay.transform.SetParent(canvasGo.transform, false);
            downOverlay.SetActive(false);
        }
        var drt = downOverlay.GetComponent<RectTransform>();
        if (drt == null) drt = downOverlay.AddComponent<RectTransform>();
        drt.anchorMin = new Vector2(0.5f, 0.5f); drt.anchorMax = new Vector2(0.5f, 0.5f);
        drt.pivot = new Vector2(0.5f, 0.5f);
        drt.anchoredPosition = new Vector2(0f, 120f);
        drt.sizeDelta = new Vector2(760, 96);
        var dimg = downOverlay.GetComponent<Image>();
        if (dimg == null) dimg = downOverlay.AddComponent<Image>();
        dimg.color = new Color(0.35f, 0f, 0f, 0.65f);
        dimg.raycastTarget = false;
        Text dtext = downOverlay.GetComponentInChildren<Text>();
        if (dtext == null)
        {
            var dtextGo = new GameObject("DownText");
            dtextGo.transform.SetParent(downOverlay.transform, false);
            var trt = dtextGo.AddComponent<RectTransform>();
            trt.anchorMin = new Vector2(0.5f, 0.5f); trt.anchorMax = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(700, 80);
            dtext = dtextGo.AddComponent<Text>();
            dtext.raycastTarget = false;
        }
        dtext.font = font;
        dtext.fontSize = 36;
        dtext.alignment = TextAnchor.MiddleCenter;
        dtext.color = new Color(1f, 0.4f, 0.35f);
        dtext.text = "DOWNED - 行動不能 (WAVE CLEAR で復帰)";

        // ---- HUD behaviour + audio sources ----
        var hudGo = GameObject.Find("HordeHUD");
        if (hudGo == null)
        {
            hudGo = new GameObject("HordeHUD");
            hudGo.AddUdonSharpComponent<HordeHUD>();
        }
        var hud = hudGo.GetComponent<HordeHUD>();
        var src = hudGo.GetComponent<AudioSource>();
        if (src == null) src = hudGo.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0f;
        var oneShot = hudGo.GetComponents<AudioSource>();
        AudioSource oneShotSrc = oneShot.Length > 1 ? oneShot[1] : null;
        if (oneShotSrc == null)
        {
            oneShotSrc = hudGo.AddComponent<AudioSource>();
            oneShotSrc.playOnAwake = false;
            oneShotSrc.spatialBlend = 0f;
            oneShotSrc.volume = 1f;
        }
        hud.hordeNet = net;
        hud.hordeSim = sim;
        hud.waveText = waveText;
        hud.quotaText = quotaText;
        hud.aliveText = aliveText;
        hud.bossHpRoot = bossHpRoot;
        hud.bossHpFill = bossHpFill;
        hud.playerHpFill = playerHpFill;
        hud.downOverlay = downOverlay;
        hud.audioSrc = src;
        hud.oneShotSrc = oneShotSrc;
        hud.waveHorn = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/HordeShooter/Audio/WaveHorn.wav");
        hud.hordeAmbient = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/HordeShooter/Audio/HordeAmbient.wav");
        UdonSharpEditorUtility.CopyProxyToUdon(hud);
    }

    // ---------------- gun ----------------

    private static void SetupGunAudio()
    {
        var gunGo = GameObject.Find("Gun");
        if (gunGo == null) return;
        var gun = gunGo.GetComponent<GunController>();
        if (gun == null) return;
        var src = gunGo.GetComponent<AudioSource>();
        if (src == null) src = gunGo.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0f;
        gun.audioSrc = src;
        gun.shotClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/HordeShooter/Audio/GunShot.wav");
        UdonSharpEditorUtility.CopyProxyToUdon(gun);
    }

    private static void EnsureGunPlayerObject()
    {
        var gunGo = GameObject.Find("Gun");
        if (gunGo == null) return;
        if (gunGo.GetComponent<VRC.SDK3.Components.VRCPlayerObject>() == null)
            gunGo.AddComponent<VRC.SDK3.Components.VRCPlayerObject>();
        if (gunGo.GetComponent<VRC.SDK3.Components.VRCObjectSync>() == null)
            gunGo.AddComponent<VRC.SDK3.Components.VRCObjectSync>();
        if (gunGo.transform.Find("RespawnAnchor") == null)
        {
            var anchorGo = new GameObject("RespawnAnchor");
            anchorGo.transform.SetParent(gunGo.transform, false);
        }
    }

    // ---------------- helpers ----------------

    private delegate void CubeInit(GameObject go);

    private static void RecreateCube(string name, Vector3 pos, CubeInit init)
    {
        var old = GameObject.Find(name);
        if (old != null) Object.DestroyImmediate(old);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = pos;
        go.transform.localScale = new Vector3(0.4f, 0.4f, 0.4f);
        init(go);
    }

    private static Text MakeText(Transform parent, string name, Font font, Vector2 anchor, Vector2 pos, Vector2 size, int fontSize, TextAnchor align, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor;
        rt.pivot = anchor;
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

    private static GameObject MakeBar(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor;
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var img = go.AddComponent<Image>();
        img.color = color;
        return go;
    }

    private static Image MakeFill(Transform parent, string name, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(2, 2);
        rt.offsetMax = new Vector2(-2, -2);
        var img = go.AddComponent<Image>();
        img.color = color;
        // fillAmount only takes effect when the Image type is Filled; assign the
        // built-in UI sprite so the fill renders correctly in builds too
        img.type = Image.Type.Filled;
        img.fillMethod = Image.FillMethod.Horizontal;
        img.fillAmount = 1f;
        img.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        return img;
    }

    private static Text FindText(Transform root, string name)
    {
        var t = root.Find(name);
        return t != null ? t.GetComponent<Text>() : null;
    }

    private static Image FindFill(Transform root, string barName, string fillName)
    {
        var bar = root.Find(barName);
        if (bar == null) return null;
        var f = bar.Find(fillName);
        return f != null ? f.GetComponent<Image>() : null;
    }
}
