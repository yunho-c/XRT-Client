using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Drives the ZEDStereoPassthrough material's FOV-correction and IPD-correction
/// uniforms each frame. The shader itself renders in clip space (no 3D transform),
/// so this script only sets material properties — it does not move any geometry.
///
/// FOV correction (_UVScaleX/Y):
///   LetterboxCorrect (default) — displays ZED content at its true angular size.
///   ZED Mini calibrated FOVs at HD720: ~84 H x ~54 V degrees.
///   The image occupies the central ~79% x ~56% of the Quest 3 display with black
///   bars at the edges. Objects look natural-sized, matching real-world angles.
///
/// IPD correction (_IPDShift):
///   ZED Mini baseline = 63 mm (designed for average human IPD).
///   If your Quest IPD setting differs from 63 mm, stereo depth will feel off.
///   Positive shift -> increases effective baseline -> objects appear closer.
///   Start at 0 and adjust in 0.005 steps until a held object at arm's length
///   looks like it is actually at arm's length.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(Renderer))]
public class ZEDFOVFiller : MonoBehaviour
{
    public enum FillMode { LetterboxCorrect, StretchToFill }

    [Header("ZED Mini Calibrated FOV (HD720)")]
    [Tooltip("Horizontal FOV in degrees from calibration. Nominal: 84 deg (fx~711).")]
    [SerializeField] private float zedHorizontalFOV = 84f;

    [Tooltip("Vertical FOV in degrees from calibration. Nominal: 54 deg (fy~712).")]
    [SerializeField] private float zedVerticalFOV = 54f;

    [Header("Fill Mode")]
    [Tooltip("LetterboxCorrect: natural angular scale, black bars. StretchToFill: no bars, distorted scale.")]
    [SerializeField] private FillMode fillMode = FillMode.LetterboxCorrect;

    [Header("Image Orientation")]
    [Tooltip("Flip the image vertically. Toggle this if the video appears upside down.")]
    [SerializeField] private bool flipY = false;

    [Header("Stereo Depth Alignment")]
    [Tooltip("Per-eye horizontal UV nudge for IPD correction.\n" +
             "ZED Mini baseline = 63 mm. If your Quest IPD > 63 mm, try small positive values.\n" +
             "Adjust in 0.005 steps while looking at a held object until depth feels correct.\n" +
             "Typical range: -0.02 to +0.02.")]
    [SerializeField] private float ipdShift = 0f;

    [Header("User Reposition (Align Video panel; saved to PlayerPrefs)")]
    [Tooltip("Zoom factor for the feed. >1 magnifies (zoom in). Applied to both eyes equally.")]
    [SerializeField] private float userZoom = 1f;
    [Tooltip("Horizontal pan of the feed in UV units (both eyes equally — stereo preserved).")]
    [SerializeField] private float userOffsetX = 0f;
    [Tooltip("Vertical pan of the feed in UV units.")]
    [SerializeField] private float userOffsetY = 0f;

    [Header("Reposition Steps / Limits")]
    [Tooltip("Zoom change per arrow press.")]
    public float zoomStep = 0.03f;
    [Tooltip("Pan change per arrow press, in UV units.")]
    public float panStep = 0.005f;
    public float minZoom = 0.4f;
    public float maxZoom = 3f;
    [Tooltip("Max absolute pan offset in UV units.")]
    public float maxOffset = 0.5f;

    [Header("Zoom Convergence (IPD comfort)")]
    [Tooltip("Per-eye convergence added as you zoom in, so the magnified stereo parallax stays " +
             "fusible instead of going cross-eyed/double. Tune to the operator (try 0.005 steps, " +
             "~0.01–0.04). 0 = off.")]
    [SerializeField] private float zoomConvergence = 0.02f;

    [Header("Canvas Shape (Resize Display panel; clip-space output)")]
    [Tooltip("Horizontal stretch of the display window.")]
    [SerializeField] private float canvasScaleX = 1f;
    [Tooltip("Vertical stretch of the display window.")]
    [SerializeField] private float canvasScaleY = 1f;
    [Tooltip("Uniform size of the display window. >1 bigger (feels closer), <1 smaller (feels further).")]
    [SerializeField] private float canvasDepth = 1f;

    [Header("Canvas Steps / Limits")]
    public float stretchStep = 0.03f;
    public float depthStep = 0.03f;
    public float minCanvasScale = 0.3f;
    public float maxCanvasScale = 3f;

    const string PP_ZOOM = "zedUserZoom";
    const string PP_OFFX = "zedUserOffsetX";
    const string PP_OFFY = "zedUserOffsetY";
    const string PP_CONV = "zedZoomConverge";
    const string PP_CSX  = "zedCanvasScaleX";
    const string PP_CSY  = "zedCanvasScaleY";
    const string PP_CDEP = "zedCanvasDepth";

    private Renderer _renderer;
    private MaterialPropertyBlock _mpb;
    private Camera _cam;

    void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _mpb = new MaterialPropertyBlock();
        LoadReposition();
    }

    void Start() => _cam = GetComponentInParent<Camera>();

    void LateUpdate()
    {
        if (_cam == null)
            _cam = GetComponentInParent<Camera>();
        if (_cam == null)
            return;

        float uvScaleX = 1f;
        float uvScaleY = 1f;

        if (fillMode == FillMode.LetterboxCorrect)
        {
            // tan(half-angle) ratio: display FOV / ZED FOV.
            // Result > 1 => shader samples outside [0,1] => saturate => black bars.
            Matrix4x4 p = XRSettings.enabled
                ? _cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left)
                : _cam.projectionMatrix;

            float displayTanHH = 1f / Mathf.Abs(p[0, 0]);
            float displayTanHV = 1f / Mathf.Abs(p[1, 1]);
            float zedTanHH = Mathf.Tan(zedHorizontalFOV * 0.5f * Mathf.Deg2Rad);
            float zedTanHV = Mathf.Tan(zedVerticalFOV   * 0.5f * Mathf.Deg2Rad);

            uvScaleX = displayTanHH / zedTanHH;
            uvScaleY = displayTanHV / zedTanHV;
        }

        _renderer.GetPropertyBlock(_mpb);
        _mpb.SetFloat("_UVScaleX", uvScaleX);
        _mpb.SetFloat("_UVScaleY", uvScaleY);
        _mpb.SetFloat("_IPDShift", ipdShift);
        _mpb.SetFloat("_FlipY", flipY ? 1f : 0f);
        _mpb.SetFloat("_UserZoom", userZoom);
        _mpb.SetFloat("_UserOffsetX", userOffsetX);
        _mpb.SetFloat("_UserOffsetY", userOffsetY);
        _mpb.SetFloat("_ZoomConvergence", zoomConvergence);
        _mpb.SetFloat("_CanvasScaleX", canvasScaleX);
        _mpb.SetFloat("_CanvasScaleY", canvasScaleY);
        _mpb.SetFloat("_CanvasDepth", canvasDepth);
        _renderer.SetPropertyBlock(_mpb);
    }

    // ── Align Video panel API ──────────────────────────────────────────────────
    // Direct C# calls (work even though this component sits on the inactive
    // VideoStreamingViewport until the display is shown). The uniforms apply on the
    // next LateUpdate once the viewport is active.

    public float UserZoom => userZoom;
    public float UserOffsetX => userOffsetX;
    public float UserOffsetY => userOffsetY;

    /// <summary>Zoom in/out. dir = +1 zooms in, -1 zooms out.</summary>
    public void NudgeZoom(float dir)
    {
        userZoom = Mathf.Clamp(userZoom + dir * zoomStep, minZoom, maxZoom);
    }

    /// <summary>Pan horizontally. dir = +1 moves the feed left, -1 moves it right.</summary>
    public void NudgePanX(float dir)
    {
        userOffsetX = Mathf.Clamp(userOffsetX + dir * panStep, -maxOffset, maxOffset);
    }

    /// <summary>Pan vertically. dir = +1 moves the feed down, -1 moves it up.</summary>
    public void NudgePanY(float dir)
    {
        userOffsetY = Mathf.Clamp(userOffsetY + dir * panStep, -maxOffset, maxOffset);
    }

    // Convenience methods for direct UI button wiring.
    // Arrows now PAN (up/down/left/right); the +/- buttons ZOOM.
    public void ZoomIn()  => NudgeZoom(+1f);
    public void ZoomOut() => NudgeZoom(-1f);
    public void PanLeft()  => NudgePanX(+1f);
    public void PanRight() => NudgePanX(-1f);
    public void PanUp()    => NudgePanY(-1f);
    public void PanDown()  => NudgePanY(+1f);

    /// <summary>Persist the current zoom/pan so it survives app restarts (and Quest builds).</summary>
    public void SaveReposition()
    {
        PlayerPrefs.SetFloat(PP_ZOOM, userZoom);
        PlayerPrefs.SetFloat(PP_OFFX, userOffsetX);
        PlayerPrefs.SetFloat(PP_OFFY, userOffsetY);
        PlayerPrefs.SetFloat(PP_CONV, zoomConvergence);
        PlayerPrefs.Save();
    }

    /// <summary>Reset zoom/pan to defaults (no zoom, centered). Does not clear the saved values
    /// until you press Save again.</summary>
    public void ResetReposition()
    {
        userZoom = 1f;
        userOffsetX = 0f;
        userOffsetY = 0f;
    }

    void LoadReposition()
    {
        userZoom        = PlayerPrefs.GetFloat(PP_ZOOM, userZoom);
        userOffsetX     = PlayerPrefs.GetFloat(PP_OFFX, userOffsetX);
        userOffsetY     = PlayerPrefs.GetFloat(PP_OFFY, userOffsetY);
        zoomConvergence = PlayerPrefs.GetFloat(PP_CONV, zoomConvergence);
        canvasScaleX    = PlayerPrefs.GetFloat(PP_CSX,  canvasScaleX);
        canvasScaleY    = PlayerPrefs.GetFloat(PP_CSY,  canvasScaleY);
        canvasDepth     = PlayerPrefs.GetFloat(PP_CDEP, canvasDepth);
    }

    // ── Resize Display (canvas) panel API ──────────────────────────────────────
    // Reshapes the clip-space output window. Arrows stretch; +/- change uniform size.

    public float CanvasScaleX => canvasScaleX;
    public float CanvasScaleY => canvasScaleY;
    public float CanvasDepth  => canvasDepth;

    public void NudgeStretchX(float dir) => canvasScaleX = Mathf.Clamp(canvasScaleX + dir * stretchStep, minCanvasScale, maxCanvasScale);
    public void NudgeStretchY(float dir) => canvasScaleY = Mathf.Clamp(canvasScaleY + dir * stretchStep, minCanvasScale, maxCanvasScale);
    public void NudgeDepth(float dir)    => canvasDepth  = Mathf.Clamp(canvasDepth  + dir * depthStep,   minCanvasScale, maxCanvasScale);

    public void StretchWider()    => NudgeStretchX(+1f);
    public void StretchNarrower() => NudgeStretchX(-1f);
    public void StretchTaller()   => NudgeStretchY(+1f);
    public void StretchShorter()  => NudgeStretchY(-1f);
    public void CanvasCloser()    => NudgeDepth(+1f);   // bigger window
    public void CanvasFurther()   => NudgeDepth(-1f);   // smaller window

    /// <summary>Persist the display-window shape.</summary>
    public void SaveCanvas()
    {
        PlayerPrefs.SetFloat(PP_CSX,  canvasScaleX);
        PlayerPrefs.SetFloat(PP_CSY,  canvasScaleY);
        PlayerPrefs.SetFloat(PP_CDEP, canvasDepth);
        PlayerPrefs.Save();
    }

    public void ResetCanvas()
    {
        canvasScaleX = 1f;
        canvasScaleY = 1f;
        canvasDepth  = 1f;
    }
}
