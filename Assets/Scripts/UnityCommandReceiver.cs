using UnityEngine;
using UnityEngine.UI;
using Unity.WebRTC;

/// <summary>
/// Receives high-level UI commands from the user study manager
/// (SEW-Geometric-Teleop/projects/inspire_hand_teleop/XRT_user_study_manager.py) over the
/// 'unity_cmds' WebRTC data channel and dispatches them to the relevant scene components.
///
/// The manager's "Unity Commands" buttons send JSON payloads of the form:
///   {"type":"unity_command","command":"toggle_audio_haptics","enabled":bool}
///   {"type":"unity_command","command":"toggle_vibrotactile_haptics","enabled":bool}
///   {"type":"unity_command","command":"show_streaming_viewport","enabled":bool}
///
/// Wiring:
///   - WebRTCController.OnDataChannel routes the server-created 'unity_cmds' channel here
///     via OnUnityCommandChannelReceived().
///   - Drop this component on a persistent scene GameObject (e.g. the WebRTC Haptics
///     Receiver) and assign the target references below (auto-detected on Start if null).
///
/// Targets:
///   - Audio / vibrotactile haptics -> WebRTCHapticReceiver
///   - Streaming display            -> MediaMTXReceiver (stereo ZED feed in the ZEDv2 scene),
///                                     falling back to WebRTCController if no MediaMTXReceiver
///                                     is present.
/// </summary>
public class UnityCommandReceiver : MonoBehaviour
{
    [Header("Targets (auto-detected on Start if left null)")]
    [Tooltip("Drives vibrotactile (bHaptics) and audio (piano-note) haptics.")]
    public WebRTCHapticReceiver hapticReceiver;

    [Tooltip("Stereo ZED streaming display (mediamtx/WHEP). Primary target for show_streaming_viewport.")]
    public MediaMTXReceiver stereoReceiver;

    [Tooltip("Fallback streaming display (single WebRTC video track) used only if neither the " +
             "CloverUI toggle nor a MediaMTXReceiver is assigned.")]
    public WebRTCController webRTCController;

    [Tooltip("Optional: the CloverUI 'Streaming Display' toggle (StreamingDisplayButton). When " +
             "assigned, show_streaming_viewport drives this toggle so the remote command behaves " +
             "exactly like a button press AND the in-VR checkbox stays in sync. Takes priority " +
             "over the receiver references below.")]
    public Toggle streamingDisplayToggle;

    [Header("Debug")]
    public bool showDebugLogs = true;

    [System.Serializable]
    private class UnityCommandMessage
    {
        public string type;
        public string command;
        public bool enabled;
    }

    void Start()
    {
        if (hapticReceiver == null)
        {
            hapticReceiver = FindFirstObjectByType<WebRTCHapticReceiver>();
        }
        if (stereoReceiver == null)
        {
            // Include inactive: the streaming display (VideoStreamingViewport) is hidden/inactive
            // by default, so a default search would miss its MediaMTXReceiver.
            stereoReceiver = FindFirstObjectByType<MediaMTXReceiver>(FindObjectsInactive.Include);
        }
        if (webRTCController == null)
        {
            webRTCController = FindFirstObjectByType<WebRTCController>();
        }
    }

    /// <summary>
    /// Called by WebRTCController when the 'unity_cmds' data channel is received from the server.
    /// </summary>
    public void OnUnityCommandChannelReceived(RTCDataChannel channel)
    {
        channel.OnMessage = bytes =>
        {
            string message = System.Text.Encoding.UTF8.GetString(bytes);
            // Data-channel callbacks can fire off the main thread; marshal back before
            // touching GameObjects / Unity APIs.
            UnityMainThreadDispatcher.Instance().Enqueue(() => HandleMessage(message));
        };

        if (showDebugLogs)
        {
            Debug.Log($"[UnityCommandReceiver] '{channel.Label}' channel connected.");
        }
    }

    void HandleMessage(string message)
    {
        UnityCommandMessage cmd;
        try
        {
            cmd = JsonUtility.FromJson<UnityCommandMessage>(message);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[UnityCommandReceiver] Failed to parse command '{message}': {e.Message}");
            return;
        }

        if (cmd == null || string.IsNullOrEmpty(cmd.command))
        {
            return;
        }

        if (showDebugLogs)
        {
            Debug.Log($"[UnityCommandReceiver] Command '{cmd.command}' enabled={cmd.enabled}");
        }

        switch (cmd.command)
        {
            case "toggle_audio_haptics":
                if (hapticReceiver != null)
                {
                    hapticReceiver.SetAudioHapticsEnabled(cmd.enabled);
                }
                else
                {
                    Debug.LogWarning("[UnityCommandReceiver] No WebRTCHapticReceiver assigned; ignoring toggle_audio_haptics.");
                }
                break;

            case "toggle_vibrotactile_haptics":
                if (hapticReceiver != null)
                {
                    hapticReceiver.SetVibrotactileEnabled(cmd.enabled);
                }
                else
                {
                    Debug.LogWarning("[UnityCommandReceiver] No WebRTCHapticReceiver assigned; ignoring toggle_vibrotactile_haptics.");
                }
                break;

            case "show_streaming_viewport":
                // Prefer driving the CloverUI toggle: this fires the exact same
                // onValueChanged -> MediaMTXReceiver.ToggleVideoStream the in-VR button uses,
                // and keeps the checkbox visually in sync. Setting isOn to its current value is
                // a no-op (Unity only fires on change), so the explicit receiver call below
                // guarantees the action when the toggle is unavailable.
                if (streamingDisplayToggle != null)
                {
                    streamingDisplayToggle.isOn = cmd.enabled;
                }
                else if (stereoReceiver != null)
                {
                    stereoReceiver.ToggleVideoStream(cmd.enabled);
                }
                else if (webRTCController != null)
                {
                    webRTCController.ToggleVideoStream(cmd.enabled);
                }
                else
                {
                    Debug.LogWarning("[UnityCommandReceiver] No streaming display target assigned; ignoring show_streaming_viewport.");
                }
                break;

            default:
                Debug.LogWarning($"[UnityCommandReceiver] Unknown command '{cmd.command}'.");
                break;
        }
    }
}
