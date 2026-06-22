using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives the CloverUI settings panel:
///   - Settings button toggles panel visibility
///   - Haptics toggle enables/disables vibrotactile (bHaptics) output
///   - Audio Haptics toggle switches audio (piano-note) haptics
///   - Avatar toggle shows/hides the player avatar (ghost outline) during teleop
/// Both haptics toggles drive the same gates the remote study manager toggles (UnityCommandReceiver).
/// </summary>
public class SettingsController : MonoBehaviour
{
    [Header("References")]
    public WebRTCHapticReceiver hapticReceiver;
    public GameObject           settingsPanel;
    public TeleopVisualsController teleopVisuals;

    [Header("Toggles")]
    public Toggle hapticsToggle;
    public Toggle audioHapticsToggle;
    public Toggle avatarVisualsToggle;

    void Start()
    {
        if (settingsPanel != null) settingsPanel.SetActive(false);

        if (hapticReceiver != null)
        {
            if (hapticsToggle      != null) hapticsToggle.isOn      = hapticReceiver.vibrotactileEnabled;
            if (audioHapticsToggle != null) audioHapticsToggle.isOn = hapticReceiver.useAudioHaptics;
        }
        if (avatarVisualsToggle != null && teleopVisuals != null)
            avatarVisualsToggle.isOn = teleopVisuals.showAvatarDuringTeleop;

        if (hapticsToggle       != null) hapticsToggle.onValueChanged.AddListener(SetHapticsEnabled);
        if (audioHapticsToggle  != null) audioHapticsToggle.onValueChanged.AddListener(SetAudioHapticsEnabled);
        if (avatarVisualsToggle != null) avatarVisualsToggle.onValueChanged.AddListener(SetAvatarVisualsEnabled);
    }

    public void ToggleSettingsPanel()
    {
        if (settingsPanel != null)
            settingsPanel.SetActive(!settingsPanel.activeSelf);
    }

    public void SetSettingsPanelVisible(bool visible)
    {
        if (settingsPanel != null)
            settingsPanel.SetActive(visible);
    }

    public void SetHapticsEnabled(bool isOn)
    {
        // Drive the same vibrotactile gate the remote study manager uses, so the
        // in-VR menu and UnityCommandReceiver stay consistent. (Toggling the whole
        // component's 'enabled' did not stop the running haptic coroutine.)
        if (hapticReceiver != null) hapticReceiver.SetVibrotactileEnabled(isOn);
    }

    public void SetAudioHapticsEnabled(bool isOn)
    {
        if (hapticReceiver != null) hapticReceiver.SetAudioHapticsEnabled(isOn);
    }

    public void SetAvatarVisualsEnabled(bool isOn)
    {
        // ON = show the ghost-outline avatar during teleop; OFF = hide it entirely.
        if (teleopVisuals != null) teleopVisuals.SetShowAvatarDuringTeleop(isOn);
    }
}
