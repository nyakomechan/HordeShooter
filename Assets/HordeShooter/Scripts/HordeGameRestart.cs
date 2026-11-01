using UdonSharp;
using UnityEngine;

/// <summary>Interact cube: owner restarts the game (from game over or mid-game).</summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class HordeGameRestart : UdonSharpBehaviour
{
    public HordeNet hordeNet;

    public override void Interact()
    {
        if (hordeNet != null) hordeNet._RestartGame();
    }
}
