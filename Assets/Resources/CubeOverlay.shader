// Semi-transparent cube overlay with slightly brighter edges.
// Works with Unity's built-in cube mesh, whose faces each use UVs 0..1.
// Loaded at runtime via Resources.Load<Shader>("CubeOverlay").
Shader "Hidden/CubeOverlay"
{
    Properties
    {
        _Color ("Fill Color", Color) = (0.1, 0.75, 1, 0.3)
        _EdgeColor ("Edge Color", Color) = (0.1, 0.75, 1, 0.85)
        _EdgeWidth ("Edge Width", Range(0, 0.2)) = 0.03
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            // Separate alpha blending keeps the eye buffer's alpha correct for passthrough compositing.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;
            fixed4 _EdgeColor;
            float _EdgeWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float shade : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                // Gentle fixed shading so neighbouring faces are distinguishable.
                float3 n = UnityObjectToWorldNormal(v.normal);
                o.shade = 0.7 + 0.3 * saturate(dot(n, normalize(float3(0.3, 0.8, -0.5))));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 d = min(i.uv, 1.0 - i.uv);
                float edge = _EdgeWidth > 0 ? 1.0 - smoothstep(_EdgeWidth * 0.5, _EdgeWidth, min(d.x, d.y)) : 0.0;
                fixed4 c = lerp(_Color, _EdgeColor, edge);
                c.rgb *= i.shade;
                return c;
            }
            ENDCG
        }
    }
}
