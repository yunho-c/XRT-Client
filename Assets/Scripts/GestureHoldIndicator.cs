using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A radial "loading ring" shown at the player's hand while a hold-to-activate gesture is held,
/// mirroring the Quest system menu (pinch-and-hold) ring that fills over the hold duration.
///
/// Self-contained: it builds its own world-space canvas, background ring and radial-fill ring at
/// runtime (procedural ring sprite), follows <see cref="followTarget"/> (the hand), and billboards
/// to <see cref="faceCamera"/>. Driven by GestureUIController via Show()/SetProgress()/Hide().
/// </summary>
[DisallowMultipleComponent]
public class GestureHoldIndicator : MonoBehaviour
{
    [Header("Placement")]
    [Tooltip("Fallback transform the ring is centered on (the hand wrist) if no skeleton palm is available.")]
    public Transform followTarget;

    [Tooltip("Hand skeleton used to center the ring on the actual palm joint. Strongly preferred over followTarget.")]
    public OVRSkeleton handSkeleton;

    [Tooltip("Camera the ring faces. Falls back to Camera.main if null.")]
    public Camera faceCamera;

    [Tooltip("Metres to float the ring from the hand toward the camera (so the hand mesh doesn't cover it).")]
    public float towardCameraOffset = 0.03f;

    [Tooltip("If no Palm bone exists, blend wrist->middle-finger by this fraction (0=wrist, 1=knuckle).")]
    [Range(0f, 1f)] public float wristToKnuckleBlend = 0.6f;

    [Tooltip("Fallback local offset from followTarget toward the palm if the skeleton is unavailable (hand-local metres).")]
    public Vector3 palmLocalOffset = new Vector3(0f, 0f, 0.06f);

    [Tooltip("World-space diameter of the ring, in metres.")]
    public float diameter = 0.05f;

    [Header("Style")]
    public Color fillColor = new Color(1f, 1f, 1f, 0.95f);
    public Color backgroundColor = new Color(1f, 1f, 1f, 0.20f);
    [Range(0.5f, 0.95f)] public float innerRadiusFrac = 0.78f;

    private Canvas _canvas;
    private Image _fill;
    private bool _visible;

    // Cached palm-resolution targets from the hand skeleton.
    private Transform _palmBone;     // direct Palm joint (OpenXR skeleton), if present
    private Transform _wristBone;    // fallback: wrist
    private Transform _middleBone;   // fallback: middle-finger knuckle
    private bool _bonesResolved;

    void Awake()
    {
        BuildUI();
        SetVisible(false);
    }

    void BuildUI()
    {
        var canvasGO = new GameObject("RingCanvas", typeof(RectTransform));
        canvasGO.transform.SetParent(transform, false);
        _canvas = canvasGO.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;

        var rt = (RectTransform)canvasGO.transform;
        rt.sizeDelta = new Vector2(100f, 100f);
        float s = diameter / 100f;          // 100 canvas units == diameter metres
        rt.localScale = new Vector3(s, s, s);

        var sprite = BuildRingSprite(128, innerRadiusFrac, 0.98f);

        var bg = NewImage("Background", canvasGO.transform, sprite, backgroundColor);
        bg.type = Image.Type.Simple;

        _fill = NewImage("Fill", canvasGO.transform, sprite, fillColor);
        _fill.type = Image.Type.Filled;
        _fill.fillMethod = Image.FillMethod.Radial360;
        _fill.fillOrigin = (int)Image.Origin360.Top;
        _fill.fillClockwise = true;
        _fill.fillAmount = 0f;
    }

    static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    // Procedural anti-aliased ring (annulus) sprite, white with alpha.
    static Sprite BuildRingSprite(int size, float innerFrac, float outerFrac)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        float c = (size - 1) * 0.5f, rOut = outerFrac * c, rIn = innerFrac * c, edge = 1.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                float a;
                if (d < rIn)       a = Mathf.Clamp01((d - (rIn - edge)) / edge);
                else if (d > rOut) a = Mathf.Clamp01(((rOut + edge) - d) / edge);
                else               a = 1f;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    /// <summary>Show the ring and reset progress to 0.</summary>
    public void Show()
    {
        SetVisible(true);
        SetProgress(0f);
        UpdateTransform();
    }

    /// <summary>Hide the ring.</summary>
    public void Hide() => SetVisible(false);

    /// <summary>Set fill 0..1.</summary>
    public void SetProgress(float t)
    {
        if (_fill != null) _fill.fillAmount = Mathf.Clamp01(t);
    }

    void SetVisible(bool v)
    {
        _visible = v;
        if (_canvas != null) _canvas.enabled = v;
    }

    void LateUpdate()
    {
        if (_visible) UpdateTransform();
    }

    void UpdateTransform()
    {
        var cam = faceCamera != null ? faceCamera : Camera.main;

        Vector3 pos = ResolveHandPoint();
        if (cam != null) pos += (cam.transform.position - pos).normalized * towardCameraOffset;
        transform.position = pos;

        if (cam != null) transform.rotation = cam.transform.rotation; // billboard to view
    }

    // Where to center the ring: prefer the actual palm joint, then a wrist->knuckle blend,
    // then a hand-local offset from the wrist fallback transform.
    Vector3 ResolveHandPoint()
    {
        ResolveBones();

        if (_palmBone != null)
            return _palmBone.position;

        if (_wristBone != null && _middleBone != null)
            return Vector3.Lerp(_wristBone.position, _middleBone.position, wristToKnuckleBlend);

        if (followTarget != null)
            return followTarget.TransformPoint(palmLocalOffset);

        return transform.position;
    }

    void ResolveBones()
    {
        if (_bonesResolved || handSkeleton == null) return;
        if (!handSkeleton.IsInitialized || handSkeleton.Bones == null || handSkeleton.Bones.Count == 0) return;

        foreach (var b in handSkeleton.Bones)
        {
            if (b == null || b.Transform == null) continue;
            string id = b.Id.ToString();
            if (id.Contains("Palm")) { _palmBone = b.Transform; }
            else if (id.Contains("Wrist")) { _wristBone = b.Transform; }
            else if (_middleBone == null && id.Contains("Middle")) { _middleBone = b.Transform; }
        }
        // Resolved once the skeleton is populated (whatever subset of bones it exposes).
        _bonesResolved = true;
    }
}
