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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/AmbientOcclusion.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _RoomExposure;
            float _RoomSaturation;

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

                Light mainLight = GetMainLight();
                half ndl = saturate(dot(n, mainLight.direction));
                half3 color = albedo * mainLight.color * ndl;

                uint count = GetAdditionalLightsCount();
                for (uint li = 0u; li < count; li++)
                {
                    Light light = GetAdditionalLight(li, i.positionWS);
                    half extra = saturate(dot(n, light.direction));
                    color += albedo * light.color * extra * light.distanceAttenuation * 0.7h;
                }

                // Screen space occlusion replaces the contact shadows this build cannot render:
                // the URP shadow path corrupts texture sampling, but SSAO only touches ambient.
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

                // Grading lives in the shader because the URP volume stack does not reach this
                // camera; ACES keeps the linen from clipping the way a raw copy does.
                color *= _RoomExposure;
                const half a = 2.51h, b = 0.03h, c = 2.43h, d = 0.59h, e = 0.14h;
                color = saturate((color * (a * color + b)) / (color * (c * color + d) + e));
                half luma = dot(color, half3(0.2126h, 0.7152h, 0.0722h));
                color = lerp(luma.xxx, color, _RoomSaturation);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
