// Shared per-instance fetch + procedural animation for EnemyRender.shader.
// Instance data lives in the HordeSim RenderTextures; unity_InstanceID selects
// the slot — either directly (legacy single-mesh draw) or through the per-variant
// slot-list texture (_UseSlotList > 0.5, M7d variant-batched draws).
// Included by both the color pass and the ShadowCaster pass (UnityCG.cginc must be
// included before this file).

Texture2D<float4> _PosTex;     // xyz world pos, w yaw
Texture2D<float4> _StateTex;   // x hp, y state, z state timer, w hit flash remaining
Texture2D<float4> _MetaTex;    // x speed, y variant, z target idx, w anim phase
Texture2D<float4> _SlotList;   // 512x1: r = slot index for this instance (variant-batched draws)

int _SlotW;
int _SlotCount;
float _Extrapolate;
float _DebugUseMatrix;   // 1 = render at the instance (dummy) matrix, bypassing texture data
float _DebugForceScale;  // 1 = force scale 1 (bypass state/LOD collapse)
float _UseSlotList;      // 1 = unity_InstanceID indexes _SlotList instead of the slot directly
float _MoveSpeed;
float _AttackDuration;
float _StaggerDuration;
float _SpawnDuration;
float _DeathDuration;

#define ES_INACTIVE 0
#define ES_SPAWNING 1
#define ES_CHASE    2
#define ES_ATTACK   3
#define ES_STAGGER  4
#define ES_DYING    5

// variant ids (mirror of EnemySim.compeito)
#define ES_V_WALKER  0
#define ES_V_RUNNER  1
#define ES_V_BOSS    2
#define ES_V_BRUTE   3
#define ES_V_STALKER 4
#define ES_V_SWARM   5
#define ES_V_BOMBER  6
#define ES_V_ELITE   7

float EsHash(int n) { return frac(sin((float)n * 127.1) * 43758.5453); }

// Instance slot index; 0 in non-instancing variants (unused in practice because the
// material has instancing enabled, but the variant must still compile).
int EsInstanceSlot()
{
#if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
    if (_UseSlotList > 0.5)
        return (int)_SlotList.Load(int3((int)unity_InstanceID, 0, 0)).r;
    return (int)unity_InstanceID;
#else
    return 0;
#endif
}

// Per-variant render scale (mirror of EnemySim.VariantScale).
float EsVariantScale(int v)
{
    if (v == ES_V_BOSS) return 1.9;
    if (v == ES_V_RUNNER) return 0.9;
    if (v == ES_V_BRUTE) return 1.4;
    if (v == ES_V_STALKER) return 0.8;
    if (v == ES_V_SWARM) return 0.6;
    if (v == ES_V_BOMBER) return 1.1;
    if (v == ES_V_ELITE) return 1.2;
    return 1.0;
}

// Per-variant base tint (Minecraft-ish low-poly palette).
float3 EsVariantTint(int v, float hash)
{
    float3 c;
    if (v == ES_V_RUNNER) c = float3(1.05, 1.05, 0.55);        // sickly yellow-green
    else if (v == ES_V_BOSS) c = float3(1.3, 0.5, 0.45);       // red
    else if (v == ES_V_BRUTE) c = float3(0.75, 0.4, 0.3);      // dark red-brown
    else if (v == ES_V_STALKER) c = float3(0.75, 0.5, 1.1);    // purple
    else if (v == ES_V_SWARM) c = float3(1.1, 1.1, 1.0);       // pale
    else if (v == ES_V_BOMBER) c = float3(1.25, 0.75, 0.3);    // orange
    else if (v == ES_V_ELITE) c = float3(0.5, 0.7, 1.25);      // blue
    else c = float3(0.9, 0.95, 0.8);                           // walker: gray-green
    return c * (0.85 + 0.3 * hash);
}

// Computes the animated world-space vertex position/normal for one instance.
void EsAnimate(float3 local, float3 normal, float armMask, float headMask, int slot,
    out float3 worldPos, out float3 worldNormal, out float3 tint, out float flash)
{
    uint uSlot = (uint)slot;
    uint uW = (uint)_SlotW;
    int2 texel = int2(uSlot % uW, uSlot / uW);
    float4 pd = _PosTex.Load(int3(texel, 0));
    float4 sd = _StateTex.Load(int3(texel, 0));
    float4 md = _MetaTex.Load(int3(texel, 0));

    float3 pos = pd.xyz;
    float yaw = pd.w;
    int state = (int)round(sd.y);
    float timer = sd.z;
    float flashT = sd.w;
    float speed = md.x;
    int variant = (int)round(md.y);
    float phase = md.w;

    float3 p = local;
    float scale = 1.0;
    float speedFactor = saturate(speed / max(_MoveSpeed, 0.001));

    // walk bob + limb secondary motion
    p.y += abs(sin(phase)) * 0.05 * speedFactor;
    p.y += armMask * sin(phase * 2.0 + 1.3) * 0.035 * speedFactor;
    p.y += headMask * sin(phase + 2.1) * 0.02 * speedFactor;

    // lean into movement (shear around the feet)
    p.z += p.y * speedFactor * 0.10;

    if (state == ES_ATTACK)
        p.z += sin(saturate(timer / _AttackDuration) * 3.14159) * 0.28;
    else if (state == ES_STAGGER)
        p.z -= sin(saturate(timer / _StaggerDuration) * 3.14159) * 0.14;
    else if (state == ES_SPAWNING)
    {
        float t = saturate(timer / _SpawnDuration);
        p.y -= (1.0 - t) * 1.5;
        scale = 0.2 + 0.8 * t;
    }
    else if (state == ES_DYING)
    {
        float t = saturate(timer / _DeathDuration);
        float fall = saturate(t * 3.0);
        float ang = fall * 1.5708;
        float cy = cos(ang);
        float sy = sin(ang);
        p = float3(p.x, p.y * cy - p.z * sy, p.y * sy + p.z * cy);
        p.y -= saturate((t - 0.55) / 0.45) * 1.3;
    }

    // variant scale
    scale *= EsVariantScale(variant);

    // distance LOD: shrink beyond 80m, collapse beyond 120m
    float camDist = distance(pos, _WorldSpaceCameraPos);
    if (camDist > 80.0) scale *= saturate(1.0 - (camDist - 80.0) / 40.0);

    // inactive slots collapse to nothing
    if (state == ES_INACTIVE) scale = 0.0;
    if (_DebugForceScale > 0.5) scale = 1.0;

    // yaw rotation (forward = +Z at yaw 0)
    float syaw = sin(yaw);
    float cyaw = cos(yaw);
    float3 r = float3(p.x * cyaw + p.z * syaw, p.y, -p.x * syaw + p.z * cyaw);

    // extrapolate between sim steps
    pos += float3(syaw, 0, cyaw) * (speed * _Extrapolate);

    worldPos = pos + r * scale;
    worldNormal = normalize(float3(normal.x * cyaw + normal.z * syaw, normal.y, -normal.x * syaw + normal.z * cyaw));

    // per-variant tint with a small per-instance hash variation
    tint = EsVariantTint(variant, EsHash(slot * 7 + 13));
    flash = saturate(flashT * 6.0);
}
