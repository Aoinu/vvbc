// Landing point circle drawn on one horizontal Quad (1 x 1, UV 0..1): a disc touching the
// Quad's edges, fully transparent outside it, a thin outline and a faint interior. The outline
// width is in metres, read against the Quad's world scale, so it stays the same as the circle
// grows. Unlit, alpha-blended, no depth write, no shadows, both faces. Polygon Offset keeps it
// above the ground it lies on; the Quad itself sits only a few millimetres above.
Shader "VVBC/Landing Marker"
{
    Properties
    {
        [MainColor] _Color ("Color", Color) = (1, 1, 1, 1)
        _OutlineAlpha ("Outline Alpha", Range(0, 1)) = 0.9
        _InteriorAlpha ("Interior Alpha", Range(0, 1)) = 0.18
        _OutlineWidth ("Outline Width (m)", Float) = 0.06
        _OffsetFactor ("Offset Factor", Float) = -1
        _OffsetUnits ("Offset Units", Float) = -1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "ForceNoShadowCasting" = "True"
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset [_OffsetFactor], [_OffsetUnits]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                // xy: position in the disc (-1..1 at the Quad's edges); z: outline start, as a
                // fraction of the radius.
                float3 disc : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            half4 _Color;
            half _OutlineAlpha;
            half _InteriorAlpha;
            float _OutlineWidth;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                float radius = 0.5 * length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20));
                o.disc.xy = v.uv * 2.0 - 1.0;
                o.disc.z = saturate(1.0 - _OutlineWidth / max(radius, 0.0001));
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float r = length(i.disc.xy);
                float aa = max(fwidth(r), 0.0001);
                // 1 inside the disc, 0 outside, antialiased over one pixel.
                float inside = 1.0 - smoothstep(1.0 - aa, 1.0, r);
                float outline = smoothstep(i.disc.z - aa, i.disc.z, r);
                half alpha = lerp(_InteriorAlpha, _OutlineAlpha, outline) * inside * _Color.a;
                return half4(_Color.rgb, alpha);
            }
            ENDCG
        }
    }
}
