Shader "Custom/ZEDStereoPassthrough"
{
    // Full-screen stereo passthrough for ZED Mini -> Quest 3 teleoperation.
    //
    // Renders in the Background queue so it sits behind all scene objects.
    // Selects _Left or _Right texture based on the current stereo eye index.
    //
    // _UVScaleX / _UVScaleY: set by ZEDFOVFiller for FOV correction.
    //   1.0   -> stretch to fill (distorts angular scale)
    //   >1.0  -> letterbox: correct 1:1 angular mapping, black bars at edges (recommended)
    //
    // _IPDShift: per-eye horizontal UV nudge to tune stereo depth alignment.
    //   Positive -> left eye samples further left / right eye samples further right
    //               = increases effective stereo baseline = objects appear closer.
    //   Start at 0 and adjust in small steps (0.005 increments) until depth feels correct.
    //   Typical correction range: -0.02 to +0.02 UV units.
    //
    // ZED Mini calibrated FOVs at HD720: ~84 deg H x ~54 deg V  (from fx~711, fy~712)
    // Quest 3 display FOVs:             ~107 deg H x ~96 deg V

    Properties
    {
        _Left      ("Left Eye Texture",  2D) = "black" {}
        _Right     ("Right Eye Texture", 2D) = "black" {}
        _UVScaleX  ("UV Scale X",     Float) = 1.0
        _UVScaleY  ("UV Scale Y",     Float) = 1.0
        _IPDShift  ("IPD Shift (UV)", Float) = 0.0
        _FlipY     ("Flip Y",         Float) = 0.0

        // User reposition (set by ZEDFOVFiller). Applied IDENTICALLY to both eyes so
        // stereo alignment is preserved (no per-eye vertical/horizontal asymmetry).
        _UserZoom    ("User Zoom",     Float) = 1.0   // >1 zooms in (magnifies feed)
        _UserOffsetX ("User Offset X", Float) = 0.0   // pans feed horizontally
        _UserOffsetY ("User Offset Y", Float) = 0.0   // pans feed vertically

        // Zoom convergence (IPD comfort): extra per-eye convergence that scales with the zoom,
        // pulling the two eye images together as you zoom in so disparity doesn't blow up into
        // double vision. Tune to the operator. 0 = no compensation.
        _ZoomConvergence ("Zoom Convergence", Float) = 0.0

        // Canvas (display window) shaping — scales the clip-space output rectangle.
        // The pass renders in clip space, so these reshape the on-screen window itself.
        _CanvasScaleX ("Canvas Scale X (stretch)", Float) = 1.0
        _CanvasScaleY ("Canvas Scale Y (stretch)", Float) = 1.0
        _CanvasDepth  ("Canvas Depth (uniform)",   Float) = 1.0   // >1 bigger/closer, <1 smaller/further
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "Queue"          = "Background-100"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ZEDPassthrough"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite Off
            ZTest  Always
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_Left);  SAMPLER(sampler_Left);
            TEXTURE2D(_Right); SAMPLER(sampler_Right);

            CBUFFER_START(UnityPerMaterial)
                float4 _Left_ST;
                float4 _Right_ST;
                float  _UVScaleX;
                float  _UVScaleY;
                float  _IPDShift;
                float  _FlipY;
                float  _UserZoom;
                float  _UserOffsetX;
                float  _UserOffsetY;
                float  _ZoomConvergence;
                float  _CanvasScaleX;
                float  _CanvasScaleY;
                float  _CanvasDepth;
            CBUFFER_END

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                // Output directly in clip/NDC space — bypasses the 3D camera
                // transform so both eyes see identical full-screen coverage with
                // zero parallax. Unity's default quad has X/Y in [-0.5, 0.5].
                // Canvas shaping: stretch (X/Y) and uniform depth scale of the clip-space window.
                OUT.positionHCS = float4(IN.positionOS.x * 2.0 * _CanvasScaleX * _CanvasDepth,
                                        IN.positionOS.y * 2.0 * _CanvasScaleY * _CanvasDepth,
                                        0.999, 1.0);
                // _FlipY is set by ZEDFOVFiller. Toggle it in the Inspector
                // if the image appears upside down (needed in editor, not on device or vice versa).
                OUT.uv = float2(IN.uv.x, _FlipY > 0.5 ? 1.0 - IN.uv.y : IN.uv.y);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // 1. FOV correction + user zoom: scale around center. _UserZoom>1 magnifies.
                //    Applied to both eyes identically, so stereo stays aligned.
                float2 uv = (IN.uv - 0.5) * float2(_UVScaleX, _UVScaleY) / max(_UserZoom, 1e-3) + 0.5;

                // 1b. User pan: shift the feed the SAME way for both eyes (no parallax change).
                uv.x += _UserOffsetX;
                uv.y += _UserOffsetY;

                // 2. IPD correction: nudge each eye horizontally in opposite directions.
                //    Left eye (index 0): negative shift samples further left in texture
                //    Right eye (index 1): positive shift samples further right in texture
                //    Together this widens the effective stereo baseline when positive.
                //    Zoom convergence is added in: as _UserZoom rises above 1, the eyes are
                //    pulled together so the magnified parallax stays fusible (no double vision).
                float eyeSign = (unity_StereoEyeIndex == 0) ? -1.0 : 1.0;
                float converge = _IPDShift + _ZoomConvergence * (_UserZoom - 1.0);
                uv.x += converge * eyeSign;

                uv = saturate(uv);

                half4 L = SAMPLE_TEXTURE2D(_Left,  sampler_Left,  uv);
                half4 R = SAMPLE_TEXTURE2D(_Right, sampler_Right, uv);

                // ZED camera orientation is mirrored vs headset eye layout, so swap L/R.
                return lerp(R, L, (half)unity_StereoEyeIndex);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
