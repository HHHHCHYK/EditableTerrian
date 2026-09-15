Shader "Humanier/Terrain Low Poly"
{
    Properties { _Brightness ("Brightness", Range(0, 2)) = 1 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; half3 color : COLOR; half light : TEXCOORD0; };
            float _Brightness;
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.light = saturate(dot(normalWS, normalize(float3(.35, .8, .25))) * .55 + .45);
                output.color = input.color.rgb;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target { return half4(input.color * input.light * _Brightness, 1); }
            ENDHLSL
        }
    }
}
