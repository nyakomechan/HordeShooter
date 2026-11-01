using UdonSharp;
using UnityEngine;

/// <summary>Interact cube: owner starts wave 1 from the lobby.</summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class HordeWaveStart : UdonSharpBehaviour
{
    public HordeNet hordeNet;

    public override void Interact()
    {
        if (hordeNet != null) hordeNet._StartWave();
    }
}
