using UdonSharp;
using UnityEngine;
using nyakomake.udon3dpathfinding;
using imaginantia.Compeito;
using VRC.SDKBase;

/// <summary>
/// Bridges the forked Udon-3D-Pathfinding GPU BFS into the horde sim:
/// uploads the static voxel/leaf mapping textures once, then periodically floods a
/// flow field from the "primary target" player and bakes it into a voxel-indexed
/// direction texture that EnemySim samples with one fetch per enemy.
/// All clients run this locally (the pathfinding manager is BehaviourSyncMode.None);
/// divergence is cosmetic and corrected by the proximity snapshot sync.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class FlowFieldBridge : UdonSharpBehaviour
{
    public UdonPathfindingManager pathfinder;
    public HordeSim hordeSim;
    public Material bakeMaterial;   // generated from FlowBake.compeito
    public float refreshInterval = 1.0f;
    public float refreshMoveThreshold = 1.5f;

    private RenderTexture _flowFieldRT;
    private Texture2D _voxelToLeafTex;
    private Texture2D _leafVoxelTex;
    private int _kBake = -1;
    private bool _uploaded;
    private bool _bakeReady;
    private bool _waitingForFlow;
    private float _timer;
    private Vector3 _lastSourcePos;

    void Start()
    {
        // The pathfinder rebuilds its grid in its own Start(); defer.
        SendCustomEventDelayedSeconds(nameof(_Init), 1.5f);
    }

    public void _Init()
    {
        if (pathfinder == null || hordeSim == null || bakeMaterial == null)
        {
            Debug.LogError("[FlowBridge] wiring missing (pathfinder / hordeSim / bakeMaterial)");
            return;
        }
        _kBake = bakeMaterial.FindPass("BakeFlow");
        if (_kBake < 0)
        {
            Debug.LogError("[FlowBridge] BakeFlow pass not found");
            return;
        }
        _UploadStaticTextures();
        _timer = refreshInterval;
    }

    private void _UploadStaticTextures()
    {
        int gsx = pathfinder.gridSizeX;
        int gsy = pathfinder.gridSizeY;
        int gsz = pathfinder.gridSizeZ;
        int w = gsx * gsz;
        int total = gsx * gsy * gsz;
        int[] v2l = pathfinder.voxelToLeaf;
        if (v2l == null || v2l.Length != total)
        {
            Debug.LogError("[FlowBridge] voxelToLeaf not ready (grid not rebuilt?)");
            return;
        }

        // Voxel-indexed texture: texel (x + z*gsx, y).
        _voxelToLeafTex = new Texture2D(w, gsy, TextureFormat.RGBAFloat, false);
        _voxelToLeafTex.filterMode = FilterMode.Point;
        Color[] colors = new Color[w * gsy];
        for (int vi = 0; vi < total; vi++)
        {
            int x = vi % gsx;
            int y = (vi / gsx) % gsy;
            int z = vi / (gsx * gsy);
            colors[y * w + (x + z * gsx)] = new Color((float)v2l[vi], 0f, 0f, 0f);
        }
        _voxelToLeafTex.SetPixels(colors);
        _voxelToLeafTex.Apply(false);

        // Leaf-indexed texture using the flow data texture layout.
        int[] l2v = pathfinder.leafVoxelIndex;
        int leafCount = pathfinder.leafCount;
        int fw = pathfinder.GetFlowTexWidth();
        int fh = pathfinder.GetFlowTexHeight();
        _leafVoxelTex = new Texture2D(fw, fh, TextureFormat.RGBAFloat, false);
        _leafVoxelTex.filterMode = FilterMode.Point;
        Color[] lc = new Color[fw * fh];
        for (int i = 0; i < lc.Length; i++) lc[i] = new Color(-1f, 0f, 0f, 0f);
        for (int li = 0; li < leafCount && li < lc.Length; li++)
        {
            int tx = li % fw;
            int ty = li / fw;
            lc[ty * fw + tx] = new Color((float)l2v[li], 0f, 0f, 0f);
        }
        _leafVoxelTex.SetPixels(lc);
        _leafVoxelTex.Apply(false);

        _flowFieldRT = Compeito.CreateRT("HordeFlowField", w, gsy);

        bakeMaterial.SetInt("_GSX", gsx);
        bakeMaterial.SetInt("_GSY", gsy);
        bakeMaterial.SetInt("_GSZ", gsz);
        bakeMaterial.SetInt("_LeafTexW", pathfinder.GetFlowTexWidth());
        bakeMaterial.SetTexture("_VoxelToLeaf", _voxelToLeafTex);
        bakeMaterial.SetTexture("_LeafVoxel", _leafVoxelTex);

        hordeSim.SetFlowGrid(gsx, gsy, gsz, pathfinder.gridOrigin, pathfinder.cellSize, _voxelToLeafTex);

        _uploaded = true;
        Debug.Log("[FlowBridge] static textures uploaded: voxels=" + total + " leaves=" + leafCount);
    }

    void Update()
    {
        if (!_uploaded) return;
        _timer += Time.deltaTime;
        if (_timer < refreshInterval || _waitingForFlow) return;

        Vector3 source = _GetFlowSource();
        if (_bakeReady)
        {
            Vector3 d = source - _lastSourcePos;
            if (d.sqrMagnitude < refreshMoveThreshold * refreshMoveThreshold) return;
        }

        _timer = 0f;
        _lastSourcePos = source;
        _waitingForFlow = true;
        pathfinder.RequestFlowField(source, this, "_OnFlowFieldReady");
    }

    /// <summary>Flow source: the active player nearest to the horde centroid.</summary>
    private Vector3 _GetFlowSource()
    {
        Vector3 centroid = Vector3.zero;
        int alive = 0;
        float[] sx = hordeSim.mirrorPosX;
        float[] sz = hordeSim.mirrorPosZ;
        float[] ss = hordeSim.mirrorState;
        if (sx != null)
        {
            int n = hordeSim.slotCount;
            for (int i = 0; i < n; i++)
            {
                int s = (int)ss[i];
                if (s < 1 || s > 4) continue;
                centroid.x += sx[i];
                centroid.z += sz[i];
                alive++;
            }
        }
        if (alive > 0) centroid = centroid / (float)alive;

        VRCPlayerApi[] players = VRCPlayerApi.GetPlayers(new VRCPlayerApi[16]);
        Vector3 best = _lastSourcePos;
        float bestD2 = 1e30f;
        for (int i = 0; i < players.Length; i++)
        {
            VRCPlayerApi p = players[i];
            if (p == null || !p.IsValid()) continue;
            Vector3 pos = p.GetPosition();
            Vector3 d = pos - centroid;
            float d2 = d.sqrMagnitude;
            if (d2 < bestD2) { bestD2 = d2; best = pos; }
        }
        return best;
    }

    public void _OnFlowFieldReady()
    {
        _waitingForFlow = false;
        if (!pathfinder.flowFieldReady) return;
        bakeMaterial.SetTexture("_FlowData", pathfinder.GetFlowDataTexture());
        Compeito.Dispatch(bakeMaterial, _kBake, _flowFieldRT);
        hordeSim.SetFlowField(_flowFieldRT);
        if (!_bakeReady)
        {
            _bakeReady = true;
            Debug.Log("[FlowBridge] flow field baked and bound to the sim");
        }
    }
}
