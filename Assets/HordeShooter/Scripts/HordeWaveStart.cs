using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>Interact cube: owner starts wave 1 from the lobby (owner only).</summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class HordeWaveStart : UdonSharpBehaviour
{
    public HordeNet hordeNet;

    public override void Interact()
    {
        if (hordeNet == null) return;
        if (!Networking.IsOwner(hordeNet.gameObject)) return;   // owner only
        hordeNet._StartWave();
    }
}
