using UnityEngine;

/// <summary>
/// Toggles the CloverUI between FACE-attached (head-locked, the default) and WORLD-positioned.
///
///   Face  — the menu stays parented to the head, so it follows the player (unchanged behavior).
///   World — the menu is frozen in space. Each time it is un-hidden it re-opens directly in front
///           of the player, at the same offset/orientation it normally has head-locked (frozen
///           using head yaw only, so it doesn't tilt with head pitch/roll).
///
/// No reparenting: in World mode this pins the transform's WORLD pose every LateUpdate, which
/// overrides the head-parent movement. In Face mode it does nothing, leaving the parenting to
/// keep it head-locked.
///
/// Put this on the CloverUI GameObject (parented under CenterEyeAnchor). Wire <see cref="head"/>
/// to CenterEyeAnchor and <see cref="panel"/> to the CanvasRoot that gets hidden/shown.
/// </summary>
[DisallowMultipleComponent]
public class CloverUIPlacementMode : MonoBehaviour
{
    public enum Mode { Face, World }

    [Tooltip("Face = head-locked (follows the player). World = fixed in space, re-centers in front on un-hide.")]
    public Mode mode = Mode.Face;

    [Tooltip("Head transform (CenterEyeAnchor). Falls back to the parent / Camera.main if null.")]
    public Transform head;

    [Tooltip("The panel that gets hidden/shown (CanvasRoot). Used to re-center on every un-hide in World mode.")]
    public GameObject panel;

    [Header("World Placement Offset (from the head, metres)")]
    [Tooltip("Forward distance the menu floats in front of the player when World-locked.")]
    public float followDistance = 0.5f;

    [Tooltip("Vertical offset of the menu (− = lower, + = higher).")]
    public float heightOffset = 0f;

    [Tooltip("Sideways offset of the menu (− = left, + = right).")]
    public float lateralOffset = 0f;

    // The head-locked local pose, captured at Awake so World placement matches it exactly.
    private Vector3 _faceLocalPos;
    private Quaternion _faceLocalRot;

    private Vector3 _worldPos;
    private Quaternion _worldRot;
    private bool _placed;
    private bool _wasShown;

    void Awake()
    {
        _faceLocalPos = transform.localPosition;
        _faceLocalRot = transform.localRotation;
    }

    void Start()
    {
        if (head == null)
        {
            if (transform.parent != null) head = transform.parent;       // CenterEyeAnchor
            else if (Camera.main != null) head = Camera.main.transform;
        }
    }

    void LateUpdate()
    {
        if (mode != Mode.World) return;   // Face mode: parenting handles head-lock.

        bool shown = panel == null || panel.activeInHierarchy;
        if (shown && !_wasShown) _placed = false;   // re-center each time it is un-hidden
        _wasShown = shown;

        if (!shown) return;
        if (!_placed) PlaceInFront();
        transform.position = _worldPos;
        transform.rotation = _worldRot;
    }

    /// <summary>Freeze the menu directly in front of the player using the inspector offsets
    /// (head-yaw only, so it doesn't tilt with head pitch/roll).</summary>
    public void PlaceInFront()
    {
        if (head == null) return;
        Quaternion yaw = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
        _worldPos = head.position + yaw * new Vector3(lateralOffset, heightOffset, followDistance);
        _worldRot = yaw * _faceLocalRot;
        _placed = true;
    }

    /// <summary>Wire the settings toggle here. true = World-positioned, false = Face-attached.</summary>
    public void SetWorldMode(bool world)
    {
        mode = world ? Mode.World : Mode.Face;
        if (world)
        {
            _placed = false;
            PlaceInFront();   // snap in front of the player immediately
        }
        else
        {
            // Restore the head-locked local pose so it snaps back onto the face.
            transform.localPosition = _faceLocalPos;
            transform.localRotation = _faceLocalRot;
        }
    }

    public void ToggleMode() => SetWorldMode(mode == Mode.Face);
}
