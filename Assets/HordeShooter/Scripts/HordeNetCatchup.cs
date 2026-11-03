using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Late-joiner catch-up snapshot, split out of HordeNet so the 6KB full-slot
/// array is only serialized when a player joins (HordeNet's 3Hz snapshot
/// serialization no longer carries it — measured 44.5KB per send before the split).
/// Lives on the same GameObject as HordeNet so ownership transfers together.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class HordeNetCatchup : UdonSharpBehaviour
{
    public HordeSim hordeSim;
    public HordeNet hordeNet;

    [UdonSynced] public int[] joinSnapshot = new int[1536];
    [UdonSynced] public int joinRevision;

    /// <summary>Owner: build the full-slot snapshot from the sim mirrors and send it.</summary>
    public void PackAndSend()
    {
        if (!Networking.IsOwner(gameObject)) return;
        if (hordeSim == null) return;
        float[] mx = hordeSim.mirrorPosX;
        float[] my = hordeSim.mirrorPosY;
        float[] mz = hordeSim.mirrorPosZ;
        float[] ms = hordeSim.mirrorState;
        float[] mh = hordeSim.mirrorYaw;
        float[] mhp = hordeSim.mirrorHp;
        float[] mv = hordeSim.mirrorVariant;
        if (mx == null) return;
        int slots = hordeSim.slotCount;
        if (slots > 512) slots = 512;
        for (int i = 0; i < slots; i++)
        {
            int qx = _Quant16(mx[i] + 512f);
            int qz = _Quant16(mz[i] + 512f);
            int qy = _Quant16(my[i] + 512f);
            int yaw8 = _Quant8((mh[i] / 6.28318f + 1f) * 0.5f * 255f);
            int hp8 = _Quant8(mhp[i] * 0.5f);
            int st8 = (int)ms[i] & 0xFF;
            int var8 = (mv != null) ? ((int)mv[i] & 0xFF) : 0;
            joinSnapshot[i * 3] = (qx << 16) | (qz & 0xFFFF);
            joinSnapshot[i * 3 + 1] = qy & 0xFFFF;
            // word2: yaw8 | hp8 | st8 | var8 (M7d: variant rides the free low byte)
            joinSnapshot[i * 3 + 2] = (yaw8 << 24) | (hp8 << 16) | (st8 << 8) | var8;
        }
        joinRevision++;
        RequestSerialization();
        Debug.Log("[HordeNetCatchup] packed join snapshot rev=" + joinRevision + " (512 slots)");
    }

    public override void OnDeserialization()
    {
        if (hordeNet != null) hordeNet.ApplyJoinSnapshot(joinSnapshot, joinRevision);
    }

    private int _Quant16(float v)
    {
        int q = (int)(v * 64f);
        if (q < 0) q = 0;
        if (q > 65535) q = 65535;
        return q;
    }

    private int _Quant8(float v)
    {
        int q = (int)v;
        if (q < 0) q = 0;
        if (q > 255) q = 255;
        return q;
    }
}
