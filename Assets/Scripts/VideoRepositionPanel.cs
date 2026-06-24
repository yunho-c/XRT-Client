using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// CloverUI sub-panel for adjusting the stereo video, built procedurally (so it survives scene
/// rollbacks). Two modes, driven by <see cref="mode"/>:
///
///   Feed   ("Align Video")   — repositions the camera feed INSIDE the window:
///       ▲▼◀▶ pan the feed up/down/left/right,  + / − zoom in / out,  Save / Reset.
///       Used to line the avatar outline up with the robot's hands in the feed.
///
///   Canvas ("Resize Display") — reshapes the display WINDOW itself:
///       ▲▼ stretch taller/shorter,  ◀▶ stretch narrower/wider,  + / − bigger/smaller
///       (closer/further),  Save / Reset.
///
/// All adjustments go through ZEDFOVFiller, applied identically to both eyes so stereo stays
/// aligned. Put this on an (initially inactive) RectTransform under the CloverUI CanvasRoot; the
/// settings button toggles that GameObject active.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public class VideoRepositionPanel : MonoBehaviour
{
    public enum AdjustMode { Feed, Canvas }

    [Tooltip("Feed = reposition the camera feed inside the window. Canvas = reshape the display window.")]
    public AdjustMode mode = AdjustMode.Feed;

    [Tooltip("The video driver. Auto-found (incl. inactive) if left null.")]
    public ZEDFOVFiller fovFiller;

    [Header("Style")]
    public Color panelColor  = new Color(0.07f, 0.07f, 0.09f, 0.92f);
    public Color buttonColor = new Color(1f, 1f, 1f, 0.14f);
    public Color zoomColor   = new Color(0.20f, 0.40f, 0.62f, 1f);
    public Color ipdColor    = new Color(0.45f, 0.30f, 0.62f, 1f);
    public Color saveColor   = new Color(0.16f, 0.55f, 0.30f, 1f);
    public Color resetColor  = new Color(0.62f, 0.20f, 0.20f, 1f);
    public Color textColor   = new Color(1f, 1f, 1f, 0.92f);

    private TMP_Text _readout;
    private bool _built;

    void Awake()
    {
        if (fovFiller == null)
            fovFiller = FindFirstObjectByType<ZEDFOVFiller>(FindObjectsInactive.Include);
        BuildUI();
    }

    void Update()
    {
        if (_readout == null || fovFiller == null) return;
        if (mode == AdjustMode.Feed)
            _readout.text = $"Zoom {fovFiller.UserZoom:0.00}\n" +
                            $"X {fovFiller.UserOffsetX:+0.000;-0.000}\n" +
                            $"Y {fovFiller.UserOffsetY:+0.000;-0.000}\n" +
                            $"IPD {fovFiller.IPDShift:+0.000;-0.000}";
        else
            _readout.text = $"W {fovFiller.CanvasScaleX:0.00}\n" +
                            $"H {fovFiller.CanvasScaleY:0.00}\n" +
                            $"Size {fovFiller.CanvasDepth:0.00}";
    }

    void BuildUI()
    {
        if (_built) return;
        _built = true;
        bool feed = mode == AdjustMode.Feed;

        var root = (RectTransform)transform;
        Stretch((RectTransform)NewImage("Background", root, panelColor, true).transform);

        NewLabel("Title", root, feed ? "Align Video" : "Resize Display", 16f, new Vector2(0f, 148f), new Vector2(220f, 28f));

        // Arrow cross. Feed = pan; Canvas = stretch.
        var arrow = BuildTriangleSprite(96);
        MakeArrow("Up",    new Vector2(0f,  104f),   0f, arrow, feed ? Act(() => fovFiller?.PanUp())        : Act(() => fovFiller?.StretchTaller()));
        MakeArrow("Down",  new Vector2(0f,  -12f), 180f, arrow, feed ? Act(() => fovFiller?.PanDown())      : Act(() => fovFiller?.StretchShorter()));
        MakeArrow("Left",  new Vector2(-74f, 46f),  90f, arrow, feed ? Act(() => fovFiller?.PanLeft())      : Act(() => fovFiller?.StretchNarrower()));
        MakeArrow("Right", new Vector2(74f,  46f), -90f, arrow, feed ? Act(() => fovFiller?.PanRight())     : Act(() => fovFiller?.StretchWider()));

        _readout = NewLabel("Readout", root, "", 12f, new Vector2(0f, 46f), new Vector2(120f, 70f));

        // +/- buttons. Feed = zoom; Canvas = closer/further. Hold-to-repeat.
        MakeButton("Minus", "-", zoomColor, new Vector2(-46f, -66f), new Vector2(56f, 44f), true,
            feed ? Act(() => fovFiller?.ZoomOut()) : Act(() => fovFiller?.CanvasFurther()));
        MakeButton("Plus",  "+", zoomColor, new Vector2(46f,  -66f), new Vector2(56f, 44f), true,
            feed ? Act(() => fovFiller?.ZoomIn())  : Act(() => fovFiller?.CanvasCloser()));

        // Save / Reset (single press).
        MakeButton("Save",  "Save",  saveColor,  new Vector2(-54f, -126f), new Vector2(92f, 40f), false,
            feed ? Act(() => fovFiller?.SaveReposition())  : Act(() => fovFiller?.SaveCanvas()));
        MakeButton("Reset", "Reset", resetColor, new Vector2(54f,  -126f), new Vector2(92f, 40f), false,
            feed ? Act(() => fovFiller?.ResetReposition()) : Act(() => fovFiller?.ResetCanvas()));

        // Feed only: IPD +/- column stacked on the right (controls per-eye convergence so the
        // magnified stereo stays fusible — fixes the blur/double-vision when zooming).
        if (feed)
        {
            NewLabel("IPDLabel", root, "IPD", 11f, new Vector2(96f, 6f), new Vector2(40f, 16f));
            MakeButton("IPDPlus",  "+", ipdColor, new Vector2(96f, -22f), new Vector2(38f, 40f), true,
                Act(() => fovFiller?.IPDUp()));
            MakeButton("IPDMinus", "-", ipdColor, new Vector2(96f, -64f), new Vector2(38f, 40f), true,
                Act(() => fovFiller?.IPDDown()));
        }
    }

    static UnityEngine.Events.UnityAction Act(System.Action a) => new UnityEngine.Events.UnityAction(a);

    // ── builders ────────────────────────────────────────────────────────────────

    void MakeArrow(string name, Vector2 pos, float zRot, Sprite arrow, UnityEngine.Events.UnityAction action)
    {
        var btnImg = NewImage(name, (RectTransform)transform, buttonColor, true);
        var rt = (RectTransform)btnImg.transform;
        rt.sizeDelta = new Vector2(58f, 58f);
        rt.anchoredPosition = pos;

        var sel = btnImg.gameObject.AddComponent<Button>();      // ColorTint feedback only
        sel.transition = Selectable.Transition.ColorTint;
        sel.targetGraphic = btnImg;

        var glyph = NewImage("Glyph", rt, textColor, false);
        glyph.sprite = arrow;
        var grt = (RectTransform)glyph.transform;
        grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 0.5f);
        grt.sizeDelta = new Vector2(28f, 28f);
        grt.anchoredPosition = Vector2.zero;
        grt.localRotation = Quaternion.Euler(0f, 0f, zRot);

        var hold = btnImg.gameObject.AddComponent<HoldRepeatButton>();
        hold.onPress = new UnityEngine.Events.UnityEvent();
        hold.onPress.AddListener(action);
    }

    void MakeButton(string name, string label, Color color, Vector2 pos, Vector2 size, bool repeat, UnityEngine.Events.UnityAction action)
    {
        var img = NewImage(name, (RectTransform)transform, color, true);
        var rt = (RectTransform)img.transform;
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;

        var btn = img.gameObject.AddComponent<Button>();
        btn.transition = Selectable.Transition.ColorTint;
        btn.targetGraphic = img;
        if (repeat)
        {
            var hold = img.gameObject.AddComponent<HoldRepeatButton>();
            hold.onPress = new UnityEngine.Events.UnityEvent();
            hold.onPress.AddListener(action);
        }
        else
        {
            btn.onClick.AddListener(action);
        }

        Stretch((RectTransform)NewLabel(label + "Label", rt, label, 18f, Vector2.zero, size).transform);
    }

    Image NewImage(string name, Transform parent, Color color, bool raycast)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    TMP_Text NewLabel(string name, Transform parent, string text, float size, Vector2 pos, Vector2 sizeDelta)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (TMP_Settings.defaultFontAsset != null) t.font = TMP_Settings.defaultFontAsset;
        t.text = text;
        t.fontSize = size;
        t.color = textColor;
        t.alignment = TextAlignmentOptions.Center;
        t.raycastTarget = false;
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = sizeDelta;
        rt.anchoredPosition = pos;
        return t;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    static Sprite BuildTriangleSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            float fy = y / (float)(size - 1);
            float halfW = 0.5f * (1f - fy);
            for (int x = 0; x < size; x++)
            {
                float fx = x / (float)(size - 1) - 0.5f;
                float aa = Mathf.Clamp01((halfW - Mathf.Abs(fx)) * size * 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(aa * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    public void SetPanelVisible(bool visible) => gameObject.SetActive(visible);
    public void TogglePanel() => gameObject.SetActive(!gameObject.activeSelf);
}
