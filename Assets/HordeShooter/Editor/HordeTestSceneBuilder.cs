using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UdonSharpEditor;
using nyakomake.udon3dpathfinding;

/// <summary>
/// Builds the M0 fork-acceptance test scene: a 40x40 floor split by a wall with a
/// doorway gap, a UdonPathfindingManager wired to the fork's generated Compeito
/// material, and a PathfindingForkTest harness.
/// </summary>
public static class HordeTestSceneBuilder
{
    private const string ScenePath = "Assets/HordeShooter/Scenes/M0ForkTest.unity";
    private const string CompeitoMaterialPath =
        "Packages/com.panekit.udon-3d-pathfinding/Runtime/PathfindCompeito.compeito";

    [MenuItem("HordeShooter/Build M0 Fork Test Scene")]
    public static void BuildM0TestScene()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Walkable floor — deliberately NOT a wall collider (walls block, floor does not).
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.position = new Vector3(0f, -0.5f, 0f);
        floor.transform.localScale = new Vector3(40f, 1f, 40f);

        // Splitting wall with a doorway gap at z in (8, 12): a path from the west side
        // to the east side must detour through the gap.
        var wallA = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wallA.name = "WallA";
        wallA.transform.position = new Vector3(0f, 1.5f, -6f);
        wallA.transform.localScale = new Vector3(1f, 3f, 28f);

        var wallB = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wallB.name = "WallB";
        wallB.transform.position = new Vector3(0f, 1.5f, 16f);
        wallB.transform.localScale = new Vector3(1f, 3f, 8f);

        // Pathfinding manager wired to the fork.
        var managerGo = new GameObject("PathfindingManager");
        var manager = managerGo.AddUdonSharpComponent<UdonPathfindingManager>();
        manager.pathfindMaterial = AssetDatabase.LoadAssetAtPath<Material>(CompeitoMaterialPath);
        manager.gridSizeX = 40;
        manager.gridSizeY = 6;
        manager.gridSizeZ = 40;
        manager.cellSize = 1f;
        manager.gridOrigin = new Vector3(-20f, 0f, -20f);
        manager.itersPerFrame = 16;
        manager.wallColliders = new Collider[]
        {
            wallA.GetComponent<Collider>(),
            wallB.GetComponent<Collider>(),
        };

        // Test harness.
        var testGo = new GameObject("ForkTest");
        var test = testGo.AddUdonSharpComponent<PathfindingForkTest>();
        test.manager = manager;
        test.pathStart = MakePoint("PathStart", new Vector3(-15f, 0.5f, 0f));
        test.pathGoal = MakePoint("PathGoal", new Vector3(15f, 0.5f, 0f));
        test.flowSource = MakePoint("FlowSource", new Vector3(15f, 0.5f, 0f));
        test.flowProbe = MakePoint("FlowProbe", new Vector3(-15f, 0.5f, -15f));

        // All proxies exist before syncing proxy -> backing (cross-references resolve).
        UdonSharpEditorUtility.CopyProxyToUdon(manager);
        UdonSharpEditorUtility.CopyProxyToUdon(test);

        if (!AssetDatabase.IsValidFolder("Assets/HordeShooter"))
            AssetDatabase.CreateFolder("Assets", "HordeShooter");
        if (!AssetDatabase.IsValidFolder("Assets/HordeShooter/Scenes"))
            AssetDatabase.CreateFolder("Assets/HordeShooter", "Scenes");

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        Debug.Log("[HordeTestSceneBuilder] M0 test scene built and saved: " + ScenePath);
    }

    private static Transform MakePoint(string name, Vector3 pos)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        return go.transform;
    }
}
