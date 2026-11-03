using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Multi-barrel hitscan gun (M7b, roguelite). Up to 5 barrels fire simultaneously
/// while the trigger is held, each with its own rate/ammo/reload. Barrel types:
/// 0 basic, 1 gatling, 2 shotgun, 3 drill (short range + wide hit radius),
/// 4 railgun. Shop cards (M7c) add barrels, level them up, or apply global stat
/// multipliers via ApplyCard. All feedback is local; no per-hit network traffic.
/// The packed loadout (gunLoadout) syncs so other clients see the barrel visuals.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class GunController : UdonSharpBehaviour
{
    public HordeSim hordeSim;
    public HordeNet hordeNet;

    [Header("Barrel system (M7b)")]
    public int maxBarrels = 5;

    // ---- card ids (shared with ShopController) ----
    public const int CARD_GATLING = 0;
    public const int CARD_SHOTGUN = 1;
    public const int CARD_DRILL = 2;
    public const int CARD_RAILGUN = 3;
    public const int CARD_DAMAGE = 10;
    public const int CARD_RATE = 11;
    public const int CARD_SPREAD = 12;
    public const int CARD_MAG = 13;
    public const int CARD_RELOAD = 14;
    public const int CARD_BARREL_UP = 20;

    [Header("Audio")]
    public AudioSource audioSrc;
    public AudioClip shotClip;

    [Header("UI (world-space, behind the gun)")]
    public Text ammoText;
    public Text reloadText;
    public Text loadoutText;

    [Header("FX")]
    public ParticleSystem muzzlePs;

    [Header("Barrel visuals (extra barrels beyond the first; toggled per ownership)")]
    public GameObject[] barrelVisuals;

    [Header("Weapon config (kept: wall mask for CPU-side occlusion)")]
    public int wallMask = 2048;          // 1 << 11 (Environment)

    [Header("Respawn (PlayerObject)")]
    public float respawnDistance = 5f;   // not held + farther than this from the owner -> respawn in front

    [Header("Debug (driven via SetProgramVariable + SendCustomEvent)")]
    public int debugTestShots;
    public int debugCard;
    [Tooltip("Debug damage multiplier applied to every barrel (1 = normal). Set on the Gun template before play.")]
    public float debugDamageMul = 1f;

    // ---- barrel base stat tables (type index 0..4, filled in Start) ----
    private float[] _typeDamage;
    private float[] _typeInterval;
    private int[] _typePellets;
    private float[] _typeSpread;
    private float[] _typeRange;
    private int[] _typeMag;
    private float[] _typeReload;
    private float[] _typeHitRadius;   // 0 = default _HitRadius

    // ---- owned barrels ----
    private int[] _barrelType = new int[5];
    private int[] _barrelLevel = new int[5];
    private float[] _barrelNextFire = new float[5];
    private int[] _barrelAmmo = new int[5];
    private float[] _barrelReloadUntil = new float[5];
    private int _barrelCount;

    // ---- global upgrade multipliers (shop cards) ----
    private float _dmgMul = 1f;
    private float _rateMul = 1f;
    private float _spreadMul = 1f;
    private float _magMul = 1f;
    private float _reloadMul = 1f;

    // ---- synced loadout (3 bits type per slot, 7 = empty) ----
    [UdonSynced] public int gunLoadout;

    private VRC_Pickup _pickup;
    private VRCObjectSync _sync;
    private Transform _anchor;           // scratch transform for VRCObjectSync.TeleportTo
    private bool _downLock;

    private bool _held;
    private bool _triggerHeld;
    private int _processedBatchId;

    // per-ray damage of the current/last ray batch (indexed by ray id)
    private float[] _rayDamage;

    // damaged-slot watch (for local kill claims)
    private int[] _watchSlots = new int[32];
    private float[] _watchTimes = new float[32];
    private int _watchCount;
    private int _killsClaimed;

    private const int STATE_DYING = 5;

    void Start()
    {
        _InitTypeTables();
        _rayDamage = new float[HordeSim.MAX_RAYS];
        _AddBarrel(0);   // every gun starts with the basic barrel
        _PackLoadout();
        _UpdateLoadoutVisual();
        if (hordeSim != null) hordeSim.SetHitListener(this);
        _pickup = GetComponent<VRC_Pickup>();
        _sync = GetComponent<VRCObjectSync>();
        _anchor = transform.Find("RespawnAnchor");
    }

    private void _InitTypeTables()
    {
        _typeDamage = new float[5];
        _typeInterval = new float[5];
        _typePellets = new int[5];
        _typeSpread = new float[5];
        _typeRange = new float[5];
        _typeMag = new int[5];
        _typeReload = new float[5];
        _typeHitRadius = new float[5];

        // 0 basic
        _typeDamage[0] = 12f; _typeInterval[0] = 0.11f; _typePellets[0] = 1;
        _typeSpread[0] = 0.012f; _typeRange[0] = 200f; _typeMag[0] = 30;
        _typeReload[0] = 2.2f; _typeHitRadius[0] = 0f;
        // 1 gatling
        _typeDamage[1] = 6f; _typeInterval[1] = 0.06f; _typePellets[1] = 1;
        _typeSpread[1] = 0.03f; _typeRange[1] = 200f; _typeMag[1] = 60;
        _typeReload[1] = 1.6f; _typeHitRadius[1] = 0f;
        // 2 shotgun
        _typeDamage[2] = 8f; _typeInterval[2] = 0.9f; _typePellets[2] = 6;
        _typeSpread[2] = 0.08f; _typeRange[2] = 200f; _typeMag[2] = 8;
        _typeReload[2] = 2.4f; _typeHitRadius[2] = 0f;
        // 3 drill: short range, wide hit radius, heavy damage
        _typeDamage[3] = 45f; _typeInterval[3] = 1.2f; _typePellets[3] = 1;
        _typeSpread[3] = 0f; _typeRange[3] = 3.5f; _typeMag[3] = 6;
        _typeReload[3] = 1.5f; _typeHitRadius[3] = 1.2f;
        // 4 railgun
        _typeDamage[4] = 60f; _typeInterval[4] = 1.5f; _typePellets[4] = 1;
        _typeSpread[4] = 0.002f; _typeRange[4] = 200f; _typeMag[4] = 5;
        _typeReload[4] = 2.0f; _typeHitRadius[4] = 0f;
    }

    public override void OnPickup() { _held = true; }
    public override void OnDrop() { _held = false; _triggerHeld = false; }
    public override void OnPickupUseDown() { _triggerHeld = true; }
    public override void OnPickupUseUp() { _triggerHeld = false; }

    void Update()
    {
        if (_triggerHeld && _held)
        {
            for (int b = 0; b < _barrelCount; b++) _TryFireBarrel(b);
        }
        WatchDamagedSlots();
        if (hordeNet != null) hordeNet.FlushCombatBatch();
        UpdateGunUI();
        UpdateOwnerRespawn();
        UpdateDownLock();
    }

    // ================= firing =================

    private void _TryFireBarrel(int b)
    {
        float now = Time.time;
        if (now < _barrelNextFire[b] || now < _barrelReloadUntil[b]) return;
        int t = _barrelType[b];
        int lvl = _barrelLevel[b];
        float dmg = _typeDamage[t] * _dmgMul * debugDamageMul * (1f + 0.25f * (lvl - 1));
        float interval = _typeInterval[t] / (_rateMul * (1f + 0.06f * (lvl - 1)));
        int pellets = _typePellets[t];
        float spread = _typeSpread[t] * _spreadMul;
        float range = _typeRange[t];
        float hitRadius = _typeHitRadius[t];

        _barrelNextFire[b] = now + interval;
        _barrelAmmo[b]--;
        if (_barrelAmmo[b] <= 0)
        {
            _barrelReloadUntil[b] = now + _typeReload[t] * _reloadMul;
            _barrelAmmo[b] = _MagFor(b);
        }
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
            int rayId = hordeSim.QueueRayRadius(origin, dir, wallDist, hitRadius);
            if (rayId >= 0) _rayDamage[rayId] = dmg;
        }
    }

    private int _MagFor(int b)
    {
        int m = (int)(_typeMag[_barrelType[b]] * _magMul);
        if (m < 1) m = 1;
        return m;
    }

    /// <summary>Called by HordeSim when a ray batch resolves (results in hitRay*).</summary>
    public void _OnHitResults()
    {
        if (hordeSim == null) return;
        if (_processedBatchId == hordeSim.hitBatchId) return;
        _processedBatchId = hordeSim.hitBatchId;
        for (int r = 0; r < HordeSim.MAX_RAYS; r++)
        {
            float slotF = hordeSim.hitRaySlot[r];
            if (slotF < 0f) continue;
            int slot = (int)slotF;
            float dmg = _rayDamage[r];
            if (dmg <= 0f) dmg = 12f;
            hordeSim.QueueDamage(slot, dmg);
            if (hordeNet != null && hordeNet.IsBossSlot(slot))
                hordeNet.StageBossDamage(slot, (int)dmg);
            WatchSlot(slot);
        }
    }

    // ================= shop cards =================

    /// <summary>Candidate card ids for the shop (unowned barrels + stat ups).</summary>
    public int[] GetCardOptions()
    {
        int[] cand = new int[16];
        int n = 0;
        if (_barrelCount < maxBarrels)
        {
            for (int t = 1; t <= 4; t++)
            {
                bool owned = false;
                for (int b = 0; b < _barrelCount; b++)
                {
                    if (_barrelType[b] == t) { owned = true; break; }
                }
                if (!owned) { cand[n] = t - 1; n++; }   // card id = type - 1
            }
        }
        cand[n] = CARD_DAMAGE; n++;
        cand[n] = CARD_RATE; n++;
        cand[n] = CARD_SPREAD; n++;
        cand[n] = CARD_MAG; n++;
        cand[n] = CARD_RELOAD; n++;
        if (_barrelCount > 0) { cand[n] = CARD_BARREL_UP; n++; }
        int[] opts = new int[n];
        for (int i = 0; i < n; i++) opts[i] = cand[i];
        return opts;
    }

    /// <summary>Applies a shop card (called locally by the ShopController).</summary>
    public void ApplyCard(int cardId)
    {
        if (cardId >= CARD_GATLING && cardId <= CARD_RAILGUN)
        {
            _AddBarrel(cardId + 1);
        }
        else if (cardId == CARD_DAMAGE) _dmgMul *= 1.25f;
        else if (cardId == CARD_RATE) _rateMul *= 1.2f;
        else if (cardId == CARD_SPREAD) _spreadMul *= 0.7f;
        else if (cardId == CARD_MAG) _magMul *= 1.5f;
        else if (cardId == CARD_RELOAD) _reloadMul *= 0.7f;
        else if (cardId == CARD_BARREL_UP) _UpgradeRandomBarrel();
        _PackLoadout();
        _UpdateLoadoutVisual();
        UpdateGunUI();
    }

    private void _AddBarrel(int type)
    {
        if (_barrelCount >= maxBarrels) return;
        int b = _barrelCount;
        _barrelType[b] = type;
        _barrelLevel[b] = 1;
        _barrelAmmo[b] = _MagFor(b);
        _barrelNextFire[b] = 0f;
        _barrelReloadUntil[b] = 0f;
        _barrelCount++;
    }

    private void _UpgradeRandomBarrel()
    {
        if (_barrelCount <= 0) return;
        int b = Random.Range(0, _barrelCount);
        if (_barrelLevel[b] < 5) _barrelLevel[b]++;
    }

    // ================= loadout sync / visuals =================

    private void _PackLoadout()
    {
        int packed = 0;
        for (int i = 0; i < maxBarrels; i++)
        {
            int t = (i < _barrelCount) ? _barrelType[i] : 7;
            packed |= (t & 7) << (i * 3);
        }
        gunLoadout = packed;
    }

    public override void OnDeserialization()
    {
        // remote clients mirror the barrel visuals from the packed loadout
        if (VRC.SDKBase.Networking.IsOwner(gameObject)) return;
        int count = 0;
        for (int i = 0; i < maxBarrels; i++)
        {
            int t = (gunLoadout >> (i * 3)) & 7;
            if (t != 7) count = i + 1;
        }
        _SetBarrelVisuals(count);
    }

    private void _UpdateLoadoutVisual()
    {
        _SetBarrelVisuals(_barrelCount);
    }

    private void _SetBarrelVisuals(int count)
    {
        if (barrelVisuals == null) return;
        for (int i = 0; i < barrelVisuals.Length; i++)
        {
            GameObject go = barrelVisuals[i];
            if (go == null) continue;
            bool active = i < count - 1;   // visuals are the extra barrels beyond the first
            if (go.activeSelf != active) go.SetActive(active);
        }
    }

    // ================= player-object plumbing (unchanged) =================

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
        if (ammoText != null)
            ammoText.text = _barrelAmmo[0] + " / " + _MagFor(0);
        if (reloadText != null)
        {
            bool reloading = false;
            for (int b = 0; b < _barrelCount; b++)
            {
                if (Time.time < _barrelReloadUntil[b]) { reloading = true; break; }
            }
            if (reloadText.gameObject.activeSelf != reloading)
                reloadText.gameObject.SetActive(reloading);
        }
        if (loadoutText != null)
        {
            string s = "";
            for (int b = 0; b < _barrelCount; b++)
            {
                if (b > 0) s += " + ";
                s += _TypeName(_barrelType[b]);
                if (_barrelLevel[b] > 1) s += "Lv" + _barrelLevel[b];
            }
            loadoutText.text = s;
        }
    }

    private string _TypeName(int t)
    {
        if (t == 1) return "GATLING";
        if (t == 2) return "SHOTGUN";
        if (t == 3) return "DRILL";
        if (t == 4) return "RAILGUN";
        return "BASIC";
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

    // ---- debug entry points ----

    /// <summary>Fires debug rays from above the arena center straight down.</summary>
    public void _DebugTestShots()
    {
        int n = debugTestShots;
        if (n > HordeSim.MAX_RAYS) n = HordeSim.MAX_RAYS;
        Vector3 origin = new Vector3(0f, 8f, 0f);
        for (int i = 0; i < n; i++)
        {
            Vector3 offset = new Vector3((i % 4 - 1.5f) * 0.4f, 0f, (i / 4 - 0.5f) * 0.4f);
            Vector3 o = origin + offset;
            float wallDist = 200f;
            RaycastHit hit;
            if (Physics.Raycast(o, Vector3.down, out hit, 200f, wallMask))
                wallDist = hit.distance;
            int rayId = hordeSim.QueueRayRadius(o, Vector3.down, wallDist, 0f);
            if (rayId >= 0) _rayDamage[rayId] = 12f;
        }
        Debug.Log("[Gun] debug shots queued: " + n);
    }

    public void _DebugLogCombat()
    {
        string barrels = "";
        for (int b = 0; b < _barrelCount; b++)
        {
            barrels += _TypeName(_barrelType[b]) + "Lv" + _barrelLevel[b] + "(" + _barrelAmmo[b] + ") ";
        }
        Debug.Log("[Gun] barrels=" + _barrelCount + " [" + barrels + "]"
            + " dmgMul=" + _dmgMul.ToString("F2") + " rateMul=" + _rateMul.ToString("F2")
            + " killsClaimed=" + _killsClaimed
            + " hordeBatch=" + (hordeSim != null ? hordeSim.hitBatchId : -1));
    }

    /// <summary>Debug: applies the card id in debugCard (set via SetProgramVariable).</summary>
    public void _DebugApplyCard()
    {
        ApplyCard(debugCard);
        Debug.Log("[Gun] debug card applied: " + debugCard);
    }
}
