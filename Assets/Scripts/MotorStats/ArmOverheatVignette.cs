using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders a split-screen red vignette in the operator's XR view to warn of arm
/// motor overheating. Left panel fires for the left arm; right panel for the right arm.
///
/// Behaviour (Unitree G1 max motor temp = 80 °C):
///   • 90 % (72 °C): vignette fades in, opacity ∝ severity.
///   • 95 % (76 °C): vignette begins flashing (1 s on / 1 s off).
///   • Returns to solid (no flash) once all arm motors drop below 95 %.
///   • Hides completely once all motors drop below 90 %.
///
/// Setup: This script auto-builds the required Canvas/Image hierarchy on Awake.
/// Attach it to any persistent GameObject in the ZEDv2 scene (e.g. OVRHmd or a
/// dedicated manager).  Assign vrCamera in the Inspector (should be the same
/// camera referenced in WebRTCController).
/// </summary>
[RequireComponent(typeof(MotorStatsReceiver))]
public class ArmOverheatVignette : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("The VR / XR camera the vignette canvas is anchored to.")]
    public Camera vrCamera;

    [Header("Vignette Appearance")]
    [Tooltip("Maximum alpha of the vignette at 100 % overheat.")]
    [Range(0f, 1f)]
    public float maxAlpha = 0.75f;

    [Tooltip("Width of the gradient band (fraction of panel width, 0.1 = thin sliver, 1.0 = full panel).")]
    [Range(0.1f, 1f)]
    public float gradientWidth = 0.8f;

    [Tooltip("Vignette colour (leave red).")]
    public Color vignetteColor = new Color(1f, 0f, 0f, 1f);

    [Header("Flash Timing")]
    [Tooltip("Seconds the vignette is visible per flash cycle.")]
    public float flashOnSeconds  = 1f;
    [Tooltip("Seconds the vignette is hidden per flash cycle.")]
    public float flashOffSeconds = 1f;

    // ── Internals ─────────────────────────────────────────────────────────────
    private MotorStatsReceiver _receiver;
    private Canvas    _canvas;
    private RawImage  _leftImage, _rightImage;
    private Texture2D _leftTex,   _rightTex;

    private float _leftSeverity,  _rightSeverity;
    private bool  _leftFlashing,  _rightFlashing;
    private bool  _leftVisible = true, _rightVisible = true;

    private Coroutine _leftFlashCo,  _rightFlashCo;

    private const int TEX_W = 256, TEX_H = 4;

    void Awake()
    {
        _receiver = GetComponent<MotorStatsReceiver>();
    }

    void Start()
    {
        if (vrCamera == null)
            vrCamera = Camera.main;

        BuildCanvas();
        _receiver.OnMotorStatsUpdated += HandleStats;
    }

    void OnDestroy()
    {
        if (_receiver != null)
            _receiver.OnMotorStatsUpdated -= HandleStats;
        Destroy(_leftTex);
        Destroy(_rightTex);
    }

    // ── Canvas construction ───────────────────────────────────────────────────
    void BuildCanvas()
    {
        var go = new GameObject("OverheatVignetteCanvas");
        go.transform.SetParent(transform, false);

        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceCamera;
        _canvas.worldCamera  = vrCamera;
        _canvas.planeDistance= 0.35f;      // just beyond the ZED quad near clip
        _canvas.sortingOrder = 999;        // above everything else

        go.AddComponent<CanvasScaler>();
        go.AddComponent<GraphicRaycaster>().enabled = false;

        _leftTex  = MakeGradientTex(leftToRight: false);
        _rightTex = MakeGradientTex(leftToRight: true);

        _leftImage  = CreatePanel(go, "LeftVignette",  _leftTex,  new Vector2(0f,   0f), new Vector2(0.5f, 1f));
        _rightImage = CreatePanel(go, "RightVignette", _rightTex, new Vector2(0.5f, 0f), new Vector2(1f,   1f));

        // Start hidden
        SetPanelAlpha(_leftImage,  0f);
        SetPanelAlpha(_rightImage, 0f);
    }

    /// <summary>Generates a 256×4 gradient: opaque at one edge, transparent at the other.</summary>
    Texture2D MakeGradientTex(bool leftToRight)
    {
        var tex = new Texture2D(TEX_W, TEX_H, TextureFormat.RGBA32, false);
        tex.wrapMode  = TextureWrapMode.Clamp;
        tex.filterMode= FilterMode.Bilinear;

        Color baseColor = vignetteColor;
        Color[] pixels  = new Color[TEX_W * TEX_H];

        float fadeStart = leftToRight ? (1f - gradientWidth) : 0f;
        float fadeEnd   = leftToRight ? 1f                   : gradientWidth;

        for (int x = 0; x < TEX_W; x++)
        {
            float t = x / (float)(TEX_W - 1);
            float alpha;
            if (leftToRight)
                alpha = Mathf.InverseLerp(fadeStart, fadeEnd, t);
            else
                alpha = Mathf.InverseLerp(fadeEnd, fadeStart, t);

            alpha = Mathf.SmoothStep(0f, 1f, alpha);
            Color c = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            for (int y = 0; y < TEX_H; y++)
                pixels[y * TEX_W + x] = c;
        }

        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    static RawImage CreatePanel(GameObject canvasGO, string name, Texture2D tex,
                                Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject(name);
        go.transform.SetParent(canvasGO.transform, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var img = go.AddComponent<RawImage>();
        img.texture = tex;
        img.color   = Color.white;
        img.raycastTarget = false;
        return img;
    }

    // ── Stats handler ─────────────────────────────────────────────────────────
    void HandleStats(MotorStatsReceiver.ArmStats left, MotorStatsReceiver.ArmStats right)
    {
        float leftMaxTemp  = MotorStatsReceiver.MaxOf(left.temps);
        float rightMaxTemp = MotorStatsReceiver.MaxOf(right.temps);

        _leftSeverity  = MotorStatsReceiver.OverheatSeverity(leftMaxTemp);
        _rightSeverity = MotorStatsReceiver.OverheatSeverity(rightMaxTemp);

        bool shouldFlashLeft  = leftMaxTemp  >= MotorStatsReceiver.OVERHEAT_FLASH_FRAC * MotorStatsReceiver.MOTOR_MAX_TEMP_C;
        bool shouldFlashRight = rightMaxTemp >= MotorStatsReceiver.OVERHEAT_FLASH_FRAC * MotorStatsReceiver.MOTOR_MAX_TEMP_C;

        UpdatePanel(ref _leftFlashCo,  ref _leftFlashing,  ref _leftVisible,  _leftImage,  _leftSeverity,  shouldFlashLeft);
        UpdatePanel(ref _rightFlashCo, ref _rightFlashing, ref _rightVisible, _rightImage, _rightSeverity, shouldFlashRight);
    }

    void UpdatePanel(ref Coroutine flashCo, ref bool isFlashing, ref bool isVisible,
                     RawImage img, float severity, bool shouldFlash)
    {
        if (severity <= 0f)
        {
            // Below 90 % threshold — hide completely
            if (isFlashing)
            {
                StopCoroutine(flashCo);
                flashCo    = null;
                isFlashing = false;
            }
            isVisible = true;
            SetPanelAlpha(img, 0f);
            return;
        }

        float targetAlpha = severity * maxAlpha;

        if (shouldFlash && !isFlashing)
        {
            isFlashing = true;
            flashCo    = StartCoroutine(FlashLoop(img, () => severity * maxAlpha));
        }
        else if (!shouldFlash && isFlashing)
        {
            StopCoroutine(flashCo);
            flashCo    = null;
            isFlashing = false;
            isVisible  = true;
            SetPanelAlpha(img, targetAlpha);
        }
        else if (!isFlashing)
        {
            SetPanelAlpha(img, targetAlpha);
        }
        // If already flashing, the coroutine reads severity dynamically via the lambda.
    }

    IEnumerator FlashLoop(RawImage img, System.Func<float> alphaGetter)
    {
        while (true)
        {
            // ON phase
            SetPanelAlpha(img, alphaGetter());
            yield return new WaitForSeconds(flashOnSeconds);
            // OFF phase
            SetPanelAlpha(img, 0f);
            yield return new WaitForSeconds(flashOffSeconds);
        }
    }

    static void SetPanelAlpha(RawImage img, float alpha)
    {
        Color c = img.color;
        c.a       = alpha;
        img.color = c;
    }
}
