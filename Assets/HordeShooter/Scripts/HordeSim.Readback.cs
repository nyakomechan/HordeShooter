using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// HordeSim partial: async GPU readback of the sim state into CPU mirrors.
/// Requests are chained (pos -> state -> meta -> hit results) so the shared
/// readback callback can tell them apart without extra bookkeeping. The meta
/// pass also rebuilds the per-variant slot lists used by the M7d variant-batched
/// draws (unity_InstanceID -> slot mapping).
/// </summary>
public partial class HordeSim
{
    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        if (_mirrorAwaitPos)
        {
            _mirrorAwaitPos = false;
            if (!request.hasError && request.TryGetData(_posMirrorBuf))
            {
                for (int i = 0; i < slotCount; i++)
                {
                    mirrorPosX[i] = _posMirrorBuf[i].r;
                    mirrorPosY[i] = _posMirrorBuf[i].g;
                    mirrorPosZ[i] = _posMirrorBuf[i].b;
                    mirrorYaw[i] = _posMirrorBuf[i].a;
                }
            }
            else
            {
                Debug.Log("[HordeSim] pos readback FAILED: hasError=" + request.hasError);
            }
            _mirrorAwaitState = true;
            _awaitStart = Time.time;
            VRCAsyncGPUReadback.Request(_readA ? _stateA : _stateB, 0, (IUdonEventReceiver)this);
            return;
        }

        if (_mirrorAwaitState)
        {
            _mirrorAwaitState = false;
            if (!request.hasError && request.TryGetData(_stateMirrorBuf))
            {
                for (int i = 0; i < slotCount; i++)
                {
                    mirrorHp[i] = _stateMirrorBuf[i].r;
                    mirrorState[i] = _stateMirrorBuf[i].g;
                }
            }
            else
            {
                Debug.Log("[HordeSim] state readback FAILED: hasError=" + request.hasError);
            }
            _mirrorAwaitMeta = true;
            _awaitStart = Time.time;
            VRCAsyncGPUReadback.Request(_readA ? _metaA : _metaB, 0, (IUdonEventReceiver)this);
            return;
        }

        if (_mirrorAwaitMeta)
        {
            _mirrorAwaitMeta = false;
            if (!request.hasError && request.TryGetData(_metaMirrorBuf))
            {
                for (int i = 0; i < slotCount; i++)
                {
                    mirrorVariant[i] = _metaMirrorBuf[i].g;
                }
                _RebuildSlotLists();
            }
            else
            {
                Debug.Log("[HordeSim] meta readback FAILED: hasError=" + request.hasError);
            }
            if (_hitAwait)
            {
                _awaitStart = Time.time;
                VRCAsyncGPUReadback.Request(_hitResultRT, 0, (IUdonEventReceiver)this);
                return;
            }
        }

        if (_hitAwait)
        {
            _hitAwait = false;
            if (!request.hasError && request.TryGetData(_hitMirrorBuf))
            {
                for (int i = 0; i < MAX_RAYS; i++)
                {
                    hitRayDist[i] = _hitMirrorBuf[i].r;
                    hitRaySlot[i] = _hitMirrorBuf[i].g;
                }
                hitBatchId++;
                if (_hitListener != null)
                    _hitListener.SendCustomEvent(_hitListenerEvent);
            }
        }
    }

    /// <summary>
    /// Rebuilds the per-variant slot lists from the fresh mirror: variant v's
    /// list holds the slot indices of its alive slots; _variantCounts[v] is the
    /// instance count for that variant's DrawMeshInstanced call.
    /// </summary>
    private void _RebuildSlotLists()
    {
        for (int v = 0; v < 8; v++) _variantCounts[v] = 0;
        for (int v = 0; v < 8; v++)
        {
            Color[] cols = _slotListColors[v];
            for (int i = 0; i < slotCount; i++) cols[i] = new Color(0f, 0f, 0f, 0f);
        }
        for (int i = 0; i < slotCount; i++) _allSlotListColors[i] = new Color(0f, 0f, 0f, 0f);
        _allSlotCount = 0;
        for (int i = 0; i < slotCount; i++)
        {
            int s = (int)mirrorState[i];
            if (s < 1 || s > 4) continue;
            int v = (int)mirrorVariant[i];
            if (v < 0 || v > 7) v = 0;
            int n = _variantCounts[v];
            if (n >= slotCount) continue;
            _slotListColors[v][n] = new Color((float)i, 0f, 0f, 1f);
            _variantCounts[v] = n + 1;
            // FX list: every alive slot regardless of variant
            if (_allSlotCount < slotCount)
            {
                _allSlotListColors[_allSlotCount] = new Color((float)i, 0f, 0f, 1f);
                _allSlotCount++;
            }
        }
        for (int v = 0; v < 8; v++)
        {
            _slotListTex[v].SetPixels(_slotListColors[v]);
            _slotListTex[v].Apply(false);
        }
        _allSlotListTex.SetPixels(_allSlotListColors);
        _allSlotListTex.Apply(false);
    }
}
