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

    [Tooltip("Optional: the CloverUI FPV display toggle (ToggleFPVDisplay). When assigned, its " +
             "checkbox is kept visually in sync (SetIsOnWithoutNotify) whenever SetStreamingViewport " +
             "shows/hides the display.")]
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

    /// <summary>
    /// Show/hide the stereo streaming viewport (the FPV display), repeatably. Also used by the
    /// CloverUI StreamingConnectionButton so connecting auto-opens the display.
    ///
    /// IMPORTANT: this calls <c>MediaMTXReceiver.ToggleVideoStream</c> DIRECTLY (a plain C# call)
    /// rather than routing through the CloverUI toggle's onValueChanged. The viewport's
    /// <c>stereoDisplayObject</c> is the same GameObject that hosts the <c>MediaMTXReceiver</c>, so
    /// turning it off calls <c>SetActive(false)</c> on that GameObject. After that, a UnityEvent
    /// (e.g. the toggle's persistent listener) can never reach <c>ToggleVideoStream</c> again,
    /// because UnityEvents don't fire on inactive targets — which is why the toggle "only worked
    /// once". A direct C# call still executes on a component whose GameObject is inactive, and
    /// <c>ToggleVideoStream</c> re-activates the GameObject, so this toggles on/off as many times
    /// as needed. The CloverUI checkbox is synced with SetIsOnWithoutNotify so its visual state
    /// matches without re-invoking the bypassed (self-deactivating) event chain.
    /// </summary>
    public void SetStreamingViewport(bool show)
    {
        if (stereoReceiver != null)
        {
            stereoReceiver.ToggleVideoStream(show);
        }
        else if (webRTCController != null)
        {
            webRTCController.ToggleVideoStream(show);
        }
        else
        {
            Debug.LogWarning("[UnityCommandReceiver] No streaming display target assigned; ignoring SetStreamingViewport.");
            return;
        }

        if (streamingDisplayToggle != null)
        {
            streamingDisplayToggle.SetIsOnWithoutNotify(show);
        }
    }

    /// <summary>
    /// Streaming Connection button entry point. Ensures the stereo viewport is active (so the
    /// receiver's coroutines / WebRTC update keep running) and then connects, or cancels an
    /// in-progress / established connection when pressed again — so a snagged connection can be
    /// retried without reloading the scene. Routed through this always-active component because
    /// the MediaMTXReceiver lives on the (sometimes inactive) viewport GameObject.
    /// </summary>
    public void ToggleStreamingConnection()
    {
        if (stereoReceiver == null)
        {
            Debug.LogWarning("[UnityCommandReceiver] No stereoReceiver assigned; ignoring ToggleStreamingConnection.");
            return;
        }
        // Keep the display/viewport active so the connection coroutines can run (and don't hide it
        // on cancel — that would deactivate the receiver and break the retry).
        stereoReceiver.ToggleVideoStream(true);
        if (streamingDisplayToggle != null) streamingDisplayToggle.SetIsOnWithoutNotify(true);
        stereoReceiver.ToggleConnection();
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
                SetStreamingViewport(cmd.enabled);
                break;

            default:
                Debug.LogWarning($"[UnityCommandReceiver] Unknown command '{cmd.command}'.");
                break;
        }
    }
}
