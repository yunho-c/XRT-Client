using UnityEngine;
using System.Collections;

/// <summary>
/// Shows/hides a UI panel from a held hand pose (e.g. RockRollPoseLeft), wired via an
/// Oculus.Interaction SelectorUnityEventWrapper:
///   _whenSelected   -> OnGestureDetected (pose entered)
///   _whenUnselected -> OnGestureLost     (pose exited)
///
/// Behaviour (toggleMode):
///   - OPEN  requires holding the pose continuously for <see cref="holdToOpenSeconds"/>.
///           Releasing the pose before that cancels the open.
///   - CLOSE is immediate: while the UI is open, performing the pose again closes it at once.
/// </summary>
public class GestureUIController : MonoBehaviour
{
    [SerializeField] private GameObject uiPanel; // Reference to your CanvasRoot
    [SerializeField] private bool toggleMode = true; // Toggle on/off or show while gesture active

    [Header("Hold To Open")]
    [Tooltip("Seconds the pose must be held continuously before the UI opens. Closing stays immediate.")]
    [SerializeField] private float holdToOpenSeconds = 1.0f;

    [Tooltip("Optional radial progress ring shown at the hand while holding to open (Quest-style).")]
    [SerializeField] private GestureHoldIndicator holdIndicator;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip gestureDetectedSound; // played when the UI opens
    [SerializeField] private AudioClip gestureLostSound;     // played when the UI closes

    [Header("Cooldown")]
    [Tooltip("Minimum seconds between gesture actions; debounces pose flicker.")]
    [SerializeField] private float detectionCooldown = 1.0f;

    private bool isUIVisible = true; // synced to uiPanel state on Start
    private float lastActionTime = float.NegativeInfinity;
    private Coroutine _openRoutine;

    void Start()
    {
        if (uiPanel != null)
            isUIVisible = uiPanel.activeSelf;
    }

    /// <summary>Pose entered (SelectorUnityEventWrapper._whenSelected).</summary>
    public void OnGestureDetected()
    {
        if (Time.time - lastActionTime < detectionCooldown)
            return;

        if (toggleMode && isUIVisible)
        {
            // UI already open -> close immediately when the pose is performed again.
            lastActionTime = Time.time;
            CancelPendingOpen();
            SetUI(false);
            PlaySound(gestureLostSound);
            return;
        }

        // UI closed (or momentary mode) -> only open after the pose is held long enough.
        lastActionTime = Time.time;
        CancelPendingOpen();
        _openRoutine = StartCoroutine(OpenAfterHold());
    }

    /// <summary>Pose exited (SelectorUnityEventWrapper._whenUnselected).</summary>
    public void OnGestureLost()
    {
        // Released before the hold completed -> abort the pending open.
        CancelPendingOpen();

        // Momentary (non-toggle) mode hides as soon as the pose ends.
        if (!toggleMode)
            SetUI(false);
    }

    private IEnumerator OpenAfterHold()
    {
        // Pose must remain held for the whole duration; OnGestureLost cancels this coroutine.
        if (holdIndicator != null) holdIndicator.Show();

        float elapsed = 0f;
        while (elapsed < holdToOpenSeconds)
        {
            elapsed += Time.deltaTime;
            if (holdIndicator != null) holdIndicator.SetProgress(elapsed / holdToOpenSeconds);
            yield return null;
        }

        _openRoutine = null;
        if (holdIndicator != null) holdIndicator.Hide();
        SetUI(true);
        PlaySound(gestureDetectedSound);
        // Reset the cooldown from the moment it opens so a post-open pose flicker
        // can't immediately close it.
        lastActionTime = Time.time;
    }

    private void CancelPendingOpen()
    {
        if (_openRoutine != null)
        {
            StopCoroutine(_openRoutine);
            _openRoutine = null;
        }
        if (holdIndicator != null) holdIndicator.Hide();
    }

    private void SetUI(bool visible)
    {
        isUIVisible = visible;
        if (uiPanel != null)
            uiPanel.SetActive(visible);
    }

    private void PlaySound(AudioClip clip)
    {
        if (audioSource != null && clip != null)
            audioSource.PlayOneShot(clip);
    }

    // Public helpers (e.g. for UI buttons / other events).
    public void ShowUI()
    {
        CancelPendingOpen();
        SetUI(true);
    }

    public void HideUI()
    {
        CancelPendingOpen();
        SetUI(false);
    }
}
