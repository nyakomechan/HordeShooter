Shader "HordeShooter/EnemyFx"
{
    Properties
    {
        _AttackColor ("Attack Color", Color) = (1.0, 0.35, 0.15, 1)
        _SparkColor ("Spark Color", Color) = (1.0, 0.9, 0.5, 1)
        _PosTex ("Pos Tex", 2D) = "black" {}
        _StateTex ("State Tex", 2D) = "black" {}
        _MetaTex ("Meta Tex", 2D) = "black" {}
        _SlotList ("Slot List", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend One One
        ZWrite Off
        ZTest LEqual
        Cull Off

        // GPU-driven attack FX (M7e). One instanced quad per slot; the shader
        // reads the sim RTs and collapses every instance that has nothing to show:
        //   _FxMode 0 = attack telegraph: expanding ring in front of ATTACK-state enemies
        //   _FxMode 1 = hit spark: soft flash at the chest while the hit flash lasts
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Assets/HordeShooter/Shaders/EnemyAnim.cginc"

            float4 _AttackColor;
            float4 _SparkColor;
            float _FxMode;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 col : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                int slot = EsInstanceSlot();
                uint uW = (uint)_SlotW;
                int2 texel = int2((uint)slot % uW, (uint)slot / uW);
                float4 pd = _PosTex.Load(int3(texel, 0));
                float4 sd = _StateTex.Load(int3(texel, 0));

                float3 pos = pd.xyz;
                float yaw = pd.w;
                int state = (int)round(sd.y);
                float timer = sd.z;
                float flashT = sd.w;

                float scale = 0.0;
                float alpha = 0.0;
                float4 col = _AttackColor;

                if (_FxMode < 0.5)
                {
                    // attack telegraph: expanding ring while in ATTACK state
                    if (state == ES_ATTACK)
                    {
                        float t = saturate(timer / max(_AttackDuration, 0.001));
                        scale = lerp(0.35, 1.25, t);
                        alpha = (1.0 - t) * 0.85;
                    }
                }
                else
                {
                    // hit spark: soft flash while the hit flash decays (0.2 s)
                    if (flashT > 0.001)
                    {
                        float t = saturate(flashT / 0.2);
                        scale = lerp(0.85, 0.25, t);
                        alpha = t;
                        col = _SparkColor;
                    }
                }

                // camera-facing quad
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp = UNITY_MATRIX_V[1].xyz;
                float3 local = v.vertex.xy * scale;
                float3 wp = pos + float3(0, 0.9, 0) + camRight * local.x + camUp * local.y;
                if (_FxMode < 0.5)
                {
                    // push the ring forward along the enemy's facing
                    float sy = sin(yaw);
                    float cy = cos(yaw);
                    wp += float3(sy, 0, cy) * 0.55 * scale;
                }

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                o.uv = v.uv;
                o.col = float4(col.rgb, col.a * alpha);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float d = length(i.uv - 0.5) * 2.0;   // 0 center, 1 edge
                float mask;
                if (_FxMode < 0.5)
                {
                    // claw arc: a ring band with a soft outer edge
                    mask = smoothstep(0.35, 0.6, d) * smoothstep(1.0, 0.75, d);
                }
                else
                {
                    // spark: soft dot
                    mask = smoothstep(1.0, 0.1, d);
                }
                return float4(i.col.rgb * i.col.a * mask, 1);
            }
            ENDHLSL
        }
    }
}
