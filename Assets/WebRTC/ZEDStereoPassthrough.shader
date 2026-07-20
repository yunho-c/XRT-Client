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

        // Single side-by-side (SBS) source: when 1, BOTH eyes read from _Left, which holds the
        // stitched [left | right] stereo frame. Left eye samples the left half, right eye the right
        // half — one decoder, perfect L/R sync. 0 = classic dual-texture (_Left + _Right) path.
        _SBS ("Single SBS Texture", Float) = 0.0

        // Zoom convergence (IPD comfort): extra per-eye convergence that scales with the zoom,
        // pulling the two eye images together as you zoom in so disparity doesn't blow up into
        // double vision. Tune to the operator. 0 = no compensation.
        _ZoomConvergence ("Zoom Convergence", Float) = 0.0

        // Canvas (display window) shaping — scales the clip-space output rectangle.
        // The pass renders in clip space, so these reshape the on-screen window itself.
        _CanvasScaleX ("Canvas Scale X (stretch)", Float) = 1.0
        _CanvasScaleY ("Canvas Scale Y (stretch)", Float) = 1.0
        _CanvasDepth  ("Canvas Depth (uniform)",   Float) = 1.0   // >1 bigger/closer, <1 smaller/further

        // Edge-softening blur: blurs ONLY the stretched periphery so the harsh magnified edge pixels
        // are gentle (helps operators with photosensitivity). The central content stays perfectly
        // sharp — _BlurEdgeStart is where the blur begins (0 = screen centre, 1 = screen edge), so the
        // inner [0, _BlurEdgeStart] of the view is untouched.
        _BlurEdgeStart ("Blur Edge Start (0=center..1=edge)", Range(0,1)) = 0.78
        _BlurStrength  ("Blur Strength (0..1)",               Range(0,1)) = 1.0
        _BlurRadius    ("Blur Radius (uv)",                   Float)      = 0.008

        // Letterbox: when 1, the FOV-remapped periphery (UV that fell outside the source image) is
        // painted opaque BLACK instead of the default clamped edge-pixel extension. 0 = edge-fill.
        _BlackOutside  ("Black Outside (letterbox)",          Float)      = 0.0
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
                float  _SBS;
                float  _ZoomConvergence;
                float  _CanvasScaleX;
                float  _CanvasScaleY;
                float  _CanvasDepth;
                float  _BlurEdgeStart;
                float  _BlurStrength;
                float  _BlurRadius;
                float  _BlackOutside;
            CBUFFER_END

            // Edge blur: an 8-tap ring blended in by `weight` (0 = sharp center sample). Taps are
            // clamped to [lo,hi] so an SBS half NEVER bleeds across the L|R (or top/bottom) seam into
            // the other eye's image. `weight` is 0 across the inner screen, so center pixels take the
            // cheap single-sample path (the branch is spatially coherent → mobile-friendly).
            half4 SampleEdgeBlurred(TEXTURE2D_PARAM(tex, smp), float2 uv, float2 lo, float2 hi, float weight)
            {
                half4 c = SAMPLE_TEXTURE2D(tex, smp, clamp(uv, lo, hi));
                if (weight <= 0.001) return c;
                float r = _BlurRadius;
                float d = r * 0.70710678;   // diagonal taps at ~45°
                half4 b = c;
                b += SAMPLE_TEXTURE2D(tex, smp, clamp(uv + float2( r,  0.0), lo, hi));
                b += SAMPLE_TEXTURE2D(tex, smp, clamp(uv + float2(-r,  0.0), lo, hi));
                b += SAMPLE_TEXTURE2D(tex, smp, clamp(uv + float2( 0.0,  r), lo, hi));
                b += SAMPLE_TEXTURE2D(tex, smp, clamp(uv + float2( 0.0, -r), lo, hi));
                b += SAMPLE_TEXTURE2D(tex, smp, clamp(uv + float2( d,  d), lo, hi));
                b += SAMPLE_TEXTURE2D(tex, smp, clamp(uv + float2(-d,  d), lo, hi));
                b += SAMPLE_TEXTURE2D(tex, smp, clamp(uv + float2( d, -d), lo, hi));
                b += SAMPLE_TEXTURE2D(tex, smp, clamp(uv + float2(-d, -d), lo, hi));
                b *= (1.0 / 9.0);
                return lerp(c, b, weight);
            }

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

                // Periphery test (BEFORE the clamp): true where the FOV/zoom remap sampled beyond
                // the source image. Used by the black-outside (letterbox) mode below.
                bool outside = (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0);

                uv = saturate(uv);

                // Single stitched stereo stream: the two eye images are the two halves of _Left. Sample
                // both at the SAME transformed uv and select per eye with the IDENTICAL lerp the dual
                // path uses below — so the final render (FOV, zoom, pan, IPD, canvas, eye mapping) is
                // the same as dual; only the texture source differs. _SBS picks the split:
                //   1 = horizontal [left|right]   2 = horizontal swapped
                //   3 = vertical [top/bottom]     4 = vertical swapped
                // (orientation is auto-detected from the frame shape; swap = sbsSwapEyes; _FlipY for
                // upside-down). By the /zed_stereo stitch convention the FIRST half (left/top) is the
                // ZED-LEFT camera -> headset LEFT eye, matching dual's net mapping.
                // Edge-softening weight: 0 across the inner view, ramping up only near the screen
                // edges, so ONLY the harsh stretched periphery is blurred and the real content stays
                // sharp. Based on the screen-space quad uv (IN.uv), independent of the FOV/zoom remap.
                float2 edgeXY = abs(IN.uv - 0.5) * 2.0;     // 0 at center -> 1 at each screen edge
                float edgeWeight = smoothstep(_BlurEdgeStart, 1.0, max(edgeXY.x, edgeXY.y)) * _BlurStrength;

                half4 col;
                if (_SBS > 0.5)
                {
                    bool sbsHoriz = (_SBS < 2.5);
                    bool sbsSwap  = (_SBS == 2.0 || _SBS == 4.0);
                    float2 uvFirst  = sbsHoriz ? float2(uv.x * 0.5,       uv.y)
                                               : float2(uv.x, uv.y * 0.5 + 0.5);   // left / top
                    float2 uvSecond = sbsHoriz ? float2(uv.x * 0.5 + 0.5, uv.y)
                                               : float2(uv.x, uv.y * 0.5);          // right / bottom
                    // Sub-rect bounds for each half, so blur taps never bleed across the seam into the other eye.
                    float2 loFirst  = sbsHoriz ? float2(0.0, 0.0) : float2(0.0, 0.5);
                    float2 hiFirst  = sbsHoriz ? float2(0.5, 1.0) : float2(1.0, 1.0);
                    float2 loSecond = sbsHoriz ? float2(0.5, 0.0) : float2(0.0, 0.0);
                    float2 hiSecond = sbsHoriz ? float2(1.0, 1.0) : float2(1.0, 0.5);
                    // headset-left eye (index 0) -> first half (ZED-left), unless swapped — IDENTICAL
                    // mapping to the dual lerp below (lerp(zedLeft,zedRight,eyeIndex) picked exactly one).
                    bool useFirst = ((unity_StereoEyeIndex == 0) != sbsSwap);
                    float2 sUv = useFirst ? uvFirst : uvSecond;
                    float2 sLo = useFirst ? loFirst : loSecond;
                    float2 sHi = useFirst ? hiFirst : hiSecond;
                    col = SampleEdgeBlurred(TEXTURE2D_ARGS(_Left, sampler_Left), sUv, sLo, sHi, edgeWeight);
                }
                // Dual: ZED camera orientation is mirrored vs headset eye layout, so eye 0 -> _Right,
                // eye 1 -> _Left (identical to the old lerp(R, L, eyeIndex)).
                else if (unity_StereoEyeIndex == 0)
                    col = SampleEdgeBlurred(TEXTURE2D_ARGS(_Right, sampler_Right), uv, float2(0.0, 0.0), float2(1.0, 1.0), edgeWeight);
                else
                    col = SampleEdgeBlurred(TEXTURE2D_ARGS(_Left, sampler_Left), uv, float2(0.0, 0.0), float2(1.0, 1.0), edgeWeight);

                // Letterbox mode: paint the FOV-remapped periphery opaque black instead of the
                // default clamped edge-pixel extension.
                if (_BlackOutside > 0.5 && outside)
                    col.rgb = half3(0.0, 0.0, 0.0);
                return col;
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
