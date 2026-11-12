// Opaque unlit shader for markings laid on the ground (lines, boxes, plates).
// Z-fighting against the ground is handled by Polygon Offset; geometry is never moved.
Shader "VVBC/Ground Marking"
{
    Properties
    {
        [MainTexture] _MainTex ("Texture (RGB)", 2D) = "white" {}
        [MainColor] _Color ("Color (RGB, alpha unused)", Color) = (1, 1, 1, 1)
        _OffsetFactor ("Offset Factor", Float) = -1
        _OffsetUnits ("Offset Units", Float) = -1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry+1"
            "RenderType" = "Opaque"
            "ForceNoShadowCasting" = "True"
        }

        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Back
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
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                half3 rgb = tex2D(_MainTex, i.uv).rgb * (half3)UNITY_ACCESS_INSTANCED_PROP(Props, _Color).rgb;
                return half4(rgb, 1);
            }
            ENDCG
        }
    }
}
