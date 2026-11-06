Shader "Mas3D/RoomSurface"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _AmbientStrength ("Ambient", Float) = 1.4
        _AOStrength ("AO Strength", Float) = 0.7
        _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            // 阴影：主光是月光，要从窗外把床、柜、人的影子铺到地板上；附带光只有床边台灯，
            // 也吃软阴影（URP 资产里两个 prefilter 开关已打开）。
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/AmbientOcclusion.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half _AmbientStrength;
                half _AOStrength;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb * _Color.rgb;
                float3 n = normalize(i.normalWS);

                // 主光必须带 shadowCoord 才能拿到 shadowAttenuation：无参的 GetMainLight()
                // 恒返回 1，阴影等于没开（2026-11-06 评审 R17）。
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half ndl = saturate(dot(n, mainLight.direction));
                half3 color = albedo * mainLight.color * ndl * mainLight.shadowAttenuation;

                uint count = GetAdditionalLightsCount();
                for (uint li = 0u; li < count; li++)
                {
                    Light light = GetAdditionalLight(li, i.positionWS);
                    half extra = saturate(dot(n, light.direction));
                    color += albedo * light.color * extra * light.distanceAttenuation
                             * light.shadowAttenuation * 0.7h;
                }

                // SSAO 只吃环境项，和上面两条阴影各管一段：阴影管直射光的遮挡，SSAO 管缝隙里的环境光。
                half ao = 1.0h;
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor aoFactor =
                        GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(i.positionCS));
                    ao = aoFactor.indirectAmbientOcclusion;
                    // Full strength SSAO reads as grime on a room this flat lit, so the effect
                    // is dialled back per material instead of globally.
                    ao = lerp(1.0h, ao, _AOStrength);
                #endif

                color += albedo * SampleSH(n) * _AmbientStrength * ao;

                // 调色不在这里做：曝光/ACES/饱和度统一由 RoomLook 建的全局 Volume 承担，
                // 这样房间与 MToon 角色走同一条色调曲线（见 docs/.../2026-11-06-art-and-controls-alignment-design.md G1）。
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
