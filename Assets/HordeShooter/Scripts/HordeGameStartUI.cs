using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// Game-start UI panel (M7f): a uGUI Button on a world-space canvas in the shop
/// room. Shows the game owner's player name next to the button and only the
/// owner's clicks start the game. The panel is visible in the lobby (phase 0)
/// and hidden once a wave is running.
/// The Button.onClick is bound (editor-side) to SendCustomEvent("_TryStart").
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class HordeGameStartUI : UdonSharpBehaviour
{
    public HordeNet hordeNet;
    public Text ownerLabel;      // "START: <owner name>" next to the button
    public GameObject panelRoot; // toggled by the game phase (lobby only)

    private float _labelTimer;
    private int _lastPhase = -1;

    void Update()
    {
        if (hordeNet == null) return;
        int phase = hordeNet.gamePhase;
        if (phase != _lastPhase)
        {
            _lastPhase = phase;
            if (panelRoot != null && panelRoot.activeSelf != (phase == 0))
                panelRoot.SetActive(phase == 0);
        }

        // owner can change (master leave) — refresh the name at 2Hz
        _labelTimer += Time.deltaTime;
        if (_labelTimer < 0.5f) return;
        _labelTimer = 0f;
        if (ownerLabel == null) return;
        VRCPlayerApi owner = Networking.GetOwner(hordeNet.gameObject);
        string name = (owner != null && owner.IsValid()) ? owner.displayName : "?";
        ownerLabel.text = "START: " + name;
    }

    /// <summary>uGUI Button entry point (owner only).</summary>
    public void _TryStart()
    {
        if (hordeNet == null) return;
        if (!Networking.IsOwner(hordeNet.gameObject)) return;   // owner only
        hordeNet._StartWave();
    }
}
