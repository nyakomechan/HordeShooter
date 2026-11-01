using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Real-client test helper: paced authority-path spawner.
/// Interact = start spawning spawnCount enemies (≤16 per 0.4s, ring-safe).
/// autoSpawnOnJoin = owner spawns once when the first player joins.
/// Reset cube: local-only clear on the interacting client (no ring events —
/// the other client keeps its own horde; use for single-client load runs).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class HordeDebugSpawner : UdonSharpBehaviour
{
    public HordeNet hordeNet;
    public HordeSim hordeSim;
    public int spawnCount = 16;
    public bool autoSpawnOnJoin;
    public bool resetOnInteract;

    private int _remaining;
    private int _nextSlot;
    private int _nextSpawnIdx;
    private bool _spawnedOnce;

    public override void Interact()
    {
        if (resetOnInteract)
        {
            if (hordeSim != null) hordeSim._DebugReset();
            return;
        }
        _remaining = spawnCount;
        SendCustomEvent(nameof(_SpawnChunk));
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (!autoSpawnOnJoin || _spawnedOnce) return;
        if (hordeNet == null) return;
        if (!Networking.IsOwner(hordeNet.gameObject)) return;
        _spawnedOnce = true;
        _remaining = spawnCount;
        SendCustomEvent(nameof(_SpawnChunk));
    }

    public void _SpawnChunk()
    {
        if (hordeNet == null || _remaining <= 0) return;
        int n = _remaining;
        if (n > 16) n = 16;
        for (int i = 0; i < n; i++)
        {
            hordeNet.AuthoritySpawn(_nextSlot, _nextSpawnIdx, 0);
            _nextSlot = (_nextSlot + 1) % 512;
            _nextSpawnIdx = (_nextSpawnIdx + 1) & 7;
        }
        _remaining -= n;
        if (_remaining > 0)
        {
            SendCustomEventDelayedSeconds(nameof(_SpawnChunk), 0.4f);
        }
    }
}
