using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;

/// <summary>
/// Hitscan gun (M3 local combat). Fires rays into HordeSim's GPU HitTest pass
/// (wall occlusion pre-applied CPU-side via Physics.Raycast), applies damage
/// locally on hit confirmation, watches damaged slots for local death, and
/// accumulates kill claims for the batched combat report (networked in M4).
/// All feedback is local; no per-hit network traffic by design.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class GunController : UdonSharpBehaviour
{
    public HordeSim hordeSim;
    public HordeNet hordeNet;

    [Header("Weapon config")]
    public float damage = 12f;
    public float fireInterval = 0.11f;
    public int pellets = 1;              // shotgun: 8
    public float spread = 0.012f;
    public float range = 200f;
    public int magSize = 30;
    public float reloadTime = 2.2f;
    public int wallMask = 2048;          // 1 << 11 (Environment)

    [Header("Audio")]
    public AudioSource audioSrc;
    public AudioClip shotClip;

    [Header("UI (world-space, behind the gun)")]
    public Text ammoText;
    public Text reloadText;

    [Header("FX")]
    public ParticleSystem muzzlePs;

    [Header("Respawn (PlayerObject)")]
    public float respawnDistance = 5f;   // not held + farther than this from the owner -> respawn in front

    private VRC_Pickup _pickup;
    private VRCObjectSync _sync;
    private Transform _anchor;           // scratch transform for VRCObjectSync.TeleportTo
    private bool _downLock;

    [Header("Debug (driven via SetProgramVariable + SendCustomEvent)")]
    public int debugTestShots;

    private bool _held;
    private bool _triggerHeld;
    private float _nextFireTime;
    private float _reloadUntil;
    private int _ammo;
    private int _processedBatchId;

    // damaged-slot watch (for local kill claims)
    private int[] _watchSlots = new int[32];
    private float[] _watchTimes = new float[32];
    private int _watchCount;
    private int _killsClaimed;

    private const int STATE_DYING = 5;

    void Start()
    {
        _ammo = magSize;
        if (hordeSim != null) hordeSim.SetHitListener(this);
        _pickup = GetComponent<VRC_Pickup>();
        _sync = GetComponent<VRCObjectSync>();
        _anchor = transform.Find("RespawnAnchor");
    }

    public override void OnPickup() { _held = true; }
    public override void OnDrop() { _held = false; _triggerHeld = false; }
    public override void OnPickupUseDown() { _triggerHeld = true; TryFire(); }
    public override void OnPickupUseUp() { _triggerHeld = false; }

    void Update()
    {
        if (_triggerHeld && _held) TryFire();
        WatchDamagedSlots();
        if (hordeNet != null) hordeNet.FlushCombatBatch();
        UpdateGunUI();
        UpdateOwnerRespawn();
        UpdateDownLock();
    }

    /// <summary>
    /// PlayerObject respawn: when I own this gun, am not holding it, and it is
    /// farther than respawnDistance from me, teleport it in front of me
    /// (VRCObjectSync broadcasts the move to everyone).
    /// </summary>
    private void UpdateOwnerRespawn()
    {
        if (_held || _sync == null) return;
        if (!VRC.SDKBase.Networking.IsOwner(gameObject)) return;
        VRCPlayerApi me = VRC.SDKBase.Networking.LocalPlayer;
        Vector3 gp = me.GetPosition();
        float dx = transform.position.x - gp.x;
        float dz = transform.position.z - gp.z;
        if (dx * dx + dz * dz < respawnDistance * respawnDistance) return;
        Vector3 fwd = me.GetRotation() * Vector3.forward;
        fwd.y = 0f;
        fwd = fwd.normalized;
        Vector3 target = gp + fwd * 1.2f + Vector3.up * 0.6f;
        if (_anchor != null)
        {
            // VRCObjectSync.TeleportTo takes a Transform — stage the target pose
            // on a scratch child, then teleport the whole gun to it.
            _anchor.position = target;
            _anchor.rotation = me.GetRotation();
            _sync.TeleportTo(_anchor);
        }
        else
        {
            transform.SetPositionAndRotation(target, me.GetRotation());
        }
    }

    /// <summary>Down/revive: lock my own pickup while playerHp says I am down.</summary>
    private void UpdateDownLock()
    {
        if (hordeNet == null || _pickup == null) return;
        int idx = VRC.SDKBase.Networking.LocalPlayer.playerId - 1;
        if (idx < 0 || idx >= 16) idx = 0;
        bool down = (int)hordeNet.playerHp[idx] <= 0;
        if (down && !_downLock)
        {
            _downLock = true;
            _pickup.Drop();
            _pickup.pickupable = false;
        }
        else if (!down && _downLock)
        {
            _downLock = false;
            _pickup.pickupable = true;
        }
    }

    private void UpdateGunUI()
    {
        if (ammoText != null) ammoText.text = _ammo + " / " + magSize;
        if (reloadText != null)
        {
            bool reloading = Time.time < _reloadUntil;
            if (reloadText.gameObject.activeSelf != reloading)
                reloadText.gameObject.SetActive(reloading);
        }
    }

    private void TryFire()
    {
        float now = Time.time;
        if (now < _nextFireTime || now < _reloadUntil) return;
        if (_ammo <= 0) { StartReload(); return; }
        _nextFireTime = now + fireInterval;
        _ammo--;
        if (audioSrc != null && shotClip != null) audioSrc.PlayOneShot(shotClip);
        if (muzzlePs != null) muzzlePs.Emit(4);

        Vector3 origin = transform.position + transform.forward * 0.25f;
        for (int p = 0; p < pellets; p++)
        {
            Vector3 dir = transform.forward;
            if (spread > 0f)
                dir = (dir + Random.insideUnitSphere * spread).normalized;
            float wallDist = range;
            RaycastHit hit;
            if (Physics.Raycast(origin, dir, out hit, range, wallMask))
                wallDist = hit.distance;
            hordeSim.QueueRay(origin, dir, wallDist);
        }
    }

    /// <summary>Called by HordeSim when a ray batch resolves (results in hitRay*).</summary>
    public void _OnHitResults()
    {
        if (hordeSim == null) return;
        if (_processedBatchId == hordeSim.hitBatchId) return;
        _processedBatchId = hordeSim.hitBatchId;
        for (int r = 0; r < 8; r++)
        {
            float slotF = hordeSim.hitRaySlot[r];
            if (slotF < 0f) continue;
            int slot = (int)slotF;
            hordeSim.QueueDamage(slot, damage);
            if (hordeNet != null && hordeNet.IsBossSlot(slot))
                hordeNet.StageBossDamage(slot, (int)damage);
            WatchSlot(slot);
        }
    }

    private void WatchSlot(int slot)
    {
        for (int i = 0; i < _watchCount; i++)
        {
            if (_watchSlots[i] == slot) { _watchTimes[i] = Time.time; return; }
        }
        if (_watchCount >= 32) return;
        _watchSlots[_watchCount] = slot;
        _watchTimes[_watchCount] = Time.time;
        _watchCount++;
    }

    private void WatchDamagedSlots()
    {
        if (_watchCount == 0 || hordeSim == null) return;
        float now = Time.time;
        for (int i = _watchCount - 1; i >= 0; i--)
        {
            int slot = _watchSlots[i];
            if (now - _watchTimes[i] > 1.0f) { _watchSlots[i] = _watchSlots[_watchCount - 1]; _watchTimes[i] = _watchTimes[_watchCount - 1]; _watchCount--; continue; }
            int s = (int)hordeSim.mirrorState[slot];
            if (s == STATE_DYING)
            {
                ClaimKill(slot);
                _watchSlots[i] = _watchSlots[_watchCount - 1];
                _watchTimes[i] = _watchTimes[_watchCount - 1];
                _watchCount--;
            }
        }
    }

    private void ClaimKill(int slot)
    {
        _killsClaimed++;
        if (hordeNet != null) hordeNet.StageKillClaim(slot);
    }

    private void StartReload()
    {
        _reloadUntil = Time.time + reloadTime;
        _ammo = magSize;
    }

    // ---- debug entry points ----

    /// <summary>Fires debug rays from above the arena center straight down.</summary>
    public void _DebugTestShots()
    {
        int n = debugTestShots;
        if (n > 8) n = 8;
        Vector3 origin = new Vector3(0f, 8f, 0f);
        for (int i = 0; i < n; i++)
        {
            Vector3 offset = new Vector3((i % 4 - 1.5f) * 0.4f, 0f, (i / 4 - 0.5f) * 0.4f);
            Vector3 o = origin + offset;
            float wallDist = range;
            RaycastHit hit;
            if (Physics.Raycast(o, Vector3.down, out hit, range, wallMask))
                wallDist = hit.distance;
            hordeSim.QueueRay(o, Vector3.down, wallDist);
        }
        Debug.Log("[Gun] debug shots queued: " + n);
    }

    public void _DebugLogCombat()
    {
        Debug.Log("[Gun] ammo=" + _ammo + " killsClaimed=" + _killsClaimed
            + " watchCount=" + _watchCount + " processedBatch=" + _processedBatchId
            + " hordeBatch=" + (hordeSim != null ? hordeSim.hitBatchId : -1));
    }
}
