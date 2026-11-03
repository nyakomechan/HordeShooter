using UdonSharp;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Owner-authoritative horde network state (M4).
/// Synced (Manual): wave state, event ring (spawn/kill/damage events), proximity
/// snapshot (24 nearest-to-any-player enemies, 3 ints each), boss HP, player HP,
/// and a full-slot catch-up snapshot for late joiners.
///
/// Design (per plan §5.4/5.5):
///  - Trash HP is NEVER synced; kills are owner-arbitrated via batched reports.
///  - Boss HP lives in the owner's CPU array and rides the snapshot serialization.
///  - Hits are batched per shooter (~5Hz), never per-hit events.
///  - Remotes run the same local GPU sim; snapshots soft-correct near enemies.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class HordeNet : UdonSharpBehaviour
{
    public HordeSim hordeSim;
    public GunController gun;          // optional: wired for combat batch routing
    public HordeNetCatchup catchup;    // late-joiner snapshot (serialized on join only)
    public float snapshotInterval = 0.33f;
    public int snapshotK = 32;

    // ---- synced state ----
    [UdonSynced] public int gamePhase;
    [UdonSynced] public int waveNumber;
    [UdonSynced] public int waveSeed;
    [UdonSynced] public int[] eventRing = new int[128];
    [UdonSynced] public int eventRevision;
    [UdonSynced] public int eventHead;
    [UdonSynced] public int[] snapSlots = new int[32];
    [UdonSynced] public int[] snapData = new int[96];
    [UdonSynced] public int snapRevision;
    [UdonSynced] public int[] bossHp = new int[8];
    [UdonSynced] public int[] bossSlots = new int[8];
    [UdonSynced] public int[] playerHp = new int[16];
    [UdonSynced] public int clearRevision;   // bumped on game restart: remotes clear their local sim
    // ---- roguelite (M7) ----
    [UdonSynced] public int quotaType;        // 0 = kill N, 1 = survive T seconds, 2 = boss kill
    [UdonSynced] public int quotaTarget;      // kill count / seconds / 1 (boss)
    [UdonSynced] public int quotaProgress;    // kills so far / elapsed seconds
    [UdonSynced] public int waveEndRevision;  // bumped at wave end: remotes despawn all remaining enemies
    [UdonSynced] public int shopReadyBits;    // bit (playerId-1) set while that player is ready in the shop
    // ---- ownership takeover (M7f): lets a new owner resume the director mid-phase ----
    [UdonSynced] public int waveNextSlot;     // next slot the maintain spawner will allocate
    [UdonSynced] public int shopRemaining;    // seconds left in the shop phase (phase 2)
    [UdonSynced] public int gameOverRemaining; // seconds left of the game-over grace (phase 3)

    // ---- event ring ----
    public const int EV_NONE = 0;
    public const int EV_SPAWN = 1;
    public const int EV_KILL = 2;
    public const int EV_PLAYER_DAMAGE = 3;
    public const int EV_SCORE = 4;
    private const int RING_EVENTS = 32;
    private const int RING_WORDS = 128;
    private int _ringHead;      // next write index (in events)
    private float _lastExtraSerialize;

    // ---- debug ----
    public bool debugNetLog;
    public int debugSpawnCount;
    public int debugSpawnVariant;
    // ---- snapshot packing (owner) ----
    private float _snapTimer;
    private int _lastKnownPlayers;
    private float[] _selDist = new float[32];
    private int[] _selSlot = new int[32];
    private Vector3[] _playerPos = new Vector3[16];
    private bool[] _playerActive = new bool[16];
    private int _playerCount;

    // ---- correction queue (remote apply, drained 24/frame; 6 words per entry) ----
    private int[] _corrQueue = new int[3072];
    private int _corrQueueCount;
    private Color[] _corrColors;

    // ---- combat batch (sender side staging) ----
    private int[] _outBatch = new int[33];
    private int _outBatchCount;
    private float _outBatchTimer;
    private const float OUT_BATCH_INTERVAL = 0.2f;

    void Start()
    {
        _corrColors = new Color[24];
        for (int i = 0; i < 24; i++) _corrColors[i] = new Color(-1f, 0f, 0f, 0f);
        for (int i = 0; i < 8; i++) bossSlots[i] = -1;
        for (int i = 0; i < 16; i++) playerHp[i] = 100;
    }

    // ================= owner-side: event ring =================

    private bool _IsAuthority()
    {
        return Networking.IsOwner(gameObject);
    }

    private void _RingPush(int type, int slot, int p0, int p1)
    {
        int w = _ringHead * 4;
        if (w + 3 >= RING_WORDS) return;
        eventRing[w] = type;
        eventRing[w + 1] = slot;
        eventRing[w + 2] = p0;
        eventRing[w + 3] = p1;
        _ringHead = (_ringHead + 1) % RING_EVENTS;
        eventHead = _ringHead;
        eventRevision++;
    }

    /// <summary>Owner: emit a spawn event and apply locally.</summary>
    public void AuthoritySpawn(int slot, int spawnIdx, int variant)
    {
        if (!_IsAuthority()) return;
        hordeSim.QueueSpawn(slot, spawnIdx, variant);
        _RingPush(EV_SPAWN, slot, spawnIdx, variant);
        RequestSerialization();
    }

    /// <summary>
    /// Owner: emit a kill event (already validated) and apply locally.
    /// The event carries the owner's observed position (p0 = x16|z16,
    /// p1 = shooterId16|y16, quantized at 1/64 m around ±512 m) so remotes can
    /// snap the corpse to where the shooter saw it before playing the death.
    /// </summary>
    public void AuthorityKill(int slot, int shooterId)
    {
        if (!_IsAuthority()) return;
        int p0 = 0;
        int p1 = shooterId << 16;
        float[] mx = hordeSim.mirrorPosX;
        float[] my = hordeSim.mirrorPosY;
        float[] mz = hordeSim.mirrorPosZ;
        if (mx != null && slot >= 0 && slot < mx.Length)
        {
            int qx = _Quant16(mx[slot] + 512f);
            int qz = _Quant16(mz[slot] + 512f);
            int qy = _Quant16(my[slot] + 512f);
            p0 = (qx << 16) | (qz & 0xFFFF);
            p1 |= (qy & 0xFFFF);
        }
        hordeSim.QueueKill(slot);
        _RingPush(EV_KILL, slot, p0, p1);
        // roguelite kill quota: the owner counts every arbitrated kill during a wave
        if (gamePhase == 1 && quotaType == 0) quotaProgress++;
        RequestSerialization();
    }

    /// <summary>
    /// Debug: spawn up to 16 enemies through the authority path (ring events included).
    /// Capped at 16 because the ring holds 32 events and remotes walk at most 16 per
    /// serialization — burst spawns beyond that must be paced by the wave director.
    /// </summary>
    public void _DebugAuthoritySpawnWave()
    {
        if (!_IsAuthority()) return;
        int n = debugSpawnCount;
        if (n > 16) n = 16;
        for (int i = 0; i < n; i++)
        {
            AuthoritySpawn(i, i % 8, debugSpawnVariant);
        }
        if (debugNetLog) Debug.Log("[HordeNet] debug authority spawn wave: " + n);
    }

    // ================= combat batch (shooters -> owner) =================

    /// <summary>Gun side: stage a kill claim (or boss damage) into the outgoing batch.</summary>
    public void StageKillClaim(int slot)
    {
        if (_outBatchCount >= 16) return;
        _outBatch[1 + _outBatchCount * 2] = slot;
        _outBatch[2 + _outBatchCount * 2] = 0;
        _outBatchCount++;
    }

    public void StageBossDamage(int slot, int damage)
    {
        if (_outBatchCount >= 16) return;
        _outBatch[1 + _outBatchCount * 2] = slot;
        _outBatch[2 + _outBatchCount * 2] = damage;
        _outBatchCount++;
    }

    /// <summary>Gun side: flush the staged batch (call ~5Hz from Update).</summary>
    public void FlushCombatBatch()
    {
        if (_outBatchCount == 0) return;
        _outBatchTimer += Time.deltaTime;
        if (_outBatchTimer < OUT_BATCH_INTERVAL) return;
        _outBatchTimer = 0f;
        _outBatch[0] = _outBatchCount;
        int[] batch = new int[1 + _outBatchCount * 2];
        for (int i = 0; i < batch.Length; i++) batch[i] = _outBatch[i];
        _outBatchCount = 0;
        if (_IsAuthority())
        {
            _ReportCombat(batch);
        }
        else
        {
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(_ReportCombat), batch);
        }
    }

    /// <summary>Owner only: validate and apply a combat batch.</summary>
    [NetworkCallable(20)]
    public void _ReportCombat(int[] batch)
    {
        if (!NetworkCalling.InNetworkCall && !_IsAuthority()) return;
        if (!_IsAuthority()) return;
        if (batch == null || batch.Length < 1) return;
        int n = batch[0];
        if (n < 1 || n > 16 || batch.Length < 1 + n * 2) return;
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        // A direct local call (no network context) is the authority's own client.
        int shooterId = (caller != null && caller.IsValid()) ? caller.playerId : Networking.LocalPlayer.playerId;
        bool selfClaim = shooterId == Networking.LocalPlayer.playerId;

        for (int i = 0; i < n; i++)
        {
            int slot = batch[1 + i * 2];
            int val = batch[2 + i * 2];
            if (slot < 0 || slot >= hordeSim.slotCount) continue;
            int bossIdx = _IndexOfBossSlot(slot);
            if (bossIdx >= 0 && val > 0)
            {
                // Boss damage: owner CPU HP is the only truth.
                int hp = bossHp[bossIdx] - val;
                if (hp < 0) hp = 0;
                bossHp[bossIdx] = hp;
                hordeSim.QueueDamage(slot, (float)val);
                if (hp == 0)
                {
                    AuthorityKill(slot, shooterId);
                }
            }
            else
            {
                // Trash kill claim: validate the slot is alive on our mirror.
                // Self-claims skip the check: the shooter's local sim already killed
                // it, so the event must be emitted for the other clients regardless.
                int s = (int)hordeSim.mirrorState[slot];
                if (s >= 1 && s <= 4 || selfClaim)
                {
                    AuthorityKill(slot, shooterId);
                }
            }
        }
    }

    private int _IndexOfBossSlot(int slot)
    {
        for (int i = 0; i < 8; i++)
        {
            if (bossSlots[i] == slot) return i;
        }
        return -1;
    }

    /// <summary>True when the slot is a boss/special slot (owner-authoritative HP).</summary>
    public bool IsBossSlot(int slot)
    {
        return _IndexOfBossSlot(slot) >= 0;
    }

    // ================= owner-side: proximity snapshot =================

    private float _ownerAlignTimer;

    void Update()
    {
        FlushCombatBatch();
        DrainCorrectionQueue();
        // ownership unification (M7f): keep HordeSim + HordeNetCatchup owned by
        // the same player as HordeNet, so every system always shares one owner
        // even after the old owner leaves (VRChat transfers each object
        // independently and the per-object pick is not guaranteed to agree).
        _ownerAlignTimer += Time.deltaTime;
        if (_ownerAlignTimer >= 1f)
        {
            _ownerAlignTimer = 0f;
            _AlignOwnership();
        }
        if (_IsAuthority())
        {
            _WaveTick();
            _snapTimer += Time.deltaTime;
            if (_snapTimer >= snapshotInterval)
            {
                _snapTimer = 0f;
                _PackSnapshot();
            }
        }
    }

    /// <summary>Aligns HordeSim / HordeNetCatchup ownership to HordeNet's owner (1Hz poll).</summary>
    private void _AlignOwnership()
    {
        VRCPlayerApi netOwner = Networking.GetOwner(gameObject);
        if (netOwner == null || !netOwner.IsValid()) return;
        if (hordeSim != null)
        {
            VRCPlayerApi simOwner = Networking.GetOwner(hordeSim.gameObject);
            if (simOwner == null || !simOwner.IsValid() || simOwner.playerId != netOwner.playerId)
                Networking.SetOwner(netOwner, hordeSim.gameObject);
        }
        if (catchup != null)
        {
            VRCPlayerApi cuOwner = Networking.GetOwner(catchup.gameObject);
            if (cuOwner == null || !cuOwner.IsValid() || cuOwner.playerId != netOwner.playerId)
                Networking.SetOwner(netOwner, catchup.gameObject);
        }
    }

    /// <summary>
    /// Ownership transfer (e.g. the old owner left): if THIS client became the
    /// HordeNet owner, reconstruct the director's runtime state from the synced
    /// fields so the wave/shop/game-over flow continues seamlessly, and pull
    /// HordeSim + HordeNetCatchup under the same owner.
    /// </summary>
    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        _AlignOwnership();
        if (player == null || !player.IsValid()) return;
        VRCPlayerApi me = Networking.LocalPlayer;
        if (me == null || !me.IsValid()) return;   // early init: LocalPlayer not ready yet
        if (player.playerId != me.playerId) return;   // someone else became owner
        _TakeOverDirector();
    }

    /// <summary>New owner: resume the director from the last synced state.</summary>
    private void _TakeOverDirector()
    {
        _waveNextSlot = waveNextSlot;
        _waveBossSpawned = quotaType == 2 && _CountBossSlots() > 0;
        if (gamePhase == 1 && quotaType == 1)
            _waveStartTime = Time.time - quotaProgress;   // survive quota continues where it left off
        if (gamePhase == 2)
            _shopUntil = Time.time + shopRemaining;
        if (gamePhase == 3)
            _gameOverAt = Time.time - (gameOverResetSeconds - gameOverRemaining);
        _maintainTimer = 0f;
        _waveEndCheckTimer = 0f;
        _damageTimer = 0f;
        _snapTimer = snapshotInterval;   // pack a fresh snapshot immediately
        Debug.Log("[HordeNet] ownership taken over: phase=" + gamePhase + " wave=" + waveNumber
            + " nextSlot=" + _waveNextSlot + " shopRemaining=" + shopRemaining
            + " gameOverRemaining=" + gameOverRemaining);
    }

    private void _RefreshPlayers()
    {
        _playerCount = 0;
        VRCPlayerApi[] players = VRCPlayerApi.GetPlayers(new VRCPlayerApi[16]);
        for (int i = 0; i < players.Length && i < 16; i++)
        {
            VRCPlayerApi p = players[i];
            if (p == null || !p.IsValid()) continue;
            _playerPos[_playerCount] = p.GetPosition();
            _playerActive[_playerCount] = true;
            _playerIds[_playerCount] = p.playerId;
            _playerCount++;
        }
    }

    private void _PackSnapshot()
    {
        if (hordeSim == null) return;
        _RefreshPlayers();
        // Fallback catch-up trigger: if the player count grew since the last
        // snapshot (OnPlayerJoined can be missed in some simulators), send the
        // join snapshot anyway. Harmless in production (OnPlayerJoined already
        // packs it; the revision guard skips duplicate applies).
        if (_playerCount > _lastKnownPlayers)
        {
            _lastKnownPlayers = _playerCount;
            if (catchup != null) catchup.PackAndSend();
        }
        else if (_playerCount < _lastKnownPlayers)
        {
            _lastKnownPlayers = _playerCount;
        }
        if (_playerCount == 0) return;

        // Top-K nearest-to-any-player alive slots (insertion into a bounded list).
        for (int i = 0; i < snapshotK; i++) { _selDist[i] = 1e30f; _selSlot[i] = -1; }
        float[] mx = hordeSim.mirrorPosX;
        float[] mz = hordeSim.mirrorPosZ;
        float[] ms = hordeSim.mirrorState;
        if (mx == null) return;
        int slotCount = hordeSim.slotCount;
        for (int i = 0; i < slotCount; i++)
        {
            int s = (int)ms[i];
            if (s < 1 || s > 4) continue;
            float x = mx[i];
            float z = mz[i];
            float best = 1e30f;
            for (int p = 0; p < _playerCount; p++)
            {
                float dx = x - _playerPos[p].x;
                float dz = z - _playerPos[p].z;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) best = d2;
            }
            if (best >= _selDist[snapshotK - 1]) continue;
            // insert
            int j = snapshotK - 1;
            while (j > 0 && _selDist[j - 1] > best) { _selDist[j] = _selDist[j - 1]; _selSlot[j] = _selSlot[j - 1]; j--; }
            _selDist[j] = best;
            _selSlot[j] = i;
        }

        // Quantize: 3 ints per enemy (12B). Position relative to arena center,
        // 16 bits per axis at 1/64 m (range ±512 m).
        float[] my = hordeSim.mirrorPosY;
        float[] mh = hordeSim.mirrorYaw;
        float[] mhp = hordeSim.mirrorHp;
        int count = 0;
        for (int k = 0; k < snapshotK; k++)
        {
            int slot = _selSlot[k];
            if (slot < 0) { snapSlots[k] = -1; continue; }
            count++;
            snapSlots[k] = slot;
            int qx = _Quant16(mx[slot] + 512f);
            int qz = _Quant16(mz[slot] + 512f);
            int qy = _Quant16(my[slot] + 512f);
            int yaw8 = _Quant8((mh[slot] / 6.28318f + 1f) * 0.5f * 255f);
            int hp8 = _Quant8(mhp[slot] * 0.5f);
            int st8 = (int)ms[slot] & 0xFF;
            snapData[k * 3] = (qx << 16) | (qz & 0xFFFF);
            snapData[k * 3 + 1] = qy & 0xFFFF;
            snapData[k * 3 + 2] = (yaw8 << 24) | (hp8 << 16) | (st8 << 8);
        }
        snapRevision++;
        // ownership-takeover state: lets a new owner resume the director
        waveNextSlot = _waveNextSlot;
        shopRemaining = (gamePhase == 2) ? (int)Mathf.Max(0f, _shopUntil - Time.time) : 0;
        gameOverRemaining = (gamePhase == 3) ? (int)Mathf.Max(0f, gameOverResetSeconds - (Time.time - _gameOverAt)) : 0;
        RequestSerialization();
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

    // ================= late joiner catch-up =================

    private float _selfJoinTime;
    private int _appliedJoinRevision;

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (player == null || !player.IsValid()) return;
        if (player.playerId == Networking.LocalPlayer.playerId)
        {
            _selfJoinTime = Time.time;
            return;
        }
        if (!_IsAuthority()) return;
        // Full-slot snapshot lives in HordeNetCatchup and is serialized on join only.
        if (catchup != null) catchup.PackAndSend();
    }

    /// <summary>
    /// Remote: apply a received catch-up snapshot as corrections. Only fresh
    /// joiners run the full 512-entry wave; existing clients ignore it.
    /// Slots whose snapshot state is alive (1-4) are also WOKEN — a late
    /// joiner's local sim has every slot inactive, and position corrections
    /// alone never activate anything.
    /// </summary>
    public void ApplyJoinSnapshot(int[] snap, int rev)
    {
        if (hordeSim == null || snap == null) return;
        if (_IsAuthority()) return;                 // the owner built it
        if (rev <= 0 || rev == _appliedJoinRevision) return;
        _appliedJoinRevision = rev;
        if (Time.time - _selfJoinTime > 10f) return; // not a fresh joiner
        int woke = 0;
        for (int i = 0; i < 512 && i * 3 + 2 < snap.Length; i++)
        {
            int qx = (snap[i * 3] >> 16) & 0xFFFF;
            int qz = snap[i * 3] & 0xFFFF;
            int qy = snap[i * 3 + 1] & 0xFFFF;
            int st8 = (snap[i * 3 + 2] >> 8) & 0xFF;
            int var8 = snap[i * 3 + 2] & 0xFF;
            if (st8 >= 1 && st8 <= 4)
            {
                _EnqueueCorrection(i, qx, qy, qz, st8, var8);
                woke++;
            }
        }
        if (debugNetLog) Debug.Log("[HordeNet] join snapshot rev=" + rev + ": woke " + woke + " slots");
    }
}
