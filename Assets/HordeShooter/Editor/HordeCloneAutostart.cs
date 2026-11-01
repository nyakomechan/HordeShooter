using UnityEditor;
using ParrelSync;

/// <summary>
/// Launch helper for the MultiSim verification: when the ParrelSync clone editor
/// is started with -executeMethod HordeCloneAutostart.Boot, it enters Play mode
/// automatically (the MultiSim client then connects to the host on port 24685).
/// Guarded by IsClone() so the original editor is never affected.
/// </summary>
public static class HordeCloneAutostart
{
    public static void Boot()
    {
        if (!ClonesManager.IsClone()) return;
        EditorApplication.delayCall += () =>
        {
            EditorApplication.isPlaying = true;
        };
    }
}
