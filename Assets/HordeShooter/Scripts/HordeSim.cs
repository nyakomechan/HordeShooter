using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
using imaginantia.Compeito;

/// <summary>
/// GPU-driven horde simulation driver (M1 core).
/// Owns the EnemySim Compeito material and the enemy render material; every frame it
/// draws the previous sim step (extrapolated), then advances the simulation with 3
/// Compeito dispatches. Udon does zero per-enemy work: all 512 enemies live in
/// 32x16 RenderTextures.
///
/// Slot state enums (mirror of EnemySim.compeito):
///   0 INACTIVE, 1 SPAWNING, 2 CHASE, 3 ATTACK, 4 STAGGER, 5 DYING
/// Command types: 1 DAMAGE, 2 KILL, 3 SPAWN.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class HordeSim : UdonSharpBehaviour
{
    [Header("Wiring (set by HordeSceneSetup)")]
    public Material simMaterial;      // generated from EnemySim.compeito
    public Material enemyMaterial;    // instance of HordeShooter/EnemyRender
    public Mesh enemyMesh;            // built by EnemyMeshBuilder
    public Transform[] spawnPoints;   // up to 16
    public HordeNet hordeNet;         // downed players (playerHp <= 0) are excluded from targeting

    [Header("Simulation config")]
    public int slotCount = 512;
    public int slotW = 32;
    public int slotH = 16;
    public float moveSpeed = 3.2f;
    public float chaseRange = 18f;
    public float attackRange = 1.3f;
    public float attackDuration = 0.7f;
    public float staggerDuration = 0.18f;
    public float spawnDuration = 0.8f;
    public float deathDuration = 2.5f;
    public float separationRadius = 0.9f;
    public float separationStrength = 2.2f;
    public float groundY = 0f;
    public float spawnJitter = 1.6f;
    public float mirrorInterval = 0.1f;

    [Header("Dummy matrix spread (covers the arena for batch culling)")]
    public Vector3 dummyCenter = Vector3.zero;
    public Vector2 dummyExtent = new Vector2(56f, 56f);

    [Header("Debug (driven via SetProgramVariable + SendCustomEvent)")]
    public int debugSpawnCount;
    public int debugSpawnVariant;
    public bool profileSimStep;

    // ---- GPU resources ----
    private RenderTexture _posA;
    private RenderTexture _posB;
    private RenderTexture _stateA;
    private RenderTexture _stateB;
    private RenderTexture _metaA;
    private RenderTexture _metaB;
    private RenderTexture _hitResultRT;
    private Texture2D _posInitTex;
    private Texture2D _stateInitTex;
    private Texture2D _metaInitTex;
    private Texture2D _playerTex;
    private Texture2D _spawnTex;
    private Texture2D _cmdTex;
    private Texture2D _corrTex;
    private Texture2D _rayOriginTex;
    private Texture2D _rayDirTex;
    private int _kSimPos = -1;
    private int _kSimState = -1;
    private int _kSimMeta = -1;
    private int _kHitTest = -1;
    private bool _readA = true;   // true = A is the next "In" set (= last written)
    private bool _initialized;

    // ---- CPU-side textures ----
    private const int MAX_PLAYERS = 16;
    private const int MAX_SPAWNS = 16;
    private const int MAX_CMDS = 8;
    private const int MAX_CORR = 24;
    private Color[] _playerColors;
    private Color[] _spawnColors;
    private Color[] _cmdColors;
    private Color[] _corrColors;
    private VRCPlayerApi[] _playerBuf;

    // ---- command queue (CPU -> GPU, drained 8/frame) ----
    private int[] _cmdType;
    private int[] _cmdSlot;
    private float[] _cmdP0;
    private float[] _cmdP1;
    private int _cmdHead;
    private int _cmdTail;
    private int _cmdCount;
    private const int CMD_CAP = 1024;

    // ---- render ----
    private Matrix4x4[] _dummyMatrices;
    private float _lastSimTime;

    // ---- mirror (async readback, see HordeSim.Readback.cs) ----
    private Color[] _posMirrorBuf;
    private Color[] _stateMirrorBuf;
    private Color[] _hitMirrorBuf;
    private bool _mirrorAwaitPos;
    private bool _mirrorAwaitState;
    private bool _hitAwait;
    private float _mirrorTimer;
    private float _awaitStart;      // watchdog: readback callbacks can stall in the editor
    private const float AWAIT_TIMEOUT = 0.5f;

    /// <summary>Readback mirrors, refreshed ~10Hz. Index = slot.</summary>
    [System.NonSerialized] public float[] mirrorPosX;
    [System.NonSerialized] public float[] mirrorPosY;
    [System.NonSerialized] public float[] mirrorPosZ;
    [System.NonSerialized] public float[] mirrorYaw;
    [System.NonSerialized] public float[] mirrorHp;
    [System.NonSerialized] public float[] mirrorState;

    // ---- hitscan (rays queued by GunController, resolved on GPU) ----
    private Color[] _rayOriginColors;
    private Color[] _rayDirColors;
    private int _rayCount;          // rays queued for the next HitTest dispatch
    public const int MAX_RAYS = 16;
    private UdonSharpBehaviour _hitListener;
    private string _hitListenerEvent = "_OnHitResults";

    /// <summary>Hit results of the latest resolved ray batch. Index = ray id.</summary>
    [System.NonSerialized] public float[] hitRayDist;
    [System.NonSerialized] public float[] hitRaySlot;
    [System.NonSerialized] public int hitBatchId;

    private int _nextDebugSlot;
    private bool _despawnAllPending;
    private float _profAccum;
    private int _profFrames;
    private System.Diagnostics.Stopwatch _profWatch = new System.Diagnostics.Stopwatch();

    void Start()
    {
        Init();
    }

    public void Init()
    {
        if (_initialized) return;

        if (simMaterial == null || enemyMaterial == null || enemyMesh == null)
        {
            Debug.LogError("[HordeSim] simMaterial / enemyMaterial / enemyMesh not assigned");
            return;
        }

        _kSimPos = simMaterial.FindPass("SimPos");
        _kSimState = simMaterial.FindPass("SimState");
        _kSimMeta = simMaterial.FindPass("SimMeta");
        _kHitTest = simMaterial.FindPass("HitTest");
        if (_kSimPos < 0 || _kSimState < 0 || _kSimMeta < 0 || _kHitTest < 0)
        {
            Debug.LogError("[HordeSim] EnemySim kernels not found (SimPos/SimState/SimMeta/HitTest)");
            return;
        }

        _posA = Compeito.CreateRT("HordePosA", slotW, slotH);
        _posB = Compeito.CreateRT("HordePosB", slotW, slotH);
        _stateA = Compeito.CreateRT("HordeStateA", slotW, slotH);
        _stateB = Compeito.CreateRT("HordeStateB", slotW, slotH);
        _metaA = Compeito.CreateRT("HordeMetaA", slotW, slotH);
        _metaB = Compeito.CreateRT("HordeMetaB", slotW, slotH);

        _playerTex = MakeCpuTex(MAX_PLAYERS);
        _spawnTex = MakeCpuTex(MAX_SPAWNS);
        _cmdTex = MakeCpuTex(MAX_CMDS);
        _corrTex = MakeCpuTex(MAX_CORR);
        _rayOriginTex = MakeCpuTex(MAX_RAYS);
        _rayDirTex = MakeCpuTex(MAX_RAYS);
        _playerColors = new Color[MAX_PLAYERS];
        _spawnColors = new Color[MAX_SPAWNS];
        _cmdColors = new Color[MAX_CMDS];
        _corrColors = new Color[MAX_CORR];
        _rayOriginColors = new Color[MAX_RAYS];
        _rayDirColors = new Color[MAX_RAYS];
        for (int i = 0; i < MAX_RAYS; i++)
        {
            _rayOriginColors[i] = new Color(0f, 0f, 0f, 0f);
            // dir.w = per-ray hit radius (0 = default _HitRadius); -1 = inactive
            _rayDirColors[i] = new Color(0f, 0f, 0f, -1f);
        }
        _rayOriginTex.SetPixels(_rayOriginColors);
        _rayOriginTex.Apply(false);
        _rayDirTex.SetPixels(_rayDirColors);
        _rayDirTex.Apply(false);
        _hitResultRT = Compeito.CreateRT("HordeHitResult", MAX_RAYS, 1);
        _hitMirrorBuf = new Color[MAX_RAYS];
        hitRayDist = new float[MAX_RAYS];
        hitRaySlot = new float[MAX_RAYS];
        for (int i = 0; i < MAX_RAYS; i++) { hitRayDist[i] = -1f; hitRaySlot[i] = -1f; }
        for (int i = 0; i < MAX_CORR; i++) _corrColors[i] = new Color(-1f, 0f, 0f, 0f);
        _corrTex.SetPixels(_corrColors);
        _corrTex.Apply(false);
        _playerBuf = new VRCPlayerApi[MAX_PLAYERS];

        // Initial slot data: everything INACTIVE far below the arena.
        _posInitTex = new Texture2D(slotW, slotH, TextureFormat.RGBAFloat, false);
        _stateInitTex = new Texture2D(slotW, slotH, TextureFormat.RGBAFloat, false);
        _metaInitTex = new Texture2D(slotW, slotH, TextureFormat.RGBAFloat, false);
        _posInitTex.filterMode = FilterMode.Point;
        _stateInitTex.filterMode = FilterMode.Point;
        _metaInitTex.filterMode = FilterMode.Point;
        Color[] init = new Color[slotW * slotH];
        for (int i = 0; i < init.Length; i++) init[i] = new Color(0f, -1000f, 0f, 0f);
        _posInitTex.SetPixels(init);
        _posInitTex.Apply(false);
        for (int i = 0; i < init.Length; i++) init[i] = new Color(0f, 0f, 0f, 0f);
        _stateInitTex.SetPixels(init);
        _stateInitTex.Apply(false);
        for (int i = 0; i < init.Length; i++) init[i] = new Color(0f, 0f, -1f, 0f);
        _metaInitTex.SetPixels(init);
        _metaInitTex.Apply(false);
        ResetAll();

        // Sim material constants.
        simMaterial.SetInt("_SlotCount", slotCount);
        simMaterial.SetInt("_SlotW", slotW);
        simMaterial.SetFloat("_MoveSpeed", moveSpeed);
        simMaterial.SetFloat("_ChaseRange", chaseRange);
        simMaterial.SetFloat("_AttackRange", attackRange);
        simMaterial.SetFloat("_AttackDuration", attackDuration);
        simMaterial.SetFloat("_StaggerDuration", staggerDuration);
        simMaterial.SetFloat("_SpawnDuration", spawnDuration);
        simMaterial.SetFloat("_DeathDuration", deathDuration);
        simMaterial.SetFloat("_SeparationRadius", separationRadius);
        simMaterial.SetFloat("_SeparationStrength", separationStrength);
        simMaterial.SetFloat("_MaxSeparation", 1.4f);
        simMaterial.SetFloat("_GroundY", groundY);
        simMaterial.SetFloat("_SpawnJitter", spawnJitter);
        simMaterial.SetFloat("_HitRadius", 0.45f);
        simMaterial.SetTexture("_PlayerTex", _playerTex);
        simMaterial.SetTexture("_SpawnTex", _spawnTex);
        simMaterial.SetTexture("_CmdTex", _cmdTex);
        simMaterial.SetTexture("_CorrectionTex", _corrTex);
        simMaterial.SetTexture("_RayOriginTex", _rayOriginTex);
        simMaterial.SetTexture("_RayDirTex", _rayDirTex);

        // Render material constants.
        enemyMaterial.SetInt("_SlotW", slotW);
        enemyMaterial.SetInt("_SlotCount", slotCount);
        enemyMaterial.SetFloat("_MoveSpeed", moveSpeed);
        enemyMaterial.SetFloat("_AttackDuration", attackDuration);
        enemyMaterial.SetFloat("_StaggerDuration", staggerDuration);
        enemyMaterial.SetFloat("_SpawnDuration", spawnDuration);
        enemyMaterial.SetFloat("_DeathDuration", deathDuration);

        UpdateSpawnTex();

        // Dummy matrices spread across the arena so the instanced batch is never
        // frustum-culled as a whole; the shader ignores them for placement.
        _dummyMatrices = new Matrix4x4[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            float fx = (float)(i % slotW) / (float)(slotW - 1) - 0.5f;
            float fz = (float)(i / slotW) / (float)(slotH - 1) - 0.5f;
            Vector3 p = dummyCenter + new Vector3(fx * dummyExtent.x, 1f, fz * dummyExtent.y);
            _dummyMatrices[i] = Matrix4x4.TRS(p, Quaternion.identity, Vector3.one);
        }

        _posMirrorBuf = new Color[slotCount];
        _stateMirrorBuf = new Color[slotCount];
        mirrorPosX = new float[slotCount];
        mirrorPosY = new float[slotCount];
        mirrorPosZ = new float[slotCount];
        mirrorYaw = new float[slotCount];
        mirrorHp = new float[slotCount];
        mirrorState = new float[slotCount];

        _cmdType = new int[CMD_CAP];
        _cmdSlot = new int[CMD_CAP];
        _cmdP0 = new float[CMD_CAP];
        _cmdP1 = new float[CMD_CAP];

        _readA = true;
        _lastSimTime = Time.time;
        _initialized = true;
        Debug.Log("[HordeSim] initialized: slots=" + slotCount + " (" + slotW + "x" + slotH + ")");
    }

    private static Texture2D MakeCpuTex(int width)
    {
        Texture2D t = new Texture2D(width, 1, TextureFormat.RGBAFloat, false);
        t.filterMode = FilterMode.Point;
        t.wrapMode = TextureWrapMode.Clamp;
        return t;
    }

    void LateUpdate()
    {
        SimStep();
    }

    /// <summary>One frame: draw previous step extrapolated, then advance the sim.</summary>
    public void SimStep()
    {
        if (!_initialized) return;
        if (profileSimStep)
        {
            _profWatch.Reset();
            _profWatch.Start();
        }
        float dt = Time.deltaTime;

        // 1) Draw the last-written state, extrapolated forward by the time since it stepped.
        float extrap = Time.time - _lastSimTime;
        if (extrap < 0f) extrap = 0f;
        if (extrap > 0.25f) extrap = 0.25f;
        RenderTexture posCur = _readA ? _posA : _posB;
        RenderTexture stateCur = _readA ? _stateA : _stateB;
        RenderTexture metaCur = _readA ? _metaA : _metaB;
        enemyMaterial.SetTexture("_PosTex", posCur);
        enemyMaterial.SetTexture("_StateTex", stateCur);
        enemyMaterial.SetTexture("_MetaTex", metaCur);
        enemyMaterial.SetFloat("_Extrapolate", extrap);
        VRCGraphics.DrawMeshInstanced(enemyMesh, 0, enemyMaterial, _dummyMatrices, slotCount);

        // 2) Advance the simulation: In = current, Out = the other set.
        UpdatePlayerTex();
        FlushCmds();
        simMaterial.SetFloat("_DeltaTime", dt);
        // one-frame global despawn flag (wave end): SimState returns every slot
        // to INACTIVE instantly — no death anim, no per-slot commands needed.
        simMaterial.SetFloat("_DespawnAll", _despawnAllPending ? 1f : 0f);
        _despawnAllPending = false;
        simMaterial.SetTexture("_PosIn", posCur);
        simMaterial.SetTexture("_StateIn", stateCur);
        simMaterial.SetTexture("_MetaIn", metaCur);
        Compeito.Dispatch(simMaterial, _kSimPos, _readA ? _posB : _posA);
        Compeito.Dispatch(simMaterial, _kSimState, _readA ? _stateB : _stateA);
        Compeito.Dispatch(simMaterial, _kSimMeta, _readA ? _metaB : _metaA);
        _readA = !_readA;
        _lastSimTime = Time.time;

        // 2b) Hitscan: resolve queued rays on the GPU against the fresh state.
        if (_rayCount > 0 && !_hitAwait)
        {
            _rayOriginTex.SetPixels(_rayOriginColors);
            _rayOriginTex.Apply(false);
            _rayDirTex.SetPixels(_rayDirColors);
            _rayDirTex.Apply(false);
            Compeito.Dispatch(simMaterial, _kHitTest, _hitResultRT);
            _hitAwait = true;
            _rayCount = 0;
            if (!_mirrorAwaitPos && !_mirrorAwaitState)
                VRCAsyncGPUReadback.Request(_hitResultRT, 0, (IUdonEventReceiver)this);
        }

        // 3) Mirror readback (chained pos -> state -> hit, see HordeSim.Readback.cs).
        // Watchdog: async readback callbacks can stall silently; re-arm after a timeout.
        if ((_mirrorAwaitPos || _mirrorAwaitState || _hitAwait)
            && Time.time - _awaitStart > AWAIT_TIMEOUT)
        {
            _mirrorAwaitPos = false;
            _mirrorAwaitState = false;
            _hitAwait = false;
            _mirrorTimer = mirrorInterval;
        }
        _mirrorTimer += dt;
        if (_mirrorTimer >= mirrorInterval && !_mirrorAwaitPos && !_mirrorAwaitState && !_hitAwait)
        {
            _mirrorTimer = 0f;
            _mirrorAwaitPos = true;
            _awaitStart = Time.time;
            VRCAsyncGPUReadback.Request(_readA ? _posA : _posB, 0, (IUdonEventReceiver)this);
        }

        if (profileSimStep)
        {
            _profWatch.Stop();
            _profAccum += (float)_profWatch.Elapsed.TotalMilliseconds;
            _profFrames++;
            if (_profFrames >= 120)
            {
                Debug.Log("[HordeSim] avg SimStep Udon cost over 120 frames: "
                    + (_profAccum / _profFrames).ToString("F3") + " ms");
                _profAccum = 0f;
                _profFrames = 0;
            }
        }
    }

    // ---- debug entry points (parameterless; fields set via SetProgramVariable) ----

    public void _DebugSpawn()
    {
        DebugSpawnRing(debugSpawnCount, debugSpawnVariant);
    }

    public void _DebugReset()
    {
        ResetAll();
    }

    private int _debugRaySlot;

    /// <summary>
    /// Debug: queue 3 test rays at the first live slot — A: straight down with
    /// radius 1.2, B: offset +0.8 m in x with radius 1.2 (default 0.45 misses),
    /// C: offset +2.0 m with the default radius (must miss even with mirror
    /// staleness drift). Results land in hitRaySlot[0..2]; the targeted slot is
    /// left in _debugRaySlot.
    /// </summary>
    public void _DebugQueueTestRays()
    {
        if (!_initialized) return;
        int slot = -1;
        for (int i = 0; i < slotCount; i++)
        {
            int s = (int)mirrorState[i];
            if (s >= 1 && s <= 4) { slot = i; break; }
        }
        _debugRaySlot = slot;
        if (slot < 0) return;
        float x = mirrorPosX[slot];
        float z = mirrorPosZ[slot];
        QueueRayRadius(new Vector3(x, 8f, z), Vector3.down, 200f, 1.2f);
        QueueRayRadius(new Vector3(x + 0.8f, 8f, z), Vector3.down, 200f, 1.2f);
        QueueRayRadius(new Vector3(x + 2.0f, 8f, z), Vector3.down, 200f, 0f);
    }

    public void _DebugLogStatus()
    {
        int alive = 0;
        for (int i = 0; i < slotCount; i++)
        {
            int s = (int)mirrorState[i];
            if (s >= 1 && s <= 4) alive++;
        }
        Debug.Log("[HordeSim] status: alive=" + alive
            + " smoothFrameMs=" + (Time.smoothDeltaTime * 1000f).ToString("F2")
            + " cmdQueue=" + _cmdCount);
    }

    private void UpdatePlayerTex()
    {
        for (int i = 0; i < MAX_PLAYERS; i++) _playerColors[i] = new Color(0f, 0f, 0f, 0f);
        VRCPlayerApi[] players = VRCPlayerApi.GetPlayers(_playerBuf);
        int n = players.Length;
        if (n > MAX_PLAYERS) n = MAX_PLAYERS;
        // downed players (playerHp <= 0) are invisible to the sim's target
        // selection: w=0 makes NearestPlayer skip them and drops them as
        // targets of enemies already chasing them.
        int[] hp = hordeNet != null ? hordeNet.playerHp : null;
        for (int i = 0; i < n; i++)
        {
            VRCPlayerApi p = players[i];
            if (p == null) continue;
            if (!p.IsValid()) continue;
            Vector3 pos = p.GetPosition();
            float active = 1f;
            if (hp != null)
            {
                int idx = p.playerId - 1;
                if (idx >= 0 && idx < 16 && (int)hp[idx] <= 0) active = 0f;
            }
            _playerColors[i] = new Color(pos.x, pos.y, pos.z, active);
        }
        _playerTex.SetPixels(_playerColors);
        _playerTex.Apply(false);
    }

    public void UpdateSpawnTex()
    {
        for (int i = 0; i < MAX_SPAWNS; i++) _spawnColors[i] = new Color(0f, 0f, 0f, 0f);
        if (spawnPoints != null)
        {
            int n = spawnPoints.Length;
            if (n > MAX_SPAWNS) n = MAX_SPAWNS;
            for (int i = 0; i < n; i++)
            {
                Transform t = spawnPoints[i];
                if (t == null) continue;
                Vector3 p = t.position;
                _spawnColors[i] = new Color(p.x, p.y, p.z, 1f);
            }
        }
        _spawnTex.SetPixels(_spawnColors);
        _spawnTex.Apply(false);
        if (_initialized) simMaterial.SetTexture("_SpawnTex", _spawnTex);
    }

    // ---- flow field integration (set by FlowFieldBridge) ----

    public void SetFlowGrid(int gsx, int gsy, int gsz, Vector3 origin, float cellSize, Texture2D voxelToLeafTex)
    {
        if (!_initialized) return;
        simMaterial.SetInt("_GSX", gsx);
        simMaterial.SetInt("_GSY", gsy);
        simMaterial.SetInt("_GSZ", gsz);
        simMaterial.SetVector("_GridOrigin", origin);
        simMaterial.SetFloat("_CellSize", cellSize);
        simMaterial.SetTexture("_VoxelToLeaf", voxelToLeafTex);
    }

    public void SetFlowField(RenderTexture flowRT)
    {
        if (!_initialized) return;
        simMaterial.SetTexture("_FlowField", flowRT);
        simMaterial.SetFloat("_UseFlow", 1f);
    }

    // ---- correction texture (fed by HordeNet's remote snapshot application) ----

    private bool _corrDirty;

    public void BeginCorrections()
    {
        for (int i = 0; i < MAX_CORR; i++) _corrColors[i] = new Color(-1f, 0f, 0f, 0f);
        _corrDirty = true;
    }

    public void PushCorrection(int slot, Vector3 pos)
    {
        for (int i = 0; i < MAX_CORR; i++)
        {
            if (_corrColors[i].r < -0.5f)
            {
                _corrColors[i] = new Color((float)slot, pos.x, pos.y, pos.z);
                _corrDirty = true;
                return;
            }
        }
    }

    public void CommitCorrections()
    {
        if (!_corrDirty || !_initialized) return;
        _corrTex.SetPixels(_corrColors);
        _corrTex.Apply(false);
        _corrDirty = false;
    }

    // ---- command queue ----

    public void QueueSpawn(int slot, int spawnIdx, int variant)
    {
        PushCmd(3, slot, (float)spawnIdx, (float)variant);
    }

    public void QueueDamage(int slot, float dmg)
    {
        PushCmd(1, slot, dmg, 0f);
    }

    public void QueueKill(int slot)
    {
        PushCmd(2, slot, 0f, 0f);
    }

    /// <summary>
    /// Late-joiner catch-up: activate an inactive slot at its corrected position.
    /// cmd p0 = target state (1-4); position comes from the correction texture
    /// (initial slot pos is far below the arena, so the correction snaps).
    /// </summary>
    public void QueueWake(int slot, int state)
    {
        PushCmd(4, slot, (float)state, 0f);
    }

    /// <summary>
    /// Wave-end despawn: every slot returns to INACTIVE on the next sim step
    /// (one _DespawnAll flag frame in EnemySim — no death anim, no kill events).
    /// </summary>
    public void DespawnAll()
    {
        _despawnAllPending = true;
    }

    // ---- hitscan ray queue ----

    /// <summary>Queues a hitscan ray with the default hit radius (max 16 per frame; extras are dropped).</summary>
    public void QueueRay(Vector3 origin, Vector3 dir, float wallDist)
    {
        QueueRayRadius(origin, dir, wallDist, 0f);
    }

    /// <summary>
    /// Queues a hitscan ray with a custom hit radius (drill etc.).
    /// radius 0 = default _HitRadius; the radius rides _RayDirTex.w (was the
    /// active flag — inactive rays are now marked w = -1).
    /// Returns the ray id (index into hitRay*) or -1 when the batch is full.
    /// </summary>
    public int QueueRayRadius(Vector3 origin, Vector3 dir, float wallDist, float radius)
    {
        if (_rayCount >= MAX_RAYS) return -1;
        _rayOriginColors[_rayCount] = new Color(origin.x, origin.y, origin.z, wallDist);
        _rayDirColors[_rayCount] = new Color(dir.x, dir.y, dir.z, radius);
        _rayCount++;
        return _rayCount - 1;
    }

    /// <summary>Registers the behaviour notified when a ray batch resolves.</summary>
    public void SetHitListener(UdonSharpBehaviour listener)
    {
        _hitListener = listener;
    }

    private void PushCmd(int type, int slot, float p0, float p1)
    {
        if (_cmdCount >= CMD_CAP) return;
        _cmdType[_cmdTail] = type;
        _cmdSlot[_cmdTail] = slot;
        _cmdP0[_cmdTail] = p0;
        _cmdP1[_cmdTail] = p1;
        _cmdTail = (_cmdTail + 1) % CMD_CAP;
        _cmdCount++;
    }

    private void FlushCmds()
    {
        for (int i = 0; i < MAX_CMDS; i++) _cmdColors[i] = new Color(0f, 0f, 0f, 0f);
        int n = _cmdCount;
        if (n > MAX_CMDS) n = MAX_CMDS;
        for (int i = 0; i < n; i++)
        {
            _cmdColors[i] = new Color((float)_cmdType[_cmdHead], (float)_cmdSlot[_cmdHead], _cmdP0[_cmdHead], _cmdP1[_cmdHead]);
            _cmdHead = (_cmdHead + 1) % CMD_CAP;
        }
        _cmdCount -= n;
        _cmdTex.SetPixels(_cmdColors);
        _cmdTex.Apply(false);
    }

    // ---- test / debug APIs ----

    /// <summary>Spawns enemies round-robin over the spawn points (M1 test helper).</summary>
    public void DebugSpawnRing(int count, int variant)
    {
        if (!_initialized) Init();
        int spawns = 0;
        if (spawnPoints != null) spawns = spawnPoints.Length;
        if (spawns > MAX_SPAWNS) spawns = MAX_SPAWNS;
        if (spawns <= 0)
        {
            Debug.LogError("[HordeSim] no spawn points assigned");
            return;
        }
        for (int n = 0; n < count; n++)
        {
            QueueSpawn(_nextDebugSlot, n % spawns, variant);
            _nextDebugSlot = (_nextDebugSlot + 1) % slotCount;
        }
    }

    /// <summary>Instantly clears every slot back to INACTIVE (re-blits init data).</summary>
    public void ResetAll()
    {
        if (!_initialized)
        {
            if (_posInitTex == null) return;
        }
        RenderTexture posIn = _readA ? _posA : _posB;
        RenderTexture stateIn = _readA ? _stateA : _stateB;
        RenderTexture metaIn = _readA ? _metaA : _metaB;
        if (posIn == null) return;
        VRCGraphics.Blit(_posInitTex, posIn);
        VRCGraphics.Blit(_stateInitTex, stateIn);
        VRCGraphics.Blit(_metaInitTex, metaIn);
        _cmdCount = 0;
        _cmdHead = 0;
        _cmdTail = 0;
        _nextDebugSlot = 0;
    }
}
