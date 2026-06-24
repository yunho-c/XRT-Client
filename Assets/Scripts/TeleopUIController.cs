using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives the CloverUI Power and Play/Pause buttons.
///
///   Power button  -> connects / disconnects the teleop server (WebRTC) ONLY; it does NOT
///                    start teleop. Its icon is highlighted (accent colour) while the button
///                    is on, dim while off. Auto-resets to the off state if the link drops.
///   Play / Pause  -> starts / pauses teleop (body-pose streaming). Usable only while
///                    connected. The icon shows what pressing it WOULD do: a "play" glyph
///                    while paused, a "pause" glyph while playing. Also signals the
///                    study-manager server (`teleop_active`) so its IK/blend engages exactly
///                    on Play instead of on connect.
///
/// Camera streaming is handled entirely by the separate StreamingConnectionButton and is
/// independent of this power/teleop flow.
/// </summary>
public class TeleopUIController : MonoBehaviour
{
    [Header("Connection")]
    [Tooltip("Auto-detected on Awake if left null.")]
    public WebRTCController webRTCController;

    [Header("Power button (connect / disconnect)")]
    public Toggle powerToggle;
    [Tooltip("The power button's icon Image — tinted to show connected (on) vs off.")]
    public Image powerIcon;
    public Color powerOffColor = new Color(1f, 1f, 1f, 0.5f);
    public Color powerOnColor  = new Color(0.30f, 0.85f, 1f, 1f);

    [Header("Play / Pause button (start / pause teleop)")]
    public Toggle playPauseToggle;
    [Tooltip("The play/pause button's icon Image — swapped between the play and pause sprites.")]
    public Image playPauseIcon;
    [Tooltip("Shown while PAUSED (pressing the button would Play).")]
    public Sprite playSprite;
    [Tooltip("Shown while PLAYING (pressing the button would Pause).")]
    public Sprite pauseSprite;

    void Awake()
    {
        if (webRTCController == null) webRTCController = FindFirstObjectByType<WebRTCController>();
    }

    void OnEnable()
    {
        if (webRTCController != null) webRTCController.OnConnectionStateChanged += HandleConnectionChanged;
        if (powerToggle     != null) powerToggle.onValueChanged.AddListener(OnPowerToggled);
        if (playPauseToggle != null) playPauseToggle.onValueChanged.AddListener(OnPlayPauseToggled);
        RefreshVisuals();
    }

    void OnDisable()
    {
        if (webRTCController != null) webRTCController.OnConnectionStateChanged -= HandleConnectionChanged;
        if (powerToggle     != null) powerToggle.onValueChanged.RemoveListener(OnPowerToggled);
        if (playPauseToggle != null) playPauseToggle.onValueChanged.RemoveListener(OnPlayPauseToggled);
    }

    // ── Power button: connect / disconnect only (never teleops) ─────────────────
    void OnPowerToggled(bool on)
    {
        if (webRTCController == null) return;
        if (on)
        {
            webRTCController.StartConnection();
        }
        else
        {
            webRTCController.StopConnection();
            ForcePause();   // turning power off also drops any teleop/play state
        }
        RefreshVisuals();
    }

    // ── Play / Pause button: start / pause teleop (requires a connection) ───────
    void OnPlayPauseToggled(bool play)
    {
        if (webRTCController == null) return;
        if (play && !webRTCController.IsConnected)
        {
            // Can't teleop without a connection — silently revert the toggle.
            if (playPauseToggle != null) playPauseToggle.SetIsOnWithoutNotify(false);
            RefreshVisuals();
            return;
        }
        webRTCController.SetTeleopActive(play);
        SignalServerTeleop(play);
        RefreshVisuals();
    }

    void ForcePause()
    {
        if (webRTCController != null) webRTCController.SetTeleopActive(false);
        if (playPauseToggle  != null) playPauseToggle.SetIsOnWithoutNotify(false);
        SignalServerTeleop(false);
    }

    /// <summary>
    /// Drive the Play/Pause state from a REMOTE command (the study manager's green Start
    /// Teleop / Stop button, over the 'unity_cmds' channel). Updates the gate + the in-VR
    /// Play/Pause button visual (play↔pause) WITHOUT sending a unity_state report back, so
    /// the manager command doesn't echo. No-op for Play while disconnected.
    /// </summary>
    public void SetTeleopActiveFromRemote(bool active)
    {
        if (active && webRTCController != null && !webRTCController.IsConnected) return;
        if (webRTCController != null) webRTCController.SetTeleopActive(active);
        if (playPauseToggle  != null) playPauseToggle.SetIsOnWithoutNotify(active);
        RefreshVisuals();
    }

    // ── Connection-state callback (from WebRTCController) ───────────────────────
    void HandleConnectionChanged(bool connected)
    {
        if (connected)
        {
            if (powerToggle != null) powerToggle.SetIsOnWithoutNotify(true);
        }
        else
        {
            // Link lost / closed: reset power + play to the off state.
            if (powerToggle     != null) powerToggle.SetIsOnWithoutNotify(false);
            if (playPauseToggle != null) playPauseToggle.SetIsOnWithoutNotify(false);
            if (webRTCController != null) webRTCController.SetTeleopActive(false);
        }
        RefreshVisuals();
    }

    // ── Server signal: engage/disengage the manager's IK exactly on Play/Pause ──
    void SignalServerTeleop(bool active)
    {
        if (webRTCController == null) return;
        string json = "{\"type\":\"unity_state\",\"command\":\"teleop_active\",\"enabled\":" +
                      (active ? "true" : "false") + "}";
        webRTCController.SendUnityState(json);
    }

    // ── Visuals ────────────────────────────────────────────────────────────────
    void RefreshVisuals()
    {
        bool powerOn = powerToggle != null && powerToggle.isOn;
        bool playing = webRTCController != null && webRTCController.IsTeleopActive;

        if (powerIcon != null)
        {
            powerIcon.color = powerOn ? powerOnColor : powerOffColor;
        }
        if (playPauseIcon != null && playSprite != null && pauseSprite != null)
        {
            // Show the glyph for the action a press WOULD perform.
            playPauseIcon.sprite = playing ? pauseSprite : playSprite;
        }
    }
}
