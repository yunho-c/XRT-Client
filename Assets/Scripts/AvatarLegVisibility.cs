using UnityEngine;

/// <summary>
/// Shows/hides the avatar's legs via an inspector toggle.
///
/// The StylizedCharacter is a single SkinnedMeshRenderer driven by the Meta Movement SDK
/// CharacterRetargeter, so the legs can't be hidden by disabling a separate renderer. Instead,
/// this collapses the upper-leg bones (their whole leg chain inherits the scale), which hides the
/// leg geometry without affecting the rest of the body. Retargeting writes bone rotations, not
/// localScale, so the collapsed scale persists at runtime.
///
/// Put this on the StylizedCharacter GameObject. Tick "Show Legs" to re-enable the legs.
/// </summary>
[DisallowMultipleComponent]
public class AvatarLegVisibility : MonoBehaviour
{
    [Header("Leg bones (auto-found under Skeleton/Hips if left null)")]
    public Transform leftUpperLeg;
    public Transform rightUpperLeg;

    [Header("Visibility")]
    [Tooltip("Tick to show the legs, untick to hide them (collapses the leg bones).")]
    public bool showLegs = false;

    [Tooltip("Scale applied to the upper-leg bones when the legs are shown (rig default is 1,1,1).")]
    public Vector3 visibleScale = Vector3.one;

    [Tooltip("Re-apply every frame at runtime. Leave on so the legs stay hidden even if the " +
             "retargeter ever writes bone scale. Negligible cost.")]
    public bool enforceAtRuntime = true;

    // Near-zero (not exactly 0) avoids singular skinning matrices / NaN normals.
    const float HiddenScale = 0.0001f;

    void OnEnable()   { Apply(); }
    void Start()      { Apply(); }
    void OnValidate() { Apply(); }   // responds to the inspector toggle in edit mode

    void LateUpdate()
    {
        if (enforceAtRuntime && Application.isPlaying) Apply();
    }

    void Apply()
    {
        ResolveBones();
        Vector3 s = showLegs ? visibleScale : Vector3.one * HiddenScale;
        if (leftUpperLeg  != null && leftUpperLeg.localScale  != s) leftUpperLeg.localScale  = s;
        if (rightUpperLeg != null && rightUpperLeg.localScale != s) rightUpperLeg.localScale = s;
    }

    void ResolveBones()
    {
        if (leftUpperLeg == null)  leftUpperLeg  = transform.Find("Skeleton/Hips/Left_UpperLeg");
        if (rightUpperLeg == null) rightUpperLeg = transform.Find("Skeleton/Hips/Right_UpperLeg");
    }

    /// <summary>Show or hide the legs at runtime (e.g. from a UI event).</summary>
    public void SetLegsVisible(bool visible)
    {
        showLegs = visible;
        Apply();
    }

    /// <summary>Flip leg visibility.</summary>
    public void ToggleLegs()
    {
        SetLegsVisible(!showLegs);
    }
}
