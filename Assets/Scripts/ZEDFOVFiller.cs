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

    private Renderer _renderer;
    private MaterialPropertyBlock _mpb;
    private Camera _cam;

    void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _mpb = new MaterialPropertyBlock();
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
        _renderer.SetPropertyBlock(_mpb);
    }
}
