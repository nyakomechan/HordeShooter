using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the placeholder low-poly zombie mesh with vertex-color limb masks:
///   color.r = arm mask (secondary bob), color.g = head mask.
/// Flat-shaded boxes, ~144 verts / 72 tris.
/// </summary>
public static class EnemyMeshBuilder
{
    public const string MeshPath = "Assets/HordeShooter/Art/EnemyPlaceholder.asset";

    [MenuItem("HordeShooter/Build Enemy Mesh")]
    public static void BuildEnemyMesh()
    {
        EnsureFolders();
        Mesh mesh = BuildMesh();
        AssetDatabase.CreateAsset(mesh, MeshPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[EnemyMeshBuilder] enemy mesh created: " + MeshPath);
    }

    public static Mesh BuildMesh()
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var cols = new List<Color>();
        var tris = new List<int>();

        Color none = new Color(0f, 0f, 0f, 0f);
        Color arm = new Color(1f, 0f, 0f, 0f);
        Color head = new Color(0f, 1f, 0f, 0f);

        // torso: y 0.5..1.3
        AddBox(verts, norms, cols, tris, new Vector3(0f, 0.9f, 0f), new Vector3(0.22f, 0.4f, 0.13f), none);
        // head: y 1.31..1.59
        AddBox(verts, norms, cols, tris, new Vector3(0f, 1.45f, 0.03f), new Vector3(0.13f, 0.14f, 0.13f), head);
        // arms stretched forward (zombie pose): z 0.03..0.53
        AddBox(verts, norms, cols, tris, new Vector3(-0.29f, 1.13f, 0.28f), new Vector3(0.065f, 0.065f, 0.25f), arm);
        AddBox(verts, norms, cols, tris, new Vector3(0.29f, 1.13f, 0.28f), new Vector3(0.065f, 0.065f, 0.25f), arm);
        // legs: y 0..0.5
        AddBox(verts, norms, cols, tris, new Vector3(-0.12f, 0.25f, 0f), new Vector3(0.075f, 0.25f, 0.085f), none);
        AddBox(verts, norms, cols, tris, new Vector3(0.12f, 0.25f, 0f), new Vector3(0.075f, 0.25f, 0.085f), none);

        Mesh mesh = new Mesh();
        mesh.name = "EnemyPlaceholder";
        mesh.vertices = verts.ToArray();
        mesh.normals = norms.ToArray();
        mesh.colors = cols.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddBox(List<Vector3> verts, List<Vector3> norms, List<Color> cols, List<int> tris,
        Vector3 center, Vector3 ext, Color color)
    {
        Vector3[] c = new Vector3[8];
        for (int i = 0; i < 8; i++)
            c[i] = center + new Vector3(
                ((i & 1) != 0 ? ext.x : -ext.x),
                ((i & 2) != 0 ? ext.y : -ext.y),
                ((i & 4) != 0 ? ext.z : -ext.z));

        int[][] faces = new int[][]
        {
            new int[] { 1, 3, 7, 5 },  // +X
            new int[] { 0, 4, 6, 2 },  // -X
            new int[] { 2, 6, 7, 3 },  // +Y
            new int[] { 0, 1, 5, 4 },  // -Y
            new int[] { 4, 5, 7, 6 },  // +Z
            new int[] { 0, 2, 3, 1 },  // -Z
        };
        Vector3[] fn = new Vector3[]
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
        };

        for (int f = 0; f < 6; f++)
        {
            int baseIdx = verts.Count;
            int[] q = faces[f];
            for (int k = 0; k < 4; k++)
            {
                verts.Add(c[q[k]]);
                norms.Add(fn[f]);
                cols.Add(color);
            }
            tris.Add(baseIdx);
            tris.Add(baseIdx + 1);
            tris.Add(baseIdx + 2);
            tris.Add(baseIdx);
            tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 3);
        }
    }

    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/HordeShooter"))
            AssetDatabase.CreateFolder("Assets", "HordeShooter");
        if (!AssetDatabase.IsValidFolder("Assets/HordeShooter/Art"))
            AssetDatabase.CreateFolder("Assets/HordeShooter", "Art");
    }
}
