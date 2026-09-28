Shader "Humanier/Terrain Low Poly"
{
    Properties
    {
        _Brightness ("Brightness", Range(0, 2)) = 1
        _FacetStrength ("Facet Strength", Range(0, 1)) = 1
        _AmbientLight ("Ambient Light", Range(0, 1)) = 0.3
        [HideInInspector] _FarMaskEnabled ("Far Mask Enabled", Float) = 0
        [HideInInspector] _FarCoverageMask ("Far Coverage Mask", 2D) = "black" {}
        [HideInInspector] _FarMaskOriginInvSize ("Far Mask Origin Inv Size", Vector) = (0, 0, 0, 0)
        [HideInInspector] _FarHeightOffset ("Far Height Offset", Float) = 0
        [HideInInspector] _FarFadeOrigin ("Far Fade Origin", Vector) = (0, 0, 0, 0)
        [HideInInspector] _FarFadeStartEnd ("Far Fade Start End", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_FarCoverageMask);
            SAMPLER(sampler_FarCoverageMask);
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 color : COLOR;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };
            CBUFFER_START(UnityPerMaterial)
                float _Brightness;
                float _FacetStrength;
                float _AmbientLight;
                float _FarMaskEnabled;
                float4 _FarMaskOriginInvSize;
                float _FarHeightOffset;
                float4 _FarFadeOrigin;
                float4 _FarFadeStartEnd;
            CBUFFER_END
            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionOS = input.positionOS.xyz;
                positionOS.y += _FarHeightOffset;
                float3 positionWS = TransformObjectToWorld(positionOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = input.color.rgb;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                if (_FarMaskEnabled > 0.5)
                {
                    float2 farMaskUv = (input.positionWS.xz - _FarMaskOriginInvSize.xy) * _FarMaskOriginInvSize.z;
                    if (all(farMaskUv >= 0.0) && all(farMaskUv <= 1.0))
                    {
                        half detailedTerrainReady = SAMPLE_TEXTURE2D(_FarCoverageMask, sampler_FarCoverageMask, farMaskUv).r;
                        if (detailedTerrainReady > 0.5h)
                        {
                            float distanceToFocus = distance(input.positionWS.xz, _FarFadeOrigin.xy);
                            float farVisibility = smoothstep(_FarFadeStartEnd.x, _FarFadeStartEnd.y, distanceToFocus);
                            float2 pixel = floor(input.positionCS.xy);
                            float noise = frac(sin(dot(pixel, float2(12.9898, 78.233))) * 43758.5453);
                            clip(farVisibility - noise);
                        }
                    }
                }
                float3 smoothNormal = SafeNormalize(input.normalWS);
                // Derivatives recover each triangle's geometric normal without
                // duplicating mesh vertices or changing terrain collision geometry.
                float3 faceNormal = cross(ddy(input.positionWS), ddx(input.positionWS));
                faceNormal = dot(faceNormal, faceNormal) > 1e-20 ? normalize(faceNormal) : smoothNormal;
                // Screen-space derivative handedness differs between graphics APIs.
                faceNormal *= dot(faceNormal, smoothNormal) < 0 ? -1 : 1;
                float3 normalWS = SafeNormalize(lerp(smoothNormal, faceNormal, _FacetStrength));
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half diffuse = saturate(dot(normalWS, mainLight.direction));
                half light = diffuse * mainLight.shadowAttenuation * (1 - _AmbientLight) + _AmbientLight;
                return half4(input.color * light * mainLight.color * _Brightness, 1);
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
    }
}
