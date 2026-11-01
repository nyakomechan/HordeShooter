Shader "HordeShooter/EnemyRender"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.5, 0.55, 0.45, 1)
        _FlashColor ("Flash Color", Color) = (1.0, 0.2, 0.1, 1)
        _PosTex ("Pos Tex", 2D) = "black" {}
        _StateTex ("State Tex", 2D) = "black" {}
        _MetaTex ("Meta Tex", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off

        // Color pass: cheap fixed-direction lambert + height AO + hit flash.
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Assets/HordeShooter/Shaders/EnemyAnim.cginc"

            float4 _BaseColor;
            float4 _FlashColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 shade : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                int slot = EsInstanceSlot();
                v2f o;
                float3 wp, wn, tint;
                float flash;
                EsAnimate(v.vertex.xyz, v.normal, v.color.r, v.color.g, slot, wp, wn, tint, flash);
                if (_DebugUseMatrix > 1.5 && _DebugUseMatrix < 2.5)
                    o.pos = mul(UNITY_MATRIX_VP, mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)));
                else if (_DebugUseMatrix > 0.5)
                    o.pos = UnityObjectToClipPos(v.vertex);
                else
                    o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));

                float ndl = saturate(dot(wn, normalize(float3(0.35, 0.85, 0.4)))) * 0.7 + 0.3;
                float ao = saturate(0.55 + v.vertex.y * 0.5);
                float3 col = _BaseColor.rgb * tint * ndl * ao;
                col = lerp(col, _FlashColor.rgb, flash);
                o.shade = col;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.shade, 1);
            }
            ENDHLSL
        }

        // Depth-only shadow caster with the same animated transform.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual Cull Off

            HLSLPROGRAM
            #pragma target 4.0
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_instancing
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            #include "Assets/HordeShooter/Shaders/EnemyAnim.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2fShadow
            {
                float4 pos : SV_POSITION;
            };

            v2fShadow vertShadow(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                int slot = EsInstanceSlot();
                v2fShadow o;
                float3 wp, wn, tint;
                float flash;
                EsAnimate(v.vertex.xyz, v.normal, v.color.r, v.color.g, slot, wp, wn, tint, flash);
                o.pos = mul(UNITY_MATRIX_VP, float4(wp + wn * 0.02, 1.0));
                return o;
            }

            float4 fragShadow(v2fShadow i) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
