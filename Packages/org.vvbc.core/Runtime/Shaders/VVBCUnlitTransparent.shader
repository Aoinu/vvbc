// Lightweight unlit alpha-blended shader for phantoms, catch volumes and zone displays.
// Standard Transparent-queue rules only: no GrabPass, no depth write, no queue override.
Shader "VVBC/Unlit Transparent"
{
    Properties
    {
        [MainTexture] _MainTex ("Texture (RGBA)", 2D) = "white" {}
        [MainColor] _Color ("Color", Color) = (1, 1, 1, 1)
        [Toggle(_VVBC_VERTEX_COLOR)] _UseVertexColor ("Use Vertex Color", Float) = 0
        [Enum(Back, 2, Off, 0)] _Cull ("Cull", Float) = 2
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
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma shader_feature_local _VVBC_VERTEX_COLOR

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            #if defined(_VVBC_VERTEX_COLOR)
                half4 color : COLOR;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            #if defined(_VVBC_VERTEX_COLOR)
                half4 color : COLOR;
            #endif
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
            #if defined(_VVBC_VERTEX_COLOR)
                o.color = v.color;
            #endif
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                half4 col = tex2D(_MainTex, i.uv) * (half4)UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
            #if defined(_VVBC_VERTEX_COLOR)
                col *= i.color;
            #endif
                return col;
            }
            ENDCG
        }
    }
}
