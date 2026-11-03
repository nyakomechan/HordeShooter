using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UdonSharpEditor;
using nyakomake.udon3dpathfinding;

/// <summary>
/// Rebuilds the horde arena from Kenney's "Modular Space Kit" (Assets/kenney_modular-space-kit_1.0).
///
/// Layout (single floor, same 60 x 60 footprint as the old blockout Floor object):
///   - Deck          : 15 x 15 floor tiles (4 m kit grid) at y = 0
///   - Perimeter     : 60 wall modules (15 per side) + one 60 x 1 x 60 collision slab
///   - Central room  : room-large (20 x 20, four 4 m doorways) with a gate frame at each doorway
///   - Buildings     : two room-small (12 x 12) halls in the NW and SE quadrants
///   - Interior walls: two 4 m service corridors (NE / SW) and four wall pairs that form a
///                     vestibule in front of each central gate -> lanes, chokepoints and cover
///
/// The 8 enemy spawn points stay at their old positions (radius 24) and none of the
/// modules covers them. PathfindingManager.wallColliders is rewired to the new colliders
/// so the flow field sees the modular geometry.
/// </summary>
public static class ModularArenaBuilder
{
    private const string KitModels = "Assets/kenney_modular-space-kit_1.0/Models/FBX format";
    private const string KitTexturePath = KitModels + "/Textures/colormap.png";
    private const string KitMaterialPath = "Assets/HordeShooter/Materials/KitColormap.mat";

    private const int EnvLayer = 11;          // VRChat Environment layer (bullets + pathfinding)
    private const float Cell = 4f;            // kit grid module
    private const float HalfSize = 30f;       // arena half extent -> 60 x 60
    private const float RoomLift = 0.004f;    // room pieces carry their own floor: lift a hair above the deck

    private static Material kitMaterial;

    [MenuItem("HordeShooter/Rebuild Modular Arena (Kenney Kit)")]
    public static void Build()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.path))
        {
            EditorUtility.DisplayDialog("Modular Arena", "Save the scene first.", "OK");
            return;
        }

        kitMaterial = EnsureKitMaterial();
        RemoveOldStage();

        var stage = new GameObject("Stage").transform;
        var colliders = new List<Collider>();

        int tiles = Mathf.RoundToInt(HalfSize * 2f / Cell);   // 15

        BuildDeck(stage, tiles);
        BuildDeckCollision(stage);
        BuildPerimeter(stage, tiles, colliders);
        BuildCentralRoom(stage, colliders);
        BuildBuildings(stage, colliders);
        BuildInteriorWalls(stage, colliders);
        BuildPerimeterPillars(stage, colliders);

        WirePathfinding(colliders);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[ModularArenaBuilder] stage rebuilt: " + tiles * tiles + " deck tiles, " +
                  colliders.Count + " wall colliders wired to PathfindingManager.");
    }

    // ---------------------------------------------------------------- deck

    private static void BuildDeck(Transform stage, int tiles)
    {
        Transform deck = Group("Deck", stage);
        for (int i = 0; i < tiles; i++)
        {
            for (int j = 0; j < tiles; j++)
            {
                var pos = new Vector3(-HalfSize + Cell * 0.5f + i * Cell, 0f, -HalfSize + Cell * 0.5f + j * Cell);
                GameObject tile = Place("template-floor", "Deck_" + i + "_" + j, pos, 0f, deck);
                var renderer = tile.GetComponent<Renderer>();
                if (renderer != null) renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
        }
    }

    private static void BuildDeckCollision(Transform stage)
    {
        var go = new GameObject("Deck_Collision");
        go.transform.SetParent(stage, false);
        go.layer = EnvLayer;
        var box = go.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, -0.5f, 0f);
        box.size = new Vector3(HalfSize * 2f, 1f, HalfSize * 2f);
    }

    // ----------------------------------------------------------- perimeter

    private static void BuildPerimeter(Transform stage, int tiles, List<Collider> colliders)
    {
        Transform perimeter = Group("Perimeter", stage);
        for (int i = 0; i < tiles; i++)
        {
            float c = -HalfSize + Cell * 0.5f + i * Cell;
            AddWall(perimeter, "WallN_" + i, new Vector3(c, 0f, -HalfSize), 0f, colliders);
            AddWall(perimeter, "WallS_" + i, new Vector3(c, 0f, HalfSize), 180f, colliders);
            AddWall(perimeter, "WallW_" + i, new Vector3(-HalfSize, 0f, c), 90f, colliders);
            AddWall(perimeter, "WallE_" + i, new Vector3(HalfSize, 0f, c), -90f, colliders);
        }
    }

    private static void AddWall(Transform parent, string name, Vector3 pos, float yaw, List<Collider> colliders)
    {
        GameObject wall = Place("template-wall", name, pos, yaw, parent);
        var box = wall.AddComponent<BoxCollider>();
        // template-wall mesh bounds: x [-2, 2], y [0, 4.25], z [-1, 0]
        box.center = new Vector3(0f, 2.125f, -0.5f);
        box.size = new Vector3(4f, 4.25f, 1f);
        colliders.Add(box);
    }

    // --------------------------------------------------------- centre room

    private static void BuildCentralRoom(Transform stage, List<Collider> colliders)
    {
        Transform central = Group("Central", stage);

        GameObject room = Place("room-large", "CentralRoom", new Vector3(0f, RoomLift, 0f), 0f, central);
        AddMeshCollider(room, colliders);

        // The room has a 4 m doorway in the middle of each wall; frame each one with a gate.
        AddGate(central, "GateN", new Vector3(0f, RoomLift, -10f), 0f, colliders);
        AddGate(central, "GateS", new Vector3(0f, RoomLift, 10f), 180f, colliders);
        AddGate(central, "GateW", new Vector3(-10f, RoomLift, 0f), 90f, colliders);
        AddGate(central, "GateE", new Vector3(10f, RoomLift, 0f), -90f, colliders);
    }

    private static void AddGate(Transform parent, string name, Vector3 pos, float yaw, List<Collider> colliders)
    {
        GameObject gate = Place("gate", name, pos, yaw, parent);
        AddMeshCollider(gate, colliders);
    }

    // ------------------------------------------------------------ buildings

    private static void BuildBuildings(Transform stage, List<Collider> colliders)
    {
        Transform buildings = Group("Buildings", stage);

        GameObject nw = Place("room-small", "BuildingNW", new Vector3(-20f, RoomLift, -14f), 0f, buildings);
        AddMeshCollider(nw, colliders);

        GameObject se = Place("room-small", "BuildingSE", new Vector3(20f, RoomLift, 14f), 180f, buildings);
        AddMeshCollider(se, colliders);
    }

    // -------------------------------------------------------- interior walls

    private static void BuildInteriorWalls(Transform stage, List<Collider> colliders)
    {
        Transform walls = Group("InteriorWalls", stage);

        // Two 4 m wide service corridors: NE runs along X at z -20..-16, SW mirrored at z 16..20.
        // The decorated face of each wall points into the corridor.
        AddRun(walls, "CorridorNE_N", new Vector3(16f, 0f, -20f), 3, 1, 0f, colliders);
        AddRun(walls, "CorridorNE_S", new Vector3(16f, 0f, -16f), 3, 1, 180f, colliders);
        AddRun(walls, "CorridorSW_N", new Vector3(-24f, 0f, 16f), 3, 1, 0f, colliders);
        AddRun(walls, "CorridorSW_S", new Vector3(-24f, 0f, 20f), 3, 1, 180f, colliders);

        // Vestibule wall pairs 4 m in front of each central gate: an 8 m gap stays open on the
        // gate axis so the crowd funnels into the doorway instead of shearing along the wall.
        AddRun(walls, "GateN_W", new Vector3(-6f, 0f, -14f), 1, 1, 0f, colliders);
        AddRun(walls, "GateN_E", new Vector3(6f, 0f, -14f), 1, 1, 0f, colliders);
        AddRun(walls, "GateS_W", new Vector3(-6f, 0f, 14f), 1, 1, 180f, colliders);
        AddRun(walls, "GateS_E", new Vector3(6f, 0f, 14f), 1, 1, 180f, colliders);
        AddRun(walls, "GateW_N", new Vector3(-14f, 0f, -6f), 1, 0, 90f, colliders);
        AddRun(walls, "GateW_S", new Vector3(-14f, 0f, 6f), 1, 0, 90f, colliders);
        AddRun(walls, "GateE_N", new Vector3(14f, 0f, -6f), 1, 0, -90f, colliders);
        AddRun(walls, "GateE_S", new Vector3(14f, 0f, 6f), 1, 0, -90f, colliders);

        // Returns that tie each vestibule panel back to the central room wall, turning the
        // free-standing panels into 4 x 4 m alcoves either side of every gate.
        AddRun(walls, "AlcoveN_W", new Vector3(-8f, 0f, -12f), 1, 0, 90f, colliders);
        AddRun(walls, "AlcoveN_E", new Vector3(8f, 0f, -12f), 1, 0, -90f, colliders);
        AddRun(walls, "AlcoveS_W", new Vector3(-8f, 0f, 12f), 1, 0, 90f, colliders);
        AddRun(walls, "AlcoveS_E", new Vector3(8f, 0f, 12f), 1, 0, -90f, colliders);
        AddRun(walls, "AlcoveW_N", new Vector3(-12f, 0f, -8f), 1, 1, 0f, colliders);
        AddRun(walls, "AlcoveW_S", new Vector3(-12f, 0f, 8f), 1, 1, 180f, colliders);
        AddRun(walls, "AlcoveE_N", new Vector3(12f, 0f, -8f), 1, 1, 0f, colliders);
        AddRun(walls, "AlcoveE_S", new Vector3(12f, 0f, 8f), 1, 1, 180f, colliders);

        // A partition through the middle of each 12 x 12 hall, so the rooms read as two bays
        // instead of an empty box.
        AddRun(walls, "PartitionNW", new Vector3(-20f, 0f, -14f), 1, 0, 90f, colliders);
        AddRun(walls, "PartitionSE", new Vector3(20f, 0f, 14f), 1, 0, 90f, colliders);
    }

    /// <summary>Places <paramref name="count"/> 4 m wall modules starting at
    /// <paramref name="start"/>, stepping along +X (dirX = 1) or +Z (dirX = 0).</summary>
    private static void AddRun(Transform parent, string namePrefix, Vector3 start, int count, int dirX,
        float yaw, List<Collider> colliders)
    {
        for (int i = 0; i < count; i++)
        {
            Vector3 pos = start + (dirX == 1 ? new Vector3(Cell * i, 0f, 0f) : new Vector3(0f, 0f, Cell * i));
            AddWall(parent, namePrefix + "_" + i, pos, yaw, colliders);
        }
    }

    // -------------------------------------------------------------- pillars

    /// <summary>Structural pilasters against the inside of the perimeter wall: they break up
    /// 60 m of flat panel and give the outer lane vertical rhythm + cover.</summary>
    private static void BuildPerimeterPillars(Transform stage, List<Collider> colliders)
    {
        Transform pillars = Group("Pillars", stage);
        float offset = HalfSize - 0.8f;                 // 29.2 -> 0.02 m clear of the wall face
        float[] along = { -24f, -8f, 8f, 24f };

        foreach (float a in along)
        {
            AddPillar(pillars, "PillarN_" + a, new Vector3(a, 0f, -offset), colliders);
            AddPillar(pillars, "PillarS_" + a, new Vector3(a, 0f, offset), colliders);
            AddPillar(pillars, "PillarW_" + a, new Vector3(-offset, 0f, a), colliders);
            AddPillar(pillars, "PillarE_" + a, new Vector3(offset, 0f, a), colliders);
        }
    }

    private static void AddPillar(Transform parent, string name, Vector3 pos, List<Collider> colliders)
    {
        GameObject pillar = Place("template-detail", name, pos, 0f, parent);
        var box = pillar.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 2.1f, 0f);
        box.size = new Vector3(1.2f, 4.2f, 1.2f);
        colliders.Add(box);
    }

    // ------------------------------------------------------------ wiring

    private static void WirePathfinding(List<Collider> colliders)
    {
        var pf = Object.FindObjectOfType<UdonPathfindingManager>();
        if (pf == null)
        {
            Debug.LogWarning("[ModularArenaBuilder] PathfindingManager not found - wallColliders not rewired.");
            return;
        }

        pf.wallColliders = colliders.ToArray();
        UdonSharpEditorUtility.CopyProxyToUdon(pf);
    }

    // ------------------------------------------------------------- helpers

    private static Material EnsureKitMaterial()
    {
        EnsureFolder("Assets/HordeShooter", "Materials");

        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(KitTexturePath);
        if (texture == null)
        {
            Debug.LogError("[ModularArenaBuilder] kit texture missing: " + KitTexturePath);
            return null;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(KitMaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, KitMaterialPath);
        }

        material.mainTexture = texture;
        material.color = Color.white;
        material.SetFloat("_Glossiness", 0.15f);
        material.SetFloat("_Metallic", 0f);
        material.enableInstancing = true;
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        return material;
    }

    private static GameObject Place(string model, string name, Vector3 pos, float yaw, Transform parent)
    {
        string path = KitModels + "/" + model + ".fbx";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            Debug.LogError("[ModularArenaBuilder] missing kit model: " + path);
            return null;
        }

        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        if (PrefabUtility.IsPartOfPrefabInstance(go))
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = EnvLayer;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            r.sharedMaterial = kitMaterial;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        return go;
    }

    private static void AddMeshCollider(GameObject go, List<Collider> colliders)
    {
        var filter = go.GetComponent<MeshFilter>();
        if (filter == null)
        {
            Debug.LogWarning("[ModularArenaBuilder] no MeshFilter on " + go.name);
            return;
        }

        var mesh = go.AddComponent<MeshCollider>();
        mesh.sharedMesh = filter.sharedMesh;
        colliders.Add(mesh);
    }

    private static Transform Group(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.layer = EnvLayer;
        return go.transform;
    }

    private static void RemoveOldStage()
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        var doomed = new List<GameObject>();
        foreach (var go in roots)
        {
            string n = go.name;
            if (n == "Stage" || n == "Floor" ||
                n.StartsWith("Wall") || n.StartsWith("BldA_") || n.StartsWith("BldB_") || n.StartsWith("Ring_"))
            {
                doomed.Add(go);
            }
        }

        foreach (var go in doomed) Object.DestroyImmediate(go);
        if (doomed.Count > 0) Debug.Log("[ModularArenaBuilder] removed " + doomed.Count + " old stage objects.");
    }

    private static void EnsureFolder(string parent, string folder)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + folder))
            AssetDatabase.CreateFolder(parent, folder);
    }
}
