using UnityEngine;

/// <summary>
/// HordeNet partial: remote application of synced state.
/// Events drive kill/spawn commands; the proximity snapshot feeds a correction
/// queue drained 24 entries per frame into the sim's correction texture
/// (soft-pull 0.35 lerp, snap beyond 5 m — implemented GPU-side in SimPos).
/// </summary>
public partial class HordeNet
{
    private int _appliedHead;
    private bool _hasAppliedHead;
    private int _appliedSnapRevision;
    private bool _hasAppliedSnap;
    private bool _hasLoggedSnap;
    private int _appliedClearRevision;
    private bool _hasAppliedClear;
    private int _appliedWaveEnd;
    private bool _hasAppliedWaveEnd;

    public override void OnDeserialization()
    {
        ApplySyncedState();
    }

    private void ApplySyncedState()
    {
        if (hordeSim == null) return;
        if (_IsAuthority()) return;   // the owner applies its own writes directly

        // Event ring: walk from the last applied head to the synced head.
        if (!_hasAppliedHead)
        {
            _appliedHead = eventHead;
            _hasAppliedHead = true;
        }
        else if (eventHead != _appliedHead)
        {
            int steps = (eventHead - _appliedHead + RING_EVENTS) % RING_EVENTS;
            if (steps > RING_EVENTS / 2) steps = RING_EVENTS / 2;   // overflow guard
            for (int n = 0; n < steps; n++)
            {
                int w = ((_appliedHead + n) % RING_EVENTS) * 4;
                int type = eventRing[w];
                if (type == EV_NONE) continue;
                int slot = eventRing[w + 1];
                int p0 = eventRing[w + 2];
                int p1 = eventRing[w + 3];
                if (type == EV_KILL)
                {
                    // Snap the corpse to where the shooter saw it, then kill.
                    // p0 = x16|z16, p1 = shooterId16|y16 (quantized 1/64 m, ±512 m).
                    int qx = (p0 >> 16) & 0xFFFF;
                    int qz = p0 & 0xFFFF;
                    int qy = p1 & 0xFFFF;
                    _EnqueueCorrection(slot, qx, qy, qz);
                    hordeSim.QueueKill(slot);
                }
                else if (type == EV_SPAWN)
                {
                    hordeSim.QueueSpawn(slot, p0, p1);
                }
            }
            _appliedHead = eventHead;
            if (debugNetLog) Debug.Log("[HordeNet] applied " + steps + " ring events (head=" + eventHead + ")");
        }

        // Proximity snapshot -> correction queue.
        if (!_hasAppliedSnap)
        {
            _appliedSnapRevision = snapRevision;
            _hasAppliedSnap = true;
        }
        else if (snapRevision != _appliedSnapRevision)
        {
            _appliedSnapRevision = snapRevision;
            if (!_hasLoggedSnap)
            {
                _hasLoggedSnap = true;
                if (debugNetLog) Debug.Log("[HordeNet] first proximity snapshot applied (rev=" + snapRevision + ")");
            }
            for (int k = 0; k < snapshotK; k++)
            {
                int slot = snapSlots[k];
                if (slot < 0) continue;
                int qx = (snapData[k * 3] >> 16) & 0xFFFF;
                int qz = snapData[k * 3] & 0xFFFF;
                int qy = snapData[k * 3 + 1] & 0xFFFF;
                _EnqueueCorrection(slot, qx, qy, qz);
            }
        }

        // Game restart: the owner bumped clearRevision — clear the local sim too.
        if (!_hasAppliedClear)
        {
            _appliedClearRevision = clearRevision;
            _hasAppliedClear = true;
        }
        else if (clearRevision != _appliedClearRevision)
        {
            _appliedClearRevision = clearRevision;
            if (hordeSim != null) hordeSim._DebugReset();
        }

        // Wave end: the owner bumped waveEndRevision — despawn all remaining
        // enemies locally (same instant despawn the owner ran).
        if (!_hasAppliedWaveEnd)
        {
            _appliedWaveEnd = waveEndRevision;
            _hasAppliedWaveEnd = true;
        }
        else if (waveEndRevision != _appliedWaveEnd)
        {
            _appliedWaveEnd = waveEndRevision;
            if (hordeSim != null) hordeSim.DespawnAll();
        }

        // Late joiner catch-up moved to HordeNetCatchup (serialized on join only);
        // it calls back into HordeNet.ApplyJoinSnapshot.
    }

    private void _EnqueueCorrection(int slot, int qx, int qy, int qz)
    {
        _EnqueueCorrection(slot, qx, qy, qz, 0);
    }

    /// <summary>Queue entry: [slot, qx, qy, qz, wakeState, variant]. wakeState > 0 activates an inactive slot.</summary>
    private void _EnqueueCorrection(int slot, int qx, int qy, int qz, int wakeState)
    {
        if (_corrQueueCount + 6 > _corrQueue.Length) return;
        _corrQueue[_corrQueueCount] = slot;
        _corrQueue[_corrQueueCount + 1] = qx;
        _corrQueue[_corrQueueCount + 2] = qy;
        _corrQueue[_corrQueueCount + 3] = qz;
        _corrQueue[_corrQueueCount + 4] = wakeState;
        _corrQueue[_corrQueueCount + 5] = 0;
        _corrQueueCount += 6;
    }

    /// <summary>Drains up to 24 correction entries per frame into the sim.</summary>
    public void DrainCorrectionQueue()
    {
        if (_corrQueueCount == 0 || hordeSim == null) return;
        hordeSim.BeginCorrections();
        int n = 0;
        while (_corrQueueCount > 0 && n < 24)
        {
            int slot = _corrQueue[0];
            float x = (float)_corrQueue[1] / 64f - 512f;
            float y = (float)_corrQueue[2] / 64f - 512f;
            float z = (float)_corrQueue[3] / 64f - 512f;
            int wakeState = _corrQueue[4];
            if (wakeState > 0) hordeSim.QueueWake(slot, wakeState);
            hordeSim.PushCorrection(slot, new Vector3(x, y, z));
            // shift remaining
            for (int i = 6; i < _corrQueueCount; i++) _corrQueue[i - 6] = _corrQueue[i];
            _corrQueueCount -= 6;
            n++;
        }
        hordeSim.CommitCorrections();
    }
}
