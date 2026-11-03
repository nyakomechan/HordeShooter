using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;

/// <summary>
/// HordeNet partial: roguelite wave director (owner-authoritative).
/// gamePhase: 0 = lobby, 1 = wave active, 2 = shop, 3 = game over.
///
/// Waves are quota-based (M7):
///   quotaType 0 = kill N trash, 1 = survive T seconds, 2 = kill the boss
///   (every bossEveryWaves-th wave; the boss spawns at wave start).
/// Trash enemies are MAINTAINED at a constant field count: every
/// maintainCheckInterval the owner tops the alive count up to the wave's
/// maintain target with paced authority spawns (≤ spawnBatchMax per batch,
/// event-ring safe). At wave end every remaining enemy despawns instantly
/// (HordeSim.DespawnAll — no death anim, no kill events) and the game enters
/// the shop phase; the next wave starts when every player is ready or after
/// shopSeconds.
/// </summary>
public partial class HordeNet
{
    [Header("Roguelite wave config")]
    public int maintainBase = 40;          // trash kept alive on the field, wave 1
    public int maintainPerWave = 8;
    public int maintainMax = 220;
    public int bossMaintainBase = 16;      // reduced trash pressure on boss waves
    public int bossMaintainPerWave = 4;
    public float maintainCheckInterval = 0.5f;
    public int spawnBatchMax = 16;         // ring-safe batch cap (32-slot ring, 16-step walk)
    public int bossEveryWaves = 3;         // every 3rd wave: boss kill quota
    public int killQuotaBase = 30;
    public int killQuotaPerWave = 10;
    public int surviveBase = 45;           // seconds
    public int survivePerWave = 10;
    public float shopSeconds = 60f;        // shop phase timeout -> next wave
    public float playerDamageInterval = 0.5f;
    public int playerDamagePerTick = 10;
    public float playerAttackRange = 1.4f;
    public float gameOverResetSeconds = 10f;   // game over -> auto reset & respawn after this

    // ---- runtime (owner) ----
    private float _maintainTimer;
    private float _waveEndCheckTimer;
    private int _waveNextSlot;
    private int _waveSpawnIdx;
    private float _waveStartTime;
    private bool _waveBossSpawned;
    private float _shopUntil;
    private float _damageTimer;
    private float _gameOverAt = -1f;
    private bool[] _playerHitTick = new bool[16];
    private int[] _playerIds = new int[16];

    /// <summary>Interact entry: start wave 1 from the lobby (owner only).</summary>
    public void _StartWave()
    {
        if (!_IsAuthority()) return;
        if (gamePhase != 0) return;
        gamePhase = 1;
        waveNumber = 0;
        _BeginNextWave();
    }

    private void _BeginNextWave()
    {
        waveNumber++;
        _ComputeQuota();
        quotaProgress = 0;
        _waveStartTime = Time.time;
        _waveBossSpawned = false;
        _maintainTimer = 0f;
        _waveEndCheckTimer = 0f;
        if (quotaType == 2)
        {
            // boss wave: the boss spawns immediately at wave start
            _waveBossSpawned = true;
            int slot = _waveNextSlot;
            _waveNextSlot = (_waveNextSlot + 1) % 512;
            AuthoritySpawn(slot, _waveSpawnIdx, 2);
            _waveSpawnIdx = (_waveSpawnIdx + 1) & 7;
            _RegisterBoss(slot);
        }
        RequestSerialization();
    }

    /// <summary>Quota table: wave%3 cycles kill / survive / boss.</summary>
    private void _ComputeQuota()
    {
        int m = waveNumber % 3;
        if (m == 0) { quotaType = 2; quotaTarget = 1; }
        else if (m == 1) { quotaType = 0; quotaTarget = killQuotaBase + (waveNumber - 1) * killQuotaPerWave; }
        else { quotaType = 1; quotaTarget = surviveBase + (waveNumber - 1) * survivePerWave; }
    }

    /// <summary>Trash count the field should hold during this wave.</summary>
    private int _MaintainTarget()
    {
        int t = (quotaType == 2)
            ? bossMaintainBase + (waveNumber - 1) * bossMaintainPerWave
            : maintainBase + (waveNumber - 1) * maintainPerWave;
        if (t > maintainMax) t = maintainMax;
        return t;
    }

    /// <summary>Variant picker hook — M7d replaces this with per-wave weight tables.</summary>
    private int _PickVariant()
    {
        if (waveNumber >= 2 && (_waveNextSlot % 5) == 4) return 1;   // runners from wave 2
        return 0;
    }

    /// <summary>Top the field up to the maintain target (paced, ring-safe).</summary>
    private void _MaintainSpawn()
    {
        int deficit = _MaintainTarget() - _CountAlive();
        if (deficit <= 0) return;
        int n = deficit > spawnBatchMax ? spawnBatchMax : deficit;
        for (int i = 0; i < n; i++)
        {
            AuthoritySpawn(_waveNextSlot, _waveSpawnIdx, _PickVariant());
            _waveNextSlot = (_waveNextSlot + 1) % 512;
            _waveSpawnIdx = (_waveSpawnIdx + 1) & 7;
        }
    }

    private void _RegisterBoss(int slot)
    {
        for (int i = 0; i < 8; i++)
        {
            if (bossSlots[i] < 0)
            {
                bossSlots[i] = slot;
                bossHp[i] = 400;
                return;
            }
        }
    }

    /// <summary>Owner periodic: maintain spawn, quota progress, wave end, shop, player damage.</summary>
    private void _WaveTick()
    {
        if (gamePhase == 1)
        {
            _maintainTimer += Time.deltaTime;
            if (_maintainTimer >= maintainCheckInterval)
            {
                _maintainTimer = 0f;
                _MaintainSpawn();
            }

            // survive quota: elapsed seconds ride the 3Hz snapshot serialization
            if (quotaType == 1)
            {
                int elapsed = (int)(Time.time - _waveStartTime);
                if (elapsed > quotaProgress) quotaProgress = elapsed;
            }

            _waveEndCheckTimer += Time.deltaTime;
            if (_waveEndCheckTimer >= 1f)
            {
                _waveEndCheckTimer = 0f;
                if (_QuotaDone()) { _EndWave(); return; }
            }

            _damageTimer += Time.deltaTime;
            if (_damageTimer >= playerDamageInterval)
            {
                _damageTimer = 0f;
                _DamagePlayersTick();
            }
        }
        else if (gamePhase == 2)
        {
            // shop: next wave when everyone is ready or the timeout expires
            if (Time.time >= _shopUntil || _AllPlayersReady())
            {
                gamePhase = 1;
                _BeginNextWave();
            }
        }
        else if (gamePhase == 3)
        {
            // game over: auto reset & respawn after a grace period
            if (Time.time - _gameOverAt >= gameOverResetSeconds)
            {
                _gameOverAt = -1f;
                _RestartGame();
            }
        }
    }

    private bool _QuotaDone()
    {
        if (quotaType == 0) return quotaProgress >= quotaTarget;
        if (quotaType == 1) return Time.time - _waveStartTime >= quotaTarget;
        _CleanupBossSlots();
        return _waveBossSpawned && _CountBossSlots() == 0;
    }

    private int _CountBossSlots()
    {
        int c = 0;
        for (int i = 0; i < 8; i++)
        {
            if (bossSlots[i] >= 0) c++;
        }
        return c;
    }

    /// <summary>
    /// Wave end: despawn every remaining enemy instantly (no death anim, no kill
    /// events — the quota is already met), revive everyone, enter the shop phase.
    /// Remotes see waveEndRevision and run the same local despawn.
    /// </summary>
    private void _EndWave()
    {
        if (hordeSim != null) hordeSim.DespawnAll();
        waveEndRevision++;
        gamePhase = 2;
        _shopUntil = Time.time + shopSeconds;
        shopReadyBits = 0;
        for (int i = 0; i < 16; i++) playerHp[i] = 100;
        RequestSerialization();
    }

    /// <summary>ShopController ready report (owner only; network or direct local call).</summary>
    [NetworkCallable(22)]
    public void _ReportShopReady()
    {
        if (!NetworkCalling.InNetworkCall && !_IsAuthority()) return;
        if (!_IsAuthority()) return;
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        int id = (caller != null && caller.IsValid()) ? caller.playerId : Networking.LocalPlayer.playerId;
        int idx = id - 1;
        if (idx < 0 || idx >= 16) return;
        shopReadyBits |= (1 << idx);
        RequestSerialization();
    }

    private bool _AllPlayersReady()
    {
        _RefreshPlayers();
        if (_playerCount == 0) return false;
        for (int p = 0; p < _playerCount; p++)
        {
            int idx = _playerIds[p] - 1;
            if (idx < 0 || idx >= 16) continue;
            if ((shopReadyBits & (1 << idx)) == 0) return false;
        }
        return true;
    }

    private int _CountAlive()
    {
        if (hordeSim == null) return 0;
        float[] ms = hordeSim.mirrorState;
        if (ms == null) return 0;
        int alive = 0;
        for (int i = 0; i < ms.Length; i++)
        {
            int s = (int)ms[i];
            if (s >= 1 && s <= 4) alive++;
        }
        return alive;
    }

    private void _CleanupBossSlots()
    {
        float[] ms = hordeSim != null ? hordeSim.mirrorState : null;
        if (ms == null) return;
        for (int i = 0; i < 8; i++)
        {
            int slot = bossSlots[i];
            if (slot < 0) continue;
            int s = (int)ms[slot];
            if (bossHp[i] <= 0 || s < 1 || s > 4) bossSlots[i] = -1;
        }
    }

    /// <summary>
    /// ATTACK-state enemies within range chip the HP of the player they press.
    /// At most one hit per player per tick (a swarm deals 20 dmg/s, not 20×N).
    /// </summary>
    private void _DamagePlayersTick()
    {
        _RefreshPlayers();
        if (_playerCount == 0) return;
        float[] ms = hordeSim != null ? hordeSim.mirrorState : null;
        float[] mx = hordeSim != null ? hordeSim.mirrorPosX : null;
        float[] mz = hordeSim != null ? hordeSim.mirrorPosZ : null;
        if (ms == null || mx == null || mz == null) return;
        for (int p = 0; p < 16; p++) _playerHitTick[p] = false;
        float r2 = playerAttackRange * playerAttackRange;
        for (int i = 0; i < ms.Length; i++)
        {
            if ((int)ms[i] != 3) continue;   // STATE_ATTACK
            for (int p = 0; p < _playerCount; p++)
            {
                if (_playerHitTick[p]) continue;
                float dx = mx[i] - _playerPos[p].x;
                float dz = mz[i] - _playerPos[p].z;
                if (dx * dx + dz * dz < r2)
                {
                    _playerHitTick[p] = true;
                    break;
                }
            }
        }
        for (int p = 0; p < _playerCount; p++)
        {
            if (!_playerHitTick[p]) continue;
            int idx = _playerIds[p] - 1;
            if (idx < 0 || idx >= 16) continue;
            int hp = playerHp[idx];
            if (hp <= 0) continue;           // already down, waiting for the wave clear
            hp -= playerDamagePerTick;
            playerHp[idx] = hp > 0 ? hp : 0;
        }

        // game over: every active player is down
        int downed = 0;
        for (int p = 0; p < _playerCount; p++)
        {
            int idx = _playerIds[p] - 1;
            if (idx >= 0 && idx < 16 && playerHp[idx] <= 0) downed++;
        }
        if (_playerCount > 0 && downed >= _playerCount)
        {
            gamePhase = 3;
            _gameOverAt = Time.time;
            RequestSerialization();
        }
    }

    /// <summary>Interact entry: restart from game over / mid-game back to the lobby (owner only).</summary>
    public void _RestartGame()
    {
        if (!_IsAuthority()) return;
        clearRevision++;
        gamePhase = 0;
        waveNumber = 0;
        quotaType = 0;
        quotaTarget = 0;
        quotaProgress = 0;
        shopReadyBits = 0;
        for (int i = 0; i < 16; i++) playerHp[i] = 100;
        if (hordeSim != null) hordeSim._DebugReset();
        RequestSerialization();
    }

    /// <summary>Downed player calls this on the owner after their client teleported them back.</summary>
    [NetworkCallable(21)]
    public void _ReviveMe()
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        if (caller == null || !caller.IsValid()) return;
        int idx = caller.playerId - 1;
        if (idx < 0 || idx >= 16) return;
        playerHp[idx] = 100;
        RequestSerialization();
    }
}
