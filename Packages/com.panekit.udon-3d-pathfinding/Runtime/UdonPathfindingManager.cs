using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.Rendering;
using VRC.Udon.Common.Interfaces;
using imaginantia.Compeito;

namespace nyakomake.udon3dpathfinding
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class UdonPathfindingManager : UdonSharpBehaviour
    {
        [HideInInspector]
        public Material pathfindMaterial;
        public Collider[] wallColliders;
        public int gridSizeX = 16;
        public int gridSizeY = 16;
        public int gridSizeZ = 16;
        public float cellSize = 1f;
        public Vector3 gridOrigin = Vector3.zero;
        public float heightFactor = 0.2f;
        public int itersPerFrame = 8;
        public int maxIterations = 1024;
        // Voxel probe shrink (0..0.5). At 0.5 a wall that merely TOUCHES a voxel's
        // face marks the whole voxel solid, pinning agents in the walkable sliver
        // next to the wall. Shrinking the probe (e.g. 0.45) ignores exact-touching
        // boundaries so walkable slivers stay walkable.
        public float voxelProbeShrink = 0.45f;

        [HideInInspector]
        public bool isBusy;
        [HideInInspector]
        public bool pathFound;
        [HideInInspector]
        public string pathError;
        [HideInInspector]
        public Vector3[] waypoints;
        [HideInInspector]
        public UdonSharpBehaviour resultReceiver;
        [HideInInspector]
        public string foundEventName = "OnPathFound";
        [HideInInspector]
        public string failedEventName = "OnPathFailed";

        private const int STATE_IDLE = 0;
        private const int STATE_RUNNING = 1;
        private const int STATE_GOAL_PENDING = 2;
        private const int STATE_PATH_PENDING = 3;

        private byte[] grid;
        [HideInInspector]
        public int[] voxelToLeaf;
        [HideInInspector]
        public int[] leafVoxelIndex;
        private int[] neighbors;
        private float[] leafCosts;
        [HideInInspector]
        public int leafCount;

        private RenderTexture pathDataA;
        private RenderTexture pathDataB;
        private RenderTexture goalResultTex;
        private Texture2D neighborTex;
        private Texture2D costTex;

        private int kernelWavefront = -1;
        private int kernelGoalDetect = -1;
        private int kernelReset = -1;

        private int texWidth;
        private int texHeight;

        private DataList requestQueue = new DataList();
        [HideInInspector]
        public int startLeafIdx = -1;
        [HideInInspector]
        public int goalLeafIdx = -1;
        [HideInInspector]
        public Vector3 startWorld;
        [HideInInspector]
        public Vector3 goalWorld;
        [HideInInspector]
        public int searchIterationCount;
        [HideInInspector]
        public int searchFrameCount;
        private int currentIteration;
        private int state;
        private bool readA = true;

        private Color[] pathDataBuffer;
        private Color[] goalResultBuffer;

        private Vector3 requestStart;
        private Vector3 requestGoal;
        private UdonSharpBehaviour requestReceiver;
        private string requestFoundEvent;
        private string requestFailedEvent;
        private bool requestFlood;

        // ===== Flow field support (panekit fork) =====
        // After RequestFlowField completes, the wavefront buffer holds a parent pointer
        // per leaf pointing one step toward the flood source. GetFlowDataTexture()
        // exposes it so a downstream GPGPU pass can use it as a flow field with no
        // CPU readback.
        [HideInInspector]
        public bool flowFieldReady;
        [HideInInspector]
        public int flowFieldSourceLeaf = -1;
        private int floodTargetIterations;

        private int[] reconstructBuffer;
        private Vector3[] smoothBuffer;
        private int wallLayerMask;

        private int[] dirX = new int[] { 1, -1, 0, 0, 0, 0 };
        private int[] dirY = new int[] { 0, 0, 1, -1, 0, 0 };
        private int[] dirZ = new int[] { 0, 0, 0, 0, 1, -1 };

        void Start()
        {
            EnsureMaterial();
            Rebuild();
        }

        private void EnsureMaterial()
        {
            if (pathfindMaterial != null) return;

            Debug.LogError("UdonPathfindingManager: PathfindCompeito material is not assigned. Please assign the generated material in the Inspector.");
            enabled = false;
        }

        public void Rebuild()
        {
            if (pathfindMaterial == null)
            {
                EnsureMaterial();
            }

            DisposeGpuResources();

            BuildGrid();
            BuildVoxelData();
            InitGpu();

            isBusy = false;
            pathFound = false;
            pathError = "";
            state = STATE_IDLE;
            Debug.Log("[UdonPathfindingManager] Rebuild complete. Leaf count: " + leafCount);
        }

        private void BuildGrid()
        {
            int gsX = gridSizeX;
            int gsY = gridSizeY;
            int gsZ = gridSizeZ;
            int total = gsX * gsY * gsZ;
            grid = new byte[total];

            wallLayerMask = 0;
            if (wallColliders == null) return;

            for (int c = 0; c < wallColliders.Length; c++)
            {
                Collider col = wallColliders[c];
                if (col != null)
                {
                    wallLayerMask |= (1 << col.gameObject.layer);
                }
            }

            for (int c = 0; c < wallColliders.Length; c++)
            {
                Collider col = wallColliders[c];
                if (col == null) continue;

                MeshCollider mesh = col.GetComponent<MeshCollider>();
                if (mesh != null)
                {
                    FillMeshCollider(mesh);
                    continue;
                }

                FillColliderWithOverlap(col);
            }
        }

        private void FillColliderWithOverlap(Collider col)
        {
            int minX, minY, minZ, maxX, maxY, maxZ;
            BoundsToVoxelRange(col.bounds, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);

            int gsX = gridSizeX;
            int gsY = gridSizeY;

            Vector3 halfExtents = new Vector3(cellSize, cellSize, cellSize) * voxelProbeShrink;
            Collider[] overlapResults = new Collider[16];

            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        Vector3 center = VoxelCenterToWorld(x, y, z);
                        int hitCount = Physics.OverlapBoxNonAlloc(center, halfExtents, overlapResults, Quaternion.identity);
                        bool overlaps = false;
                        for (int i = 0; i < hitCount; i++)
                        {
                            if (overlapResults[i] == col)
                            {
                                overlaps = true;
                                break;
                            }
                        }
                        if (overlaps)
                        {
                            grid[x + y * gsX + z * gsX * gsY] = 1;
                        }
                    }
        }

        private void BoundsToVoxelRange(Bounds b, out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
        {
            int gsX = gridSizeX;
            int gsY = gridSizeY;
            int gsZ = gridSizeZ;

            float minXF = (b.min.x - gridOrigin.x) / cellSize;
            float minYF = (b.min.y - gridOrigin.y) / cellSize;
            float minZF = (b.min.z - gridOrigin.z) / cellSize;
            float maxXF = (b.max.x - gridOrigin.x) / cellSize;
            float maxYF = (b.max.y - gridOrigin.y) / cellSize;
            float maxZF = (b.max.z - gridOrigin.z) / cellSize;

            minX = Mathf.Clamp(Mathf.FloorToInt(minXF), 0, gsX - 1);
            minY = Mathf.Clamp(Mathf.FloorToInt(minYF), 0, gsY - 1);
            minZ = Mathf.Clamp(Mathf.FloorToInt(minZF), 0, gsZ - 1);
            maxX = Mathf.Clamp(Mathf.CeilToInt(maxXF) - 1, 0, gsX - 1);
            maxY = Mathf.Clamp(Mathf.CeilToInt(maxYF) - 1, 0, gsY - 1);
            maxZ = Mathf.Clamp(Mathf.CeilToInt(maxZF) - 1, 0, gsZ - 1);
        }

        private Vector3 VoxelCenterToWorld(int x, int y, int z)
        {
            return gridOrigin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cellSize;
        }

        private void FillMeshCollider(MeshCollider mesh)
        {
            int minX, minY, minZ, maxX, maxY, maxZ;
            BoundsToVoxelRange(mesh.bounds, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);

            int gsX = gridSizeX;
            int gsY = gridSizeY;

            Vector3 halfExtents = new Vector3(cellSize, cellSize, cellSize) * voxelProbeShrink;
            Collider[] overlapResults = new Collider[16];

            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        Vector3 center = VoxelCenterToWorld(x, y, z);
                        int hitCount = Physics.OverlapBoxNonAlloc(center, halfExtents, overlapResults, Quaternion.identity);
                        bool overlaps = false;
                        for (int i = 0; i < hitCount; i++)
                        {
                            if (overlapResults[i] == mesh)
                            {
                                overlaps = true;
                                break;
                            }
                        }
                        if (overlaps)
                        {
                            grid[x + y * gsX + z * gsX * gsY] = 1;
                        }
                    }
        }

        private void BuildVoxelData()
        {
            int gsX = gridSizeX;
            int gsY = gridSizeY;
            int gsZ = gridSizeZ;
            int total = gsX * gsY * gsZ;

            voxelToLeaf = new int[total];
            for (int i = 0; i < total; i++) voxelToLeaf[i] = -1;

            leafCount = 0;
            for (int i = 0; i < total; i++)
            {
                if (grid[i] == 0) leafCount++;
            }

            leafVoxelIndex = new int[leafCount];
            neighbors = new int[leafCount * 6];
            leafCosts = new float[leafCount];

            for (int i = 0; i < leafCount * 6; i++) neighbors[i] = -1;

            int li = 0;
            for (int z = 0; z < gsZ; z++)
                for (int y = 0; y < gsY; y++)
                    for (int x = 0; x < gsX; x++)
                    {
                        int vi = x + y * gsX + z * gsX * gsY;
                        if (grid[vi] == 0)
                        {
                            voxelToLeaf[vi] = li;
                            leafVoxelIndex[li] = vi;
                            leafCosts[li] = 1.0f + heightFactor * y;
                            li++;
                        }
                    }

            for (int i = 0; i < leafCount; i++)
            {
                int vi = leafVoxelIndex[i];
                int x = vi % gsX;
                int y = (vi / gsX) % gsY;
                int z = vi / (gsX * gsY);

                for (int d = 0; d < 6; d++)
                {
                    int nx = x + dirX[d];
                    int ny = y + dirY[d];
                    int nz = z + dirZ[d];
                    if (nx >= 0 && nx < gsX && ny >= 0 && ny < gsY && nz >= 0 && nz < gsZ)
                    {
                        int nvi = nx + ny * gsX + nz * gsX * gsY;
                        neighbors[i * 6 + d] = voxelToLeaf[nvi];
                    }
                }
            }
        }

        private void InitGpu()
        {
            if (pathfindMaterial == null) return;

            kernelWavefront = pathfindMaterial.FindPass("WavefrontExpand");
            kernelGoalDetect = pathfindMaterial.FindPass("GoalDetect");
            kernelReset = pathfindMaterial.FindPass("Reset");

            if (kernelWavefront < 0 || kernelGoalDetect < 0 || kernelReset < 0)
            {
                Debug.LogError("UdonPathfindingManager: kernel not found");
                return;
            }

            texWidth = Mathf.CeilToInt(Mathf.Sqrt(leafCount));
            texHeight = Mathf.CeilToInt((float)leafCount / texWidth);
            int neighborHeight = texHeight * 2;

            neighborTex = new Texture2D(texWidth, neighborHeight, TextureFormat.RGBAFloat, false);
            neighborTex.filterMode = FilterMode.Point;
            neighborTex.wrapMode = TextureWrapMode.Clamp;

            Color[] neighborArr = new Color[texWidth * neighborHeight];
            for (int i = 0; i < leafCount; i++)
            {
                int tx = i % texWidth;
                int ty = i / texWidth;
                neighborArr[tx + (ty * 2 + 0) * texWidth] = new Color(
                    neighbors[i * 6 + 0],
                    neighbors[i * 6 + 1],
                    neighbors[i * 6 + 2],
                    neighbors[i * 6 + 3]);
                neighborArr[tx + (ty * 2 + 1) * texWidth] = new Color(
                    neighbors[i * 6 + 4],
                    neighbors[i * 6 + 5],
                    0,
                    0);
            }
            neighborTex.SetPixels(neighborArr);
            neighborTex.Apply(false);

            costTex = new Texture2D(texWidth, texHeight, TextureFormat.RFloat, false);
            costTex.filterMode = FilterMode.Point;
            costTex.wrapMode = TextureWrapMode.Clamp;
            Color[] costArr = new Color[texWidth * texHeight];
            for (int i = 0; i < leafCount; i++) costArr[i] = new Color(leafCosts[i], 0, 0, 0);
            costTex.SetPixels(costArr);
            costTex.Apply(false);

            pathDataA = Compeito.CreateRT("PathDataA", texWidth, texHeight, RenderTextureFormat.ARGBFloat);
            pathDataB = Compeito.CreateRT("PathDataB", texWidth, texHeight, RenderTextureFormat.ARGBFloat);
            goalResultTex = Compeito.CreateRT("GoalResult", 1, 1, RenderTextureFormat.ARGBFloat);

            pathfindMaterial.SetTexture("_Neighbors", neighborTex);
            pathfindMaterial.SetTexture("_LeafCosts", costTex);
            pathfindMaterial.SetInt("_NodeCount", leafCount);
            pathfindMaterial.SetInt("_LeafTexWidth", texWidth);

            pathDataBuffer = new Color[texWidth * texHeight];
            goalResultBuffer = new Color[1];
            reconstructBuffer = new int[leafCount];
            smoothBuffer = new Vector3[leafCount];
        }

        public void RequestPath(Vector3 start, Vector3 goal)
        {
            RequestPath(start, goal, resultReceiver);
        }

        public void RequestPath(Vector3 start, Vector3 goal, UdonSharpBehaviour receiver)
        {
            RequestPath(start, goal, receiver, foundEventName, failedEventName);
        }

        public void RequestPath(Vector3 start, Vector3 goal, UdonSharpBehaviour receiver, string foundEvent, string failedEvent)
        {
            if (leafCount == 0)
            {
                pathFound = false;
                pathError = "No walkable voxels";
                Notify(receiver, failedEvent);
                return;
            }

            DataDictionary request = new DataDictionary();
            request.SetValue("sx", new DataToken(start.x));
            request.SetValue("sy", new DataToken(start.y));
            request.SetValue("sz", new DataToken(start.z));
            request.SetValue("gx", new DataToken(goal.x));
            request.SetValue("gy", new DataToken(goal.y));
            request.SetValue("gz", new DataToken(goal.z));
            request.SetValue("rx", new DataToken(receiver));
            request.SetValue("found", new DataToken(foundEvent));
            request.SetValue("failed", new DataToken(failedEvent));
            requestQueue.Add(new DataToken(request));

            ProcessQueue();
        }

        // ===== Flow field (panekit fork) =====
        // Floods the whole walkable grid from sourcePos. On completion flowFieldReady
        // is true and GetFlowDataTexture() returns the wavefront buffer whose .z channel
        // holds, for every reached leaf, the parent leaf index one step toward sourcePos.
        // The receiver's doneEvent must check flowFieldReady (false on failure).
        public void RequestFlowField(Vector3 sourcePos, UdonSharpBehaviour receiver, string doneEvent)
        {
            if (leafCount == 0)
            {
                pathFound = false;
                pathError = "No walkable voxels";
                flowFieldReady = false;
                Notify(receiver, doneEvent);
                return;
            }

            DataDictionary request = new DataDictionary();
            request.SetValue("sx", new DataToken(sourcePos.x));
            request.SetValue("sy", new DataToken(sourcePos.y));
            request.SetValue("sz", new DataToken(sourcePos.z));
            request.SetValue("gx", new DataToken(sourcePos.x));
            request.SetValue("gy", new DataToken(sourcePos.y));
            request.SetValue("gz", new DataToken(sourcePos.z));
            request.SetValue("rx", new DataToken(receiver));
            request.SetValue("found", new DataToken(doneEvent));
            request.SetValue("failed", new DataToken(doneEvent));
            request.SetValue("flood", new DataToken(1));
            requestQueue.Add(new DataToken(request));

            ProcessQueue();
        }

        private Vector3 ReadVector3(DataDictionary dict, string xKey, string yKey, string zKey)
        {
            dict.TryGetValue(xKey, TokenType.Float, out DataToken tx);
            dict.TryGetValue(yKey, TokenType.Float, out DataToken ty);
            dict.TryGetValue(zKey, TokenType.Float, out DataToken tz);
            return new Vector3(tx.Float, ty.Float, tz.Float);
        }

        private UdonSharpBehaviour ReadReceiver(DataDictionary dict)
        {
            if (!dict.TryGetValue("rx", TokenType.Reference, out DataToken token)) return null;
            return (UdonSharpBehaviour)token.Reference;
        }

        private string ReadString(DataDictionary dict, string key)
        {
            if (!dict.TryGetValue(key, TokenType.String, out DataToken token)) return "";
            return token.String;
        }

        [RecursiveMethod]
        private void ProcessQueue()
        {
            if (isBusy || requestQueue.Count == 0) return;

            if (!requestQueue.TryGetValue(0, TokenType.DataDictionary, out DataToken token))
            {
                requestQueue.RemoveAt(0);
                ProcessQueue();
                return;
            }

            DataDictionary request = token.DataDictionary;
            requestQueue.RemoveAt(0);

            requestStart = ReadVector3(request, "sx", "sy", "sz");
            requestGoal = ReadVector3(request, "gx", "gy", "gz");
            requestReceiver = ReadReceiver(request);
            requestFoundEvent = ReadString(request, "found");
            requestFailedEvent = ReadString(request, "failed");
            requestFlood = false;
            if (request.TryGetValue("flood", TokenType.Int, out DataToken floodToken))
            {
                requestFlood = floodToken.Int != 0;
            }
            StartPathSearch();
        }
        [RecursiveMethod]
        private void StartPathSearch()
        {
            startWorld = requestStart;
            goalWorld = requestGoal;

            startLeafIdx = FindNearestEmptyLeaf(requestStart);

            if (requestFlood)
            {
                if (startLeafIdx < 0)
                {
                    pathFound = false;
                    pathError = "Flow field source is not reachable";
                    flowFieldReady = false;
                    Notify(requestReceiver, requestFailedEvent);
                    ProcessQueue();
                    return;
                }

                goalLeafIdx = -1;
                floodTargetIterations = gridSizeX + gridSizeY + gridSizeZ;

                ResetGpu();

                currentIteration = 0;
                readA = true;
                state = STATE_RUNNING;
                isBusy = true;
                pathFound = false;
                pathError = "";
                flowFieldReady = false;
                searchIterationCount = 0;
                searchFrameCount = 0;
                return;
            }

            goalLeafIdx = FindNearestEmptyLeaf(requestGoal);

            if (startLeafIdx < 0 || goalLeafIdx < 0)
            {
                pathFound = false;
                pathError = "Start or goal is not reachable";
                Notify(requestReceiver, requestFailedEvent);
                ProcessQueue();
                return;
            }

            ResetGpu();

            currentIteration = 0;
            readA = true;
            state = STATE_RUNNING;
            isBusy = true;
            pathFound = false;
            pathError = "";
            searchIterationCount = 0;
            searchFrameCount = 0;
        }

        private void ResetGpu()
        {
            pathfindMaterial.SetInt("_StartIndex", startLeafIdx);
            pathfindMaterial.SetInt("_NodeCount", leafCount);
            pathfindMaterial.SetInt("_LeafTexWidth", texWidth);
            Compeito.Dispatch(pathfindMaterial, kernelReset, pathDataA);
            Compeito.Dispatch(pathfindMaterial, kernelReset, pathDataB);
        }

        private void Update()
        {
            if (state != STATE_IDLE)
            {
                searchFrameCount++;
            }

            if (state == STATE_RUNNING)
            {
                Tick();
            }
        }

        private void Tick()
        {
            int iterationCap = requestFlood ? floodTargetIterations : maxIterations;
            int remaining = iterationCap - currentIteration;
            int batch = Mathf.Min(itersPerFrame, remaining);

            if (batch <= 0)
            {
                if (requestFlood)
                {
                    CompleteFlood();
                }
                else
                {
                    FailSearch("Max iterations reached");
                }
                return;
            }

            pathfindMaterial.SetInt("_NodeCount", leafCount);
            pathfindMaterial.SetInt("_GoalIndex", goalLeafIdx);
            pathfindMaterial.SetInt("_LeafTexWidth", texWidth);

            searchIterationCount += batch;

            for (int i = 0; i < batch; i++)
            {
                RenderTexture readBuf = readA ? pathDataA : pathDataB;
                RenderTexture writeBuf = readA ? pathDataB : pathDataA;

                pathfindMaterial.SetTexture("_PathDataIn", readBuf);
                Compeito.Dispatch(pathfindMaterial, kernelWavefront, writeBuf);

                readA = !readA;
                currentIteration++;
            }

            if (requestFlood)
            {
                if (currentIteration >= floodTargetIterations)
                {
                    CompleteFlood();
                }
                return;
            }

            RenderTexture currentBuf = readA ? pathDataA : pathDataB;
            pathfindMaterial.SetTexture("_PathDataIn", currentBuf);
            pathfindMaterial.SetInt("_GoalIndex", goalLeafIdx);
            pathfindMaterial.SetInt("_NodeCount", leafCount);
            Compeito.Dispatch(pathfindMaterial, kernelGoalDetect, goalResultTex);

            VRCAsyncGPUReadback.Request(goalResultTex, 0, (IUdonEventReceiver)this);
            state = STATE_GOAL_PENDING;
        }

        private void CompleteFlood()
        {
            flowFieldReady = true;
            flowFieldSourceLeaf = startLeafIdx;
            isBusy = false;
            state = STATE_IDLE;
            pathFound = true;
            pathError = "";

            Notify(requestReceiver, requestFoundEvent);
            ProcessQueue();
        }

        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                FailSearch("GPU readback error");
                return;
            }

            if (state == STATE_GOAL_PENDING)
            {
                request.TryGetData(goalResultBuffer);
                bool goalReached = goalResultBuffer[0].r > 0.5f;

                if (goalReached)
                {
                    RenderTexture currentBuf = readA ? pathDataA : pathDataB;
                    VRCAsyncGPUReadback.Request(currentBuf, 0, (IUdonEventReceiver)this);
                    state = STATE_PATH_PENDING;
                }
                else if (currentIteration >= maxIterations)
                {
                    FailSearch("No path found");
                }
                else
                {
                    state = STATE_RUNNING;
                }
            }
            else if (state == STATE_PATH_PENDING)
            {
                request.TryGetData(pathDataBuffer);
                ProcessPath();
            }
        }

        private void ProcessPath()
        {
            int pathLength = 0;
            int current = goalLeafIdx;

            while (current >= 0 && current < leafCount && pathLength < leafCount)
            {
                reconstructBuffer[pathLength] = current;
                pathLength++;

                if (current == startLeafIdx) break;

                int parent = (int)pathDataBuffer[current].b;
                if (parent < 0 || parent == current) break;
                current = parent;
            }

            if (pathLength == 0 || reconstructBuffer[pathLength - 1] != startLeafIdx)
            {
                FailSearch("Path reconstruction failed");
                return;
            }

            Vector3[] rawWaypoints = new Vector3[pathLength];
            for (int i = 0; i < pathLength; i++)
            {
                int leafIdx = reconstructBuffer[pathLength - 1 - i];
                rawWaypoints[i] = LeafToWorld(leafIdx, true);
            }

            waypoints = SmoothPath(rawWaypoints);

            isBusy = false;
            state = STATE_IDLE;
            pathFound = true;
            pathError = "";

            Debug.Log(string.Format("[UdonPathfindingManager] Path found: iterations={0}, frames={1}, waypoints={2}",
                searchIterationCount, searchFrameCount, waypoints != null ? waypoints.Length : 0));

            Notify(requestReceiver, requestFoundEvent);
            ProcessQueue();
        }

        private Vector3[] SmoothPath(Vector3[] rawPath)
        {
            if (rawPath == null || rawPath.Length == 0) return new Vector3[0];
            if (rawPath.Length == 1) return new Vector3[] { rawPath[0] };

            int wallLayerMask = this.wallLayerMask;
            float radius = cellSize * 0.25f;

            int smoothCount = 1;
            smoothBuffer[0] = rawPath[0];

            int current = 0;
            for (int i = 1; i < rawPath.Length; i++)
            {
                Vector3 from = smoothBuffer[smoothCount - 1];
                Vector3 to = rawPath[i];
                Vector3 dir = to - from;
                float dist = dir.magnitude;

                if (dist < 0.001f) continue;

                if (Physics.SphereCast(from, radius, dir.normalized, out RaycastHit hit, dist, wallLayerMask))
                {
                    smoothBuffer[smoothCount] = rawPath[i - 1];
                    smoothCount++;
                    current = i - 1;
                }
            }

            smoothBuffer[smoothCount] = rawPath[rawPath.Length - 1];
            smoothCount++;

            Vector3[] result = new Vector3[smoothCount];
            for (int i = 0; i < smoothCount; i++) result[i] = smoothBuffer[i];
            return result;
        }

        private void FailSearch(string error)
        {
            isBusy = false;
            state = STATE_IDLE;
            pathFound = false;
            pathError = error;
            waypoints = new Vector3[0];

            Debug.LogWarning(string.Format("[UdonPathfindingManager] Path failed: {0} (iterations={1}, frames={2})",
                error, searchIterationCount, searchFrameCount));

            Notify(requestReceiver, requestFailedEvent);
            ProcessQueue();
        }

        private void Notify(UdonSharpBehaviour receiver, string eventName)
        {
            if (receiver != null && !string.IsNullOrEmpty(eventName))
            {
                receiver.SendCustomEvent(eventName);
            }
        }

        public int WorldToLeaf(Vector3 worldPos)
        {
            if (voxelToLeaf == null) return -1;

            int gsX = gridSizeX;
            int gsY = gridSizeY;
            int gsZ = gridSizeZ;

            int x = Mathf.RoundToInt((worldPos.x - gridOrigin.x) / cellSize);
            int y = Mathf.RoundToInt((worldPos.y - gridOrigin.y) / cellSize);
            int z = Mathf.RoundToInt((worldPos.z - gridOrigin.z) / cellSize);

            if (x < 0 || x >= gsX || y < 0 || y >= gsY || z < 0 || z >= gsZ) return -1;

            int vi = x + y * gsX + z * gsX * gsY;
            return voxelToLeaf[vi];
        }

        public Vector3 LeafToWorld(int leafIdx, bool center)
        {
            if (leafIdx < 0 || leafIdx >= leafCount) return Vector3.zero;

            int gsX = gridSizeX;
            int gsY = gridSizeY;

            int vi = leafVoxelIndex[leafIdx];
            int x = vi % gsX;
            int y = (vi / gsX) % gsY;
            int z = vi / (gsX * gsY);

            Vector3 basePos = gridOrigin + new Vector3(x, y, z) * cellSize;

            if (center)
            {
                basePos += new Vector3(cellSize, cellSize, cellSize) * 0.5f;
            }

            return basePos;
        }

        public int FindNearestEmptyLeaf(Vector3 worldPos)
        {
            int idx = WorldToLeaf(worldPos);
            if (idx >= 0) return idx;

            int gsX = gridSizeX;
            int gsY = gridSizeY;
            int gsZ = gridSizeZ;

            int cx = Mathf.RoundToInt((worldPos.x - gridOrigin.x) / cellSize);
            int cy = Mathf.RoundToInt((worldPos.y - gridOrigin.y) / cellSize);
            int cz = Mathf.RoundToInt((worldPos.z - gridOrigin.z) / cellSize);

            int searchRange = Mathf.Max(gsX, Mathf.Max(gsY, gsZ));
            for (int r = 1; r <= searchRange; r++)
            {
                for (int z = cz - r; z <= cz + r; z++)
                    for (int y = cy - r; y <= cy + r; y++)
                        for (int x = cx - r; x <= cx + r; x++)
                        {
                            if (x < 0 || x >= gsX || y < 0 || y >= gsY || z < 0 || z >= gsZ) continue;
                            if (Mathf.Abs(x - cx) != r && Mathf.Abs(y - cy) != r && Mathf.Abs(z - cz) != r) continue;
                            int vi = x + y * gsX + z * gsX * gsY;
                            int li = voxelToLeaf[vi];
                            if (li >= 0) return li;
                        }
            }

            return -1;
        }

        private void DisposeGpuResources()
        {
            if (neighborTex != null)
            {
                DestroyImmediate(neighborTex);
                neighborTex = null;
            }
            if (costTex != null)
            {
                DestroyImmediate(costTex);
                costTex = null;
            }
            if (pathDataA != null)
            {
                pathDataA.Release();
                DestroyImmediate(pathDataA);
                pathDataA = null;
            }
            if (pathDataB != null)
            {
                pathDataB.Release();
                DestroyImmediate(pathDataB);
                pathDataB = null;
            }
            if (goalResultTex != null)
            {
                goalResultTex.Release();
                DestroyImmediate(goalResultTex);
                goalResultTex = null;
            }
        }

        public byte[] GetGrid() { return grid; }

        // ===== Flow field accessors (panekit fork) =====
        // The returned texture is the last-written wavefront buffer. Its .z channel
        // holds the parent leaf index for every leaf reached by the flood; following
        // parents walks one step per hop toward the flood source. Leaf-indexed layout:
        // texel (idx % GetFlowTexWidth(), idx / GetFlowTexWidth()) for leaf idx.
        public RenderTexture GetFlowDataTexture()
        {
            return readA ? pathDataA : pathDataB;
        }

        public int GetFlowTexWidth() { return texWidth; }
        public int GetFlowTexHeight() { return texHeight; }
        public int GetGridSizeX() { return gridSizeX; }
        public int GetGridSizeY() { return gridSizeY; }
        public int GetGridSizeZ() { return gridSizeZ; }
        public float GetCellSize() { return cellSize; }
        public Vector3 GetGridOrigin() { return gridOrigin; }

        // ===== Path Utility API (stateless, take wps as argument) =====

        public float GetPathLength(Vector3[] wps)
        {
            if (wps == null || wps.Length < 2) return 0f;
            float total = 0f;
            for (int i = 0; i < wps.Length - 1; i++)
            {
                total += Vector3.Distance(wps[i], wps[i + 1]);
            }
            return total;
        }

        public Vector3 GetClosestPointOnPath(Vector3[] wps, Vector3 worldPos)
        {
            if (wps == null || wps.Length == 0) return worldPos;
            if (wps.Length == 1) return wps[0];

            float bestDist = 1e9f;
            Vector3 bestPoint = wps[0];
            for (int i = 0; i < wps.Length - 1; i++)
            {
                Vector3 cp = ClosestPointOnSegment(worldPos, wps[i], wps[i + 1]);
                float d = Vector3.Distance(worldPos, cp);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestPoint = cp;
                }
            }
            return bestPoint;
        }

        public int GetClosestWaypointIndex(Vector3[] wps, Vector3 worldPos)
        {
            if (wps == null || wps.Length == 0) return -1;
            float bestDist = 1e9f;
            int bestIdx = -1;
            for (int i = 0; i < wps.Length; i++)
            {
                float d = Vector3.Distance(worldPos, wps[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        public Vector3[] ResamplePath(Vector3[] wps, float spacing)
        {
            if (wps == null || wps.Length == 0) return new Vector3[0];
            int n = wps.Length;
            if (n == 1) return new Vector3[] { wps[0] };

            if (spacing <= 0f) spacing = cellSize;
            if (spacing < 0.0001f) spacing = 0.0001f;

            float totalLen = GetPathLength(wps);
            if (totalLen < 0.0001f) return new Vector3[] { wps[0] };

            int maxCount = Mathf.FloorToInt(totalLen / spacing) + 2;
            Vector3[] tmp = new Vector3[maxCount];
            int ri = 0;
            tmp[ri] = wps[0];
            ri++;

            float targetDist = spacing;
            float traveled = 0f;
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 a = wps[i];
                Vector3 b = wps[i + 1];
                float segLen = Vector3.Distance(a, b);
                if (segLen < 0.0001f) continue;

                float segEnd = traveled + segLen;
                while (targetDist <= segEnd - 0.0001f && ri < maxCount)
                {
                    float localT = (targetDist - traveled) / segLen;
                    if (localT < 0f) localT = 0f;
                    if (localT > 1f) localT = 1f;
                    tmp[ri] = a + (b - a) * localT;
                    ri++;
                    targetDist += spacing;
                }
                traveled = segEnd;
            }

            Vector3 endP = wps[n - 1];
            if (ri == 0 || Vector3.Distance(tmp[ri - 1], endP) > spacing * 0.01f)
            {
                if (ri < maxCount)
                {
                    tmp[ri] = endP;
                    ri++;
                }
            }

            Vector3[] result = new Vector3[ri];
            for (int i = 0; i < ri; i++) result[i] = tmp[i];
            return result;
        }

        // ===== Waypoint progress helpers (stateless, take index as argument) =====

        public Vector3 GetCurrentWaypoint(Vector3[] wps, int index)
        {
            if (wps == null || wps.Length == 0) return Vector3.zero;
            if (index < 0 || index >= wps.Length) return Vector3.zero;
            return wps[index];
        }

        public float GetRemainingDistance(Vector3[] wps, int index)
        {
            if (wps == null || wps.Length < 2) return 0f;
            if (index < 0 || index >= wps.Length) return 0f;
            float total = 0f;
            for (int i = index; i < wps.Length - 1; i++)
            {
                total += Vector3.Distance(wps[i], wps[i + 1]);
            }
            return total;
        }

        public float GetProgress(Vector3[] wps, int index)
        {
            if (wps == null || wps.Length < 2) return 0f;
            float total = GetPathLength(wps);
            if (total < 0.0001f) return 0f;
            float remaining = GetRemainingDistance(wps, index);
            float traversed = total - remaining;
            return Mathf.Clamp01(traversed / total);
        }

        public int AdvanceWaypoint(int index, int waypointCount)
        {
            if (waypointCount <= 0) return -1;
            if (index < 0 || index >= waypointCount - 1) return -1;
            return index + 1;
        }

        public int ResetWaypointProgress(int waypointCount)
        {
            if (waypointCount <= 0) return 0;
            return (waypointCount > 1) ? 1 : 0;
        }

        // ===== Follow target (snap to nearest point, then advance along path) =====

        public Vector3 GetFollowTarget(Vector3[] wps, Vector3 currentPos)
        {
            if (wps == null || wps.Length == 0) return currentPos;

            float snapDist = cellSize * 0.5f;
            float reachDist = cellSize * 0.5f;
            int last = wps.Length - 1;

            // Snap phase: move toward closest point on path
            Vector3 cp = GetClosestPointOnPath(wps, currentPos);
            if (Vector3.Distance(currentPos, cp) > snapDist)
            {
                return cp;
            }

            // On path: determine forward direction from closest segment
            int segStart = GetClosestSegmentIndex(wps, currentPos);
            int forwardIdx = segStart + 1;
            if (forwardIdx > last) forwardIdx = last;

            // If we've reached the forward waypoint, advance to the next
            if (Vector3.Distance(currentPos, wps[forwardIdx]) <= reachDist)
            {
                if (forwardIdx >= last) return wps[last];
                return wps[forwardIdx + 1];
            }

            return wps[forwardIdx];
        }

        public Vector3 GetFollowTarget(Vector3[] wps, Vector3 currentPos, out bool reachedGoal)
        {
            Vector3 nextWayPoint = GetFollowTarget(wps, currentPos);

            float distToGoal = Vector3.Distance(currentPos, wps[wps.Length - 1]);
            if (distToGoal < GetCellSize() * 0.5f)
            {
                reachedGoal = true;
            }
            else
            {
                reachedGoal = false;
            }
            return nextWayPoint;
        }

        public Vector3 GetFollowTarget(
            Vector3[] wps, Vector3 currentPos,
            int currentIndex, float snapDistance, float reachDistance,
            out int nextIndex, out bool reachedGoal)
        {
            nextIndex = -1;
            reachedGoal = false;

            if (wps == null || wps.Length == 0) return currentPos;

            int last = wps.Length - 1;

            if (currentIndex < 0)
            {
                // Snap phase: move toward closest point on path
                Vector3 cp = GetClosestPointOnPath(wps, currentPos);
                float snapDist = Vector3.Distance(currentPos, cp);
                if (snapDist > snapDistance)
                {
                    nextIndex = -1;
                    return cp;
                }
                // Snapped: switch to forward waypoint of the closest segment
                int segStart = GetClosestSegmentIndex(wps, currentPos);
                int forwardIdx = segStart + 1;
                if (forwardIdx > last) forwardIdx = last;
                nextIndex = forwardIdx;
                return wps[forwardIdx];
            }

            // Follow phase
            if (currentIndex >= wps.Length) currentIndex = last;

            Vector3 wp = wps[currentIndex];
            float d = Vector3.Distance(currentPos, wp);
            if (d > reachDistance)
            {
                nextIndex = currentIndex;
                return wp;
            }

            // Reached current waypoint
            if (currentIndex >= last)
            {
                reachedGoal = true;
                nextIndex = -1;
                return wp;
            }

            int nxt = currentIndex + 1;
            nextIndex = nxt;
            return wps[nxt];
        }

        private int GetClosestSegmentIndex(Vector3[] wps, Vector3 pos)
        {
            if (wps == null || wps.Length == 0) return 0;
            if (wps.Length == 1) return 0;

            float bestDist = 1e9f;
            int bestIdx = 0;
            for (int i = 0; i < wps.Length - 1; i++)
            {
                Vector3 cp = ClosestPointOnSegment(pos, wps[i], wps[i + 1]);
                float d = Vector3.Distance(pos, cp);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        private Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float denom = Vector3.Dot(ab, ab);
            if (denom < 0.0001f) return a;
            float t = Vector3.Dot(p - a, ab) / denom;
            t = Mathf.Clamp01(t);
            return a + ab * t;
        }

        public void OnDestroy()
        {
            DisposeGpuResources();
        }
    }
}