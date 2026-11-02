Shader "Mas3D/Mirror"
{
    Properties
    {
        _Color ("Glass Colour", Color) = (0.62,0.70,0.76,1)
        _RimColor ("Rim Colour", Color) = (0.88,0.94,1.0,1)
        _RimPower ("Rim Power", Float) = 3.0
        _RimStrength ("Rim Strength", Float) = 0.55
        _Sheen ("Sheen", Float) = 0.35
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float _RoomExposure;
            float _RoomSaturation;

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
                half _RimPower;
                half _RimStrength;
                half _Sheen;
                half _Cull;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                half fresnel = pow(1.0h - saturate(dot(n, v)), _RimPower);

                Light mainLight = GetMainLight();
                half3 shade = _Color.rgb * (0.55h + 0.45h * saturate(dot(n, mainLight.direction)));

                // A grazing sheen is what sells flat grey as glass, since the room has no
                // reflection probe to sample.
                half3 spec = mainLight.color * pow(saturate(dot(n, mainLight.direction)), 8.0h) * _Sheen;

                half3 color = shade
                    + _RimColor.rgb * fresnel * _RimStrength
                    + spec;

                color += _Color.rgb * SampleSH(n) * 0.6h;

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
