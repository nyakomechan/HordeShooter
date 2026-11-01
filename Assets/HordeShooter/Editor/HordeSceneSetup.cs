using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UdonSharpEditor;
using nyakomake.udon3dpathfinding;
using VRC.SDKBase;
using VRC.SDK3.Components;

/// <summary>
/// One-click builder for the horde arena blockout scene:
/// 60x60 arena, perimeter walls, two buildings with doorways, a central corridor
/// ring, 8 spawn points, the forked UdonPathfindingManager, and a fully wired
/// HordeSim driver (sim material from EnemySim.compeito, instanced enemy material,
/// placeholder mesh).
/// </summary>
public static class HordeSceneSetup
{
    private const int EnvLayer = 11; // VRChat Environment layer
    private const string ArenaScenePath = "Assets/HordeShooter/Scenes/HordeArena.unity";
    private const string SimCompeitoPath = "Assets/HordeShooter/GPU/EnemySim.compeito";
    private const string FlowBakeCompeitoPath = "Assets/HordeShooter/GPU/FlowBake.compeito";
    private const string EnemyShaderName = "HordeShooter/EnemyRender";
    private const string EnemyMeshPath = "Assets/HordeShooter/Art/EnemyPlaceholder.asset";
    private const string EnemyMatPath = "Assets/HordeShooter/Materials/EnemyRender.mat";

    [MenuItem("HordeShooter/Build Arena Scene")]
    public static void BuildArenaScene()
    {
        EnemyMeshBuilder.BuildEnemyMesh();
        EnsureFolder("Assets/HordeShooter", "Materials");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ---- Arena geometry (Environment layer) ----
        var floor = MakeBlock("Floor", new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f));

        var wallColliders = new System.Collections.Generic.List<Collider>();
        wallColliders.Add(MakeWall("WallN", new Vector3(0f, 1.5f, -30f), new Vector3(62f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("WallS", new Vector3(0f, 1.5f, 30f), new Vector3(62f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("WallW", new Vector3(-30f, 1.5f, 0f), new Vector3(1f, 3f, 60f), wallColliders));
        wallColliders.Add(MakeWall("WallE", new Vector3(30f, 1.5f, 0f), new Vector3(1f, 3f, 60f), wallColliders));

        // Building A (NW) with a wide 8 m doorway on its north wall (facing the plaza;
        // wide doorways keep the crowd flowing instead of plugging at the threshold).
        wallColliders.Add(MakeWall("BldA_N", new Vector3(-18f, 1.5f, -26f), new Vector3(16f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("BldA_W", new Vector3(-26f, 1.5f, -18f), new Vector3(1f, 3f, 17f), wallColliders));
        wallColliders.Add(MakeWall("BldA_S1", new Vector3(-25f, 1.5f, -10f), new Vector3(6f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("BldA_S2", new Vector3(-11f, 1.5f, -10f), new Vector3(6f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("BldA_E", new Vector3(-10f, 1.5f, -18f), new Vector3(1f, 3f, 17f), wallColliders));

        // Building B (SE) with a wide 8 m doorway on its south wall.
        wallColliders.Add(MakeWall("BldB_S", new Vector3(18f, 1.5f, 26f), new Vector3(16f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("BldB_E", new Vector3(26f, 1.5f, 18f), new Vector3(1f, 3f, 17f), wallColliders));
        wallColliders.Add(MakeWall("BldB_N1", new Vector3(25f, 1.5f, 10f), new Vector3(6f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("BldB_N2", new Vector3(11f, 1.5f, 10f), new Vector3(6f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("BldB_W", new Vector3(10f, 1.5f, 18f), new Vector3(1f, 3f, 17f), wallColliders));

        // Central corridor ring with four offset gaps.
        wallColliders.Add(MakeWall("Ring_W", new Vector3(-8f, 1.5f, -3f), new Vector3(1f, 3f, 11f), wallColliders));
        wallColliders.Add(MakeWall("Ring_E", new Vector3(8f, 1.5f, 3f), new Vector3(1f, 3f, 11f), wallColliders));
        wallColliders.Add(MakeWall("Ring_N", new Vector3(-3f, 1.5f, -8f), new Vector3(11f, 3f, 1f), wallColliders));
        wallColliders.Add(MakeWall("Ring_S", new Vector3(3f, 1.5f, 8f), new Vector3(11f, 3f, 1f), wallColliders));

        // ---- VRC world (descriptor + spawn so ClientSim/MultiSim create a player) ----
        var vrcWorldPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCWorld.prefab");
        if (vrcWorldPrefab != null)
        {
            var world = (GameObject)PrefabUtility.InstantiatePrefab(vrcWorldPrefab);
            world.transform.position = new Vector3(0f, 0f, 0f);
        }
        else
        {
            Debug.LogWarning("[HordeSceneSetup] VRCWorld prefab not found — no descriptor in scene");
        }

        // ---- Camera & light (blockout lighting; baked lighting comes in M6) ----
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        camGo.transform.position = new Vector3(0f, 26f, -38f);
        camGo.transform.rotation = Quaternion.Euler(33f, 0f, 0f);
        cam.farClipPlane = 300f;

        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.shadows = LightShadows.Soft;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // ---- Spawn points ----
        var spawns = new System.Collections.Generic.List<Transform>();
        spawns.Add(MakePoint("Spawn0", new Vector3(-24f, 0.1f, -24f)));
        spawns.Add(MakePoint("Spawn1", new Vector3(24f, 0.1f, -24f)));
        spawns.Add(MakePoint("Spawn2", new Vector3(-24f, 0.1f, 24f)));
        spawns.Add(MakePoint("Spawn3", new Vector3(24f, 0.1f, 24f)));
        spawns.Add(MakePoint("Spawn4", new Vector3(-24f, 0.1f, 0f)));
        spawns.Add(MakePoint("Spawn5", new Vector3(24f, 0.1f, 0f)));
        spawns.Add(MakePoint("Spawn6", new Vector3(0f, 0.1f, -24f)));
        spawns.Add(MakePoint("Spawn7", new Vector3(0f, 0.1f, 24f)));

        // ---- Pathfinding manager (fork) ----
        var pfGo = new GameObject("PathfindingManager");
        var pf = pfGo.AddUdonSharpComponent<UdonPathfindingManager>();
        pf.pathfindMaterial = AssetDatabase.LoadAssetAtPath<Material>(
            "Packages/com.panekit.udon-3d-pathfinding/Runtime/PathfindCompeito.compeito");
        pf.gridSizeX = 64;
        pf.gridSizeY = 2;   // ground layer only: enemies are ground-clamped, and a
                            // 2-voxel grid seals 3 m walls so the BFS cannot route
                            // over buildings through the air (that pinned crowds).
        pf.gridSizeZ = 64;
        pf.cellSize = 1f;
        pf.gridOrigin = new Vector3(-32f, 0f, -32f);
        pf.itersPerFrame = 16;
        pf.wallColliders = wallColliders.ToArray();

        // ---- HordeSim driver ----
        var simGo = new GameObject("HordeSim");
        var sim = simGo.AddUdonSharpComponent<HordeSim>();
        sim.simMaterial = AssetDatabase.LoadAssetAtPath<Material>(SimCompeitoPath);
        sim.enemyMesh = AssetDatabase.LoadAssetAtPath<Mesh>(EnemyMeshPath);

        Material enemyMat = AssetDatabase.LoadAssetAtPath<Material>(EnemyMatPath);
        if (enemyMat == null)
        {
            Shader shader = Shader.Find(EnemyShaderName);
            if (shader == null)
            {
                Debug.LogError("[HordeSceneSetup] shader not found: " + EnemyShaderName);
                return;
            }
            enemyMat = new Material(shader);
            AssetDatabase.CreateAsset(enemyMat, EnemyMatPath);
        }
        enemyMat.enableInstancing = true;
        sim.enemyMaterial = enemyMat;
        sim.spawnPoints = spawns.ToArray();
        sim.dummyCenter = new Vector3(0f, 1f, 0f);
        sim.dummyExtent = new Vector2(56f, 56f);

        // ---- Flow field bridge ----
        var flowGo = new GameObject("FlowFieldBridge");
        var flow = flowGo.AddUdonSharpComponent<FlowFieldBridge>();
        flow.pathfinder = pf;
        flow.hordeSim = sim;
        flow.bakeMaterial = AssetDatabase.LoadAssetAtPath<Material>(FlowBakeCompeitoPath);

        // ---- Gun (pickup + hitscan controller) ----
        var gunGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        gunGo.name = "Gun";
        gunGo.transform.position = new Vector3(2.5f, 1.2f, 2.5f);
        gunGo.transform.localScale = new Vector3(0.12f, 0.12f, 0.5f);
        gunGo.AddComponent<VRCPickup>();
        var gun = gunGo.AddUdonSharpComponent<GunController>();
        gun.hordeSim = sim;

        // ---- HordeNet (owner-authoritative sync) ----
        var netGo = new GameObject("HordeNet");
        var net = netGo.AddUdonSharpComponent<HordeNet>();
        net.hordeSim = sim;
        net.gun = gun;
        gun.hordeNet = net;
        // downed players (playerHp <= 0) are excluded from enemy targeting and
        // from the flow-field source selection
        sim.hordeNet = net;
        flow.hordeNet = net;
        // Late-joiner catch-up on its OWN GameObject: MultiSim/VRChat serialize
        // per network-id (GameObject), so sharing one would re-send the 6KB
        // catch-up array on every 3Hz HordeNet snapshot.
        var catchupGo = new GameObject("HordeNetCatchup");
        var catchup = catchupGo.AddUdonSharpComponent<HordeNetCatchup>();
        catchup.hordeSim = sim;
        catchup.hordeNet = net;
        net.catchup = catchup;

        // Sync proxies -> backing (all proxies exist first).
        UdonSharpEditorUtility.CopyProxyToUdon(pf);
        UdonSharpEditorUtility.CopyProxyToUdon(sim);
        UdonSharpEditorUtility.CopyProxyToUdon(flow);
        UdonSharpEditorUtility.CopyProxyToUdon(gun);
        UdonSharpEditorUtility.CopyProxyToUdon(net);
        UdonSharpEditorUtility.CopyProxyToUdon(catchup);

        EnsureFolder("Assets/HordeShooter", "Scenes");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ArenaScenePath);
        Debug.Log("[HordeSceneSetup] arena scene built and saved: " + ArenaScenePath);
    }

    private static GameObject MakeBlock(string name, Vector3 pos, Vector3 scale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = pos;
        go.transform.localScale = scale;
        go.layer = EnvLayer;
        return go;
    }

    private static Collider MakeWall(string name, Vector3 pos, Vector3 scale,
        System.Collections.Generic.List<Collider> colliders)
    {
        var go = MakeBlock(name, pos, scale);
        Collider col = go.GetComponent<Collider>();
        colliders.Add(col);
        return col;
    }

    private static Transform MakePoint(string name, Vector3 pos)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        return go.transform;
    }

    private static void EnsureFolder(string parent, string folder)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + folder))
            AssetDatabase.CreateFolder(parent, folder);
    }
}
