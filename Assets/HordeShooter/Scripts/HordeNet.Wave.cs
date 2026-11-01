using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;

/// <summary>
/// HordeNet partial: wave director (owner-authoritative).
/// gamePhase: 0 = lobby, 1 = wave active, 2 = intermission.
/// Spawns are paced ≤16 per chunk (event-ring safe: 32-slot ring, 16-step walk).
/// Every 5th wave ends with a boss (variant 2, owner CPU HP via bossHp[]).
/// Player damage: ATTACK-state enemies within range chip playerHp[] (synced);
/// a downed player is teleported back by their own client and revives via _ReviveMe.
/// </summary>
public partial class HordeNet
{
    [Header("Wave director")]
    public int waveBaseCount = 24;
    public int waveGrowth = 12;
    public int waveMaxCount = 460;
    public float intermissionSeconds = 8f;
    public float spawnChunkInterval = 0.4f;
    public float playerDamageInterval = 0.5f;
    public int playerDamagePerTick = 10;
    public float playerAttackRange = 1.4f;

    private int _waveSpawnRemaining;
    private int _waveNextSlot;
    private int _waveSpawnIdx;
    private bool _waveSpawning;
    private float _waveCheckTimer;
    private float _intermissionUntil;
    private float _damageTimer;
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
        int count = waveBaseCount + (waveNumber - 1) * waveGrowth;
        if (count > waveMaxCount) count = waveMaxCount;
        _waveSpawnRemaining = count;
        _waveSpawning = true;
        RequestSerialization();
        _WaveSpawnChunk();
    }

    /// <summary>Paced spawn: ≤16 per call (ring-safe), chained via delayed event.</summary>
    public void _WaveSpawnChunk()
    {
        if (!_waveSpawning || !_IsAuthority()) return;
        int n = _waveSpawnRemaining;
        if (n > 16) n = 16;
        for (int i = 0; i < n; i++)
        {
            int variant = 0;
            if (waveNumber >= 2 && (_waveNextSlot % 5) == 4) variant = 1;      // runners from wave 2
            // boss: the very last enemy of every 5th wave (remaining counts down
            // across the loop, so compare against the per-iteration remainder)
            if (waveNumber % 5 == 0 && _waveSpawnRemaining - i == 1) variant = 2;
            AuthoritySpawn(_waveNextSlot, _waveSpawnIdx, variant);
            if (variant == 2) _RegisterBoss(_waveNextSlot);
            _waveNextSlot = (_waveNextSlot + 1) % 512;
            _waveSpawnIdx = (_waveSpawnIdx + 1) & 7;
        }
        _waveSpawnRemaining -= n;
        if (_waveSpawnRemaining > 0)
            SendCustomEventDelayedSeconds(nameof(_WaveSpawnChunk), spawnChunkInterval);
        else
            _waveSpawning = false;
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

    /// <summary>Owner periodic: wave end detection + player damage. Called from Update.</summary>
    private void _WaveTick()
    {
        if (gamePhase == 1)
        {
            if (!_waveSpawning)
            {
                _waveCheckTimer += Time.deltaTime;
                if (_waveCheckTimer >= 1f)
                {
                    _waveCheckTimer = 0f;
                    _CleanupBossSlots();
                    if (_CountAlive() == 0)
                    {
                        gamePhase = 2;
                        _intermissionUntil = Time.time + intermissionSeconds;
                        // wave cleared: revive every downed player
                        for (int i = 0; i < 16; i++) playerHp[i] = 100;
                        RequestSerialization();
                    }
                }
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
            if (Time.time >= _intermissionUntil)
            {
                gamePhase = 1;
                _BeginNextWave();
            }
        }
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
        for (int i = 0; i < 16; i++) playerHp[i] = 100;
        _waveSpawning = false;
        _waveSpawnRemaining = 0;
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
