// Simple vertex-extrusion outline shader for the StylizedCharacter avatar.
// Works with both Built-in and URP by using UnityObjectToClipPos.
// Renders only back-faces (Cull Front) with a uniform colour at a fixed
// screen-space thickness, producing a clean silhouette outline.
Shader "Custom/Outline"
{
    Properties
    {
        _OutlineColor     ("Outline Color",     Color)  = (1,1,1,1)
        _OutlineThickness ("Outline Thickness", Float)  = 0.008
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent+1" }

        Pass
        {
            Name "Outline"
            Cull Front      // Only render back-faces → visible as a rim from the front
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _OutlineColor;
            float  _OutlineThickness;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // Push vertex outward along normal in clip space
                float4 clipPos  = UnityObjectToClipPos(v.vertex);
                float3 clipNorm = normalize(mul((float3x3)UNITY_MATRIX_VP,
                                 mul((float3x3)unity_ObjectToWorld, v.normal)));
                // Scale offset by clip-space w so thickness is screen-constant
                clipPos.xy += clipNorm.xy * _OutlineThickness * clipPos.w;
                o.pos = clipPos;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _OutlineColor;
            }
            ENDCG
        }
    }
}
