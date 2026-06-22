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
    [Tooltip("Invoked on press and then repeatedly while held.")]
    public UnityEvent onPress;

    [Tooltip("Seconds held before auto-repeat begins.")]
    public float initialDelay = 0.4f;

    [Tooltip("Seconds between repeats while held.")]
    public float repeatInterval = 0.10f;

    private bool _held;
    private float _nextFire;

    public void OnPointerDown(PointerEventData e)
    {
        _held = true;
        Fire();
        _nextFire = Time.unscaledTime + initialDelay;
    }

    public void OnPointerUp(PointerEventData e)   => _held = false;
    public void OnPointerExit(PointerEventData e) => _held = false;

    void OnDisable() => _held = false;

    void Update()
    {
        if (_held && Time.unscaledTime >= _nextFire)
        {
            Fire();
            _nextFire = Time.unscaledTime + repeatInterval;
        }
    }

    void Fire() => onPress?.Invoke();
}
