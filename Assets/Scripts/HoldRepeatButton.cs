using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

/// <summary>
/// Fires <see cref="onPress"/> once when pressed, then repeatedly while held (after a short
/// initial delay). Works with the CloverUI poke/ray interaction since those deliver standard
/// EventSystem pointer events. Put this on a UI element that has a raycast-target Graphic.
/// </summary>
[RequireComponent(typeof(UnityEngine.UI.Graphic))]
public class HoldRepeatButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    [Tooltip("Invoked once on the initial press (the tap).")]
    public UnityEvent onPress;

    [Tooltip("Invoked on each auto-repeat tick while the button is HELD (after the initial delay) " +
             "when useOnRepeat is true. Lets a button do a fine step on tap (onPress) and a coarse " +
             "step on hold (onRepeat).")]
    public UnityEvent onRepeat;

    [Tooltip("When true, held auto-repeats fire onRepeat instead of onPress. Default false keeps the " +
             "plain hold-to-repeat behaviour (repeats fire onPress).")]
    public bool useOnRepeat = false;

    [Tooltip("Seconds held before auto-repeat begins.")]
    public float initialDelay = 0.4f;

    [Tooltip("Seconds between repeats while held.")]
    public float repeatInterval = 0.10f;

    private bool _held;
    private float _nextFire;

    public void OnPointerDown(PointerEventData e)
    {
        _held = true;
        onPress?.Invoke();           // the tap
        _nextFire = Time.unscaledTime + initialDelay;
    }

    public void OnPointerUp(PointerEventData e)   => _held = false;
    public void OnPointerExit(PointerEventData e) => _held = false;

    void OnDisable() => _held = false;

    void Update()
    {
        if (_held && Time.unscaledTime >= _nextFire)
        {
            // Repeat tick: coarse action when opted in, else the plain onPress repeat.
            if (useOnRepeat && onRepeat != null)
                onRepeat.Invoke();
            else
                onPress?.Invoke();
            _nextFire = Time.unscaledTime + repeatInterval;
        }
    }
}
