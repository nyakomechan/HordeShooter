using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// HordeSim partial: async GPU readback of the sim state into CPU mirrors.
/// Requests are chained (pos -> state -> hit results) so the shared readback
/// callback can tell them apart without extra bookkeeping.
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
}
