using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace nyakomake.udon3dpathfinding
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class UdonPathfindDrawer : UdonSharpBehaviour
    {
        [Header("Reference")]
        public UdonPathfindingManager manager;

        [Header("Materials (GPU Instancing enabled)")]
        public Material wallMaterial;
        public Material pathMaterial;
        public Material startMaterial;
        public Material goalMaterial;

        [Header("Appearance")]
        public float wallScale = 0.95f;
        public float pathScale = 0.3f;
        public float pathSpacing = 0.5f;
        public float markerScale = 0.5f;

        private const int MAX_INSTANCES = 1023;

        private Mesh cubeMesh;

        private Matrix4x4[] drawBatch;
        private Matrix4x4[] singleMatrix;

        private Matrix4x4[] wallMatrices;
        private int wallCount;
        private bool wallsBuilt;

        private Matrix4x4[] pathMatrices;
        private int pathCount;
        private Vector3[] lastWaypoints;

        private int lastLeafCount = -1;
        private Vector3 lastOrigin;
        private int lastGSX = -1;
        private int lastGSY = -1;
        private int lastGSZ = -1;
        private float lastCellSize = -1f;

        private void Start()
        {
            drawBatch = new Matrix4x4[MAX_INSTANCES];
            singleMatrix = new Matrix4x4[1];
            CreateCubeMesh();
        }

        private void CreateCubeMesh()
        {
            Vector3[] verts = new Vector3[]
            {
            new Vector3(0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,0.5f), new Vector3(0.5f,0.5f,0.5f), new Vector3(0.5f,0.5f,-0.5f),
            new Vector3(-0.5f,-0.5f,0.5f), new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(-0.5f,0.5f,-0.5f), new Vector3(-0.5f,0.5f,0.5f),
            new Vector3(0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,-0.5f), new Vector3(0.5f,0.5f,-0.5f),
            new Vector3(0.5f,-0.5f,-0.5f), new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(-0.5f,-0.5f,0.5f), new Vector3(0.5f,-0.5f,0.5f),
            new Vector3(0.5f,-0.5f,0.5f), new Vector3(-0.5f,-0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f), new Vector3(0.5f,0.5f,0.5f),
            new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f), new Vector3(0.5f,0.5f,-0.5f), new Vector3(-0.5f,0.5f,-0.5f),
            };

            Vector3[] norms = new Vector3[]
            {
            new Vector3(1,0,0), new Vector3(1,0,0), new Vector3(1,0,0), new Vector3(1,0,0),
            new Vector3(-1,0,0), new Vector3(-1,0,0), new Vector3(-1,0,0), new Vector3(-1,0,0),
            new Vector3(0,1,0), new Vector3(0,1,0), new Vector3(0,1,0), new Vector3(0,1,0),
            new Vector3(0,-1,0), new Vector3(0,-1,0), new Vector3(0,-1,0), new Vector3(0,-1,0),
            new Vector3(0,0,1), new Vector3(0,0,1), new Vector3(0,0,1), new Vector3(0,0,1),
            new Vector3(0,0,-1), new Vector3(0,0,-1), new Vector3(0,0,-1), new Vector3(0,0,-1),
            };

            int[] tris = new int[]
            {
            0,2,1, 0,3,2,
            4,6,5, 4,7,6,
            8,10,9, 8,11,10,
            12,14,13, 12,15,14,
            16,18,17, 16,19,18,
            20,22,21, 20,23,22,
            };

            cubeMesh = new Mesh();
            cubeMesh.name = "PathfindDrawerCube";
            cubeMesh.vertices = verts;
            cubeMesh.normals = norms;
            cubeMesh.triangles = tris;
            cubeMesh.RecalculateBounds();
        }

        private bool IsGridValid()
        {
            if (manager == null) return false;
            int[] v2l = manager.voxelToLeaf;
            if (v2l == null) return false;
            int expected = manager.gridSizeX * manager.gridSizeY * manager.gridSizeZ;
            return v2l.Length == expected;
        }

        private bool NeedsRebuild()
        {
            if (!wallsBuilt) return true;
            if (manager.leafCount != lastLeafCount) return true;
            if (manager.gridOrigin != lastOrigin) return true;
            if (manager.gridSizeX != lastGSX || manager.gridSizeY != lastGSY || manager.gridSizeZ != lastGSZ) return true;
            if (manager.cellSize != lastCellSize) return true;
            return false;
        }

        private void BuildWalls()
        {
            int gsX = manager.gridSizeX;
            int gsY = manager.gridSizeY;
            int gsZ = manager.gridSizeZ;
            float cs = manager.cellSize;
            Vector3 origin = manager.gridOrigin;
            int[] v2l = manager.voxelToLeaf;
            if (v2l == null) return;

            int total = gsX * gsY * gsZ;
            int count = 0;
            for (int i = 0; i < total; i++)
            {
                if (v2l[i] == -1) count++;
            }

            wallMatrices = new Matrix4x4[count];
            int idx = 0;
            Vector3 scaleVec = Vector3.one * cs * wallScale * 0.5f;
            for (int z = 0; z < gsZ; z++)
            {
                for (int y = 0; y < gsY; y++)
                {
                    for (int x = 0; x < gsX; x++)
                    {
                        int vi = x + y * gsX + z * gsX * gsY;
                        if (v2l[vi] == -1)
                        {
                            Vector3 pos = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cs;
                            wallMatrices[idx] = Matrix4x4.TRS(pos, Quaternion.identity, scaleVec);
                            idx++;
                        }
                    }
                }
            }
            wallCount = count;
            wallsBuilt = true;
            lastLeafCount = manager.leafCount;
            lastOrigin = manager.gridOrigin;
            lastGSX = gsX;
            lastGSY = gsY;
            lastGSZ = gsZ;
            lastCellSize = cs;
        }

        private void RebuildPath()
        {
            Vector3[] wps = manager.waypoints;
            if (wps == null || wps.Length == 0)
            {
                pathMatrices = null;
                pathCount = 0;
                return;
            }

            float spacing = manager.cellSize * pathSpacing;
            if (spacing < 0.001f) spacing = 0.001f;

            int totalPoints = 0;
            for (int i = 0; i < wps.Length - 1; i++)
            {
                float dist = Vector3.Distance(wps[i], wps[i + 1]);
                totalPoints += Mathf.CeilToInt(dist / spacing);
            }
            totalPoints += 1;

            pathMatrices = new Matrix4x4[totalPoints];
            Vector3 scaleVec = Vector3.one * manager.cellSize * pathScale;
            int idx = 0;
            for (int i = 0; i < wps.Length - 1; i++)
            {
                Vector3 a = wps[i];
                Vector3 b = wps[i + 1];
                float dist = Vector3.Distance(a, b);
                int steps = Mathf.CeilToInt(dist / spacing);
                if (steps < 1) steps = 1;
                for (int s = 0; s < steps; s++)
                {
                    float t = (float)s / steps;
                    Vector3 pos = Vector3.Lerp(a, b, t);
                    pathMatrices[idx] = Matrix4x4.TRS(pos, Quaternion.identity, scaleVec);
                    idx++;
                }
            }
            Vector3 last = wps[wps.Length - 1];
            pathMatrices[idx] = Matrix4x4.TRS(last, Quaternion.identity, scaleVec);
            idx++;
            pathCount = idx;
        }

        private void DrawBatched(Matrix4x4[] matrices, int count, Material mat)
        {
            if (count <= 0 || matrices == null || mat == null) return;
            int offset = 0;
            while (offset < count)
            {
                int batch = Mathf.Min(MAX_INSTANCES, count - offset);
                for (int i = 0; i < batch; i++)
                {
                    drawBatch[i] = matrices[offset + i];
                }
                VRCGraphics.DrawMeshInstanced(cubeMesh, 0, mat, drawBatch, batch);
                offset += batch;
            }
        }

        private void LateUpdate()
        {
            if (manager == null) return;
            if (cubeMesh == null) return;

            if (IsGridValid() && NeedsRebuild())
            {
                BuildWalls();
            }

            if (wallsBuilt)
            {
                DrawBatched(wallMatrices, wallCount, wallMaterial);
            }

            Vector3[] wps = manager.waypoints;
            if (wps != lastWaypoints)
            {
                lastWaypoints = wps;
                RebuildPath();
            }
            DrawBatched(pathMatrices, pathCount, pathMaterial);

            Vector3 markerScaleVec = Vector3.one * manager.cellSize * markerScale;
            if (manager.startLeafIdx >= 0 && startMaterial != null)
            {
                singleMatrix[0] = Matrix4x4.TRS(manager.startWorld, Quaternion.identity, markerScaleVec);
                VRCGraphics.DrawMeshInstanced(cubeMesh, 0, startMaterial, singleMatrix, 1);
            }
            if (manager.goalLeafIdx >= 0 && goalMaterial != null)
            {
                singleMatrix[0] = Matrix4x4.TRS(manager.goalWorld, Quaternion.identity, markerScaleVec);
                VRCGraphics.DrawMeshInstanced(cubeMesh, 0, goalMaterial, singleMatrix, 1);
            }
        }

        private void OnDestroy()
        {
            if (cubeMesh != null) Destroy(cubeMesh);
        }
    }
}