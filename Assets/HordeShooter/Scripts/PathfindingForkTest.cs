using UdonSharp;
using UnityEngine;
using nyakomake.udon3dpathfinding;
using VRC.SDK3.Rendering;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// M0 acceptance test for the panekit fork of Udon-3D-Pathfinding.
/// Verifies both the stock RequestPath API and the new RequestFlowField flood mode.
/// Flow validation: reads back the flow texture once and walks the parent chain from
/// a far probe leaf; the chain must reach the flood source leaf.
/// </summary>
// M0 acceptance harness — panekit horde shooter project.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PathfindingForkTest : UdonSharpBehaviour
{
    public UdonPathfindingManager manager;
    public Transform pathStart;
    public Transform pathGoal;
    public Transform flowSource;
    public Transform flowProbe;

    private bool _flowValidated;

    void Start()
    {
        // The manager rebuilds its grid in its own Start(); defer until that is done.
        SendCustomEventDelayedSeconds(nameof(DoRequests), 1f);
    }

    public void DoRequests()
    {
        if (manager == null)
        {
            Debug.LogError("[ForkTest] manager is null");
            return;
        }
        if (manager.leafCount <= 0)
        {
            Debug.LogError("[ForkTest] manager grid empty (leafCount=0) — Rebuild did not run?");
            return;
        }
        Debug.Log("[ForkTest] grid ready: leafCount=" + manager.leafCount
            + " grid=" + manager.gridSizeX + "x" + manager.gridSizeY + "x" + manager.gridSizeZ);

        manager.RequestPath(pathStart.position, pathGoal.position, this, "OnTestPathFound", "OnTestPathFailed");
        manager.RequestFlowField(flowSource.position, this, "OnTestFlowReady");
    }

    public void OnTestPathFound()
    {
        Debug.Log("[ForkTest] RequestPath OK: waypoints=" + manager.waypoints.Length
            + " iterations=" + manager.searchIterationCount
            + " frames=" + manager.searchFrameCount);
    }

    public void OnTestPathFailed()
    {
        Debug.LogError("[ForkTest] RequestPath FAILED: " + manager.pathError);
    }

    public void OnTestFlowReady()
    {
        if (!manager.flowFieldReady)
        {
            Debug.LogError("[ForkTest] RequestFlowField FAILED: " + manager.pathError);
            return;
        }
        Debug.Log("[ForkTest] RequestFlowField OK: sourceLeaf=" + manager.flowFieldSourceLeaf
            + " texW=" + manager.GetFlowTexWidth()
            + " texH=" + manager.GetFlowTexHeight()
            + " frames=" + manager.searchFrameCount);

        // One async readback of the flow texture, then walk the parent chain on CPU.
        _flowValidated = false;
        VRCAsyncGPUReadback.Request(manager.GetFlowDataTexture(), 0, (IUdonEventReceiver)this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        if (_flowValidated) return;

        if (request.hasError)
        {
            Debug.LogError("[ForkTest] flow readback hasError");
            return;
        }

        int w = manager.GetFlowTexWidth();
        int h = manager.GetFlowTexHeight();
        Color[] data = new Color[w * h];
        if (!request.TryGetData(data))
        {
            Debug.LogError("[ForkTest] flow TryGetData failed");
            return;
        }
        _flowValidated = true;

        int probeLeaf = manager.WorldToLeaf(flowProbe.position);
        if (probeLeaf < 0)
        {
            Debug.LogError("[ForkTest] probe position is not on the grid");
            return;
        }

        int source = manager.flowFieldSourceLeaf;
        int current = probeLeaf;
        int hops = 0;
        while (hops < manager.leafCount)
        {
            hops++;
            int parent = (int)data[current].b;
            if (parent < 0 || parent == current) break;
            if (parent == source)
            {
                Debug.Log("[ForkTest] Flow chain OK: probeLeaf=" + probeLeaf
                    + " -> sourceLeaf=" + source + " in " + hops + " hops");
                return;
            }
            current = parent;
        }
        Debug.LogError("[ForkTest] Flow chain BROKEN after " + hops + " hops (probeLeaf=" + probeLeaf + ")");
    }
}
