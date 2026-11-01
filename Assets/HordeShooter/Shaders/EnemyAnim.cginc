// Shared per-instance fetch + procedural animation for EnemyRender.shader.
// Instance data lives in the HordeSim RenderTextures; unity_InstanceID selects the slot.
// Included by both the color pass and the ShadowCaster pass (UnityCG.cginc must be
// included before this file).

Texture2D<float4> _PosTex;     // xyz world pos, w yaw
Texture2D<float4> _StateTex;   // x hp, y state, z state timer, w hit flash remaining
Texture2D<float4> _MetaTex;    // x speed, y variant, z target idx, w anim phase

int _SlotW;
int _SlotCount;
float _Extrapolate;
float _DebugUseMatrix;   // 1 = render at the instance (dummy) matrix, bypassing texture data
float _DebugForceScale;  // 1 = force scale 1 (bypass state/LOD collapse)
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
#define ES_VARIANT_RUNNER 1
#define ES_VARIANT_BOSS   2

float EsHash(int n) { return frac(sin((float)n * 127.1) * 43758.5453); }

// Instance slot index; 0 in non-instancing variants (unused in practice because the
// material has instancing enabled, but the variant must still compile).
int EsInstanceSlot()
{
#if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
    return (int)unity_InstanceID;
#else
    return 0;
#endif
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
    if (variant == ES_VARIANT_BOSS) scale *= 1.9;
    else if (variant == ES_VARIANT_RUNNER) scale *= 0.9;

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

    // per-instance tint variation; bosses read reddish
    float h = EsHash(slot * 7 + 13);
    tint = float3(0.85 + 0.3 * h, 0.85 + 0.3 * h, 0.85 + 0.3 * h);
    if (variant == ES_VARIANT_BOSS) tint = float3(1.25, 0.65, 0.6);
    flash = saturate(flashT * 6.0);
}
