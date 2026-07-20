using UnityEngine;

/// <summary>
/// Drives visuals that should differ between "menu open" and "teleoperating" (menu closed),
/// based on whether the CloverUI main menu (menuPanel) is active.
///
///   - menuOnlyVisuals : shown only while the menu is open (e.g. the ray pinch arrow between
///                       thumb and index), so they don't clutter the view during teleop.
///   - avatarVisuals   : the player avatar / ghost outline. Always shown while the menu is open;
///                       while the menu is closed it follows <see cref="showAvatarDuringTeleop"/>
///                       (driven by a settings toggle) so the operator can pick ghost-outline or
///                       nothing during teleoperation.
///
/// Must live on an always-active GameObject (NOT under the menu panel), or it would stop updating
/// the moment the menu closes.
/// </summary>
public class TeleopVisualsController : MonoBehaviour
{
    [Tooltip("The main menu panel (CloverUI CanvasRoot). Its active state == menu open.")]
    public GameObject menuPanel;

    [Tooltip("Visuals shown ONLY while the menu is open (e.g. ray pinch arrows / cursors).")]
    public GameObject[] menuOnlyVisuals;

    [Tooltip("Avatar/ghost-outline objects toggled during teleop.")]
    public GameObject[] avatarVisuals;

    [Tooltip("Show the avatar (ghost outline) while teleoperating (menu closed). Driven by the settings toggle.")]
    public bool showAvatarDuringTeleop = false;   // DEFAULT OFF (avatar overlay hidden during teleop).

    const string PP_AVATAR = "teleopShowAvatar";

    void Awake()
    {
        // Restore the operator's avatar-visibility choice across app reboots.
        showAvatarDuringTeleop = PlayerPrefs.GetInt(PP_AVATAR, showAvatarDuringTeleop ? 1 : 0) == 1;
    }

    void OnEnable() { Apply(); }
    void Update()   { Apply(); }

    void Apply()
    {
        bool menuOpen = menuPanel != null && menuPanel.activeInHierarchy;
        bool avatarVisible = menuOpen || showAvatarDuringTeleop;

        if (menuOnlyVisuals != null)
        {
            foreach (var g in menuOnlyVisuals)
                if (g != null && g.activeSelf != menuOpen) g.SetActive(menuOpen);
        }

        if (avatarVisuals != null)
        {
            foreach (var g in avatarVisuals)
                if (g != null && g.activeSelf != avatarVisible) g.SetActive(avatarVisible);
        }
    }

    /// <summary>Settings toggle entry point: show/hide the avatar during teleop.</summary>
    public void SetShowAvatarDuringTeleop(bool show)
    {
        showAvatarDuringTeleop = show;
        PlayerPrefs.SetInt(PP_AVATAR, show ? 1 : 0);   // save the choice across reboots
        PlayerPrefs.Save();
        Apply();
    }
}
