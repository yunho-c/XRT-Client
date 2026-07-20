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

    [Tooltip("Optional: the in-VR CloverUI SettingsController. When assigned, the Haptics and Audio " +
             "Haptics toggle checkboxes are kept visually in sync whenever the study manager toggles " +
             "those gates remotely, so the menu never disagrees with the manager. Auto-detected on " +
             "Start if left null.")]
    public SettingsController settingsController;

    [Tooltip("Optional: the TeleopUIController (Power/Play-Pause buttons). When assigned, a remote " +
             "`teleop_active` command from the study manager's Start/Stop Teleop button flips the in-VR " +
             "Play/Pause button. Auto-detected on Start if left null.")]
    public TeleopUIController teleopUIController;

    [Header("Debug")]
    public bool showDebugLogs = true;

    [System.Serializable]
    private class UnityCommandMessage
    {
        public string type;
        public string command;
        public bool enabled;
        // Latency clock-sync ping fields (only present on "latency_ping"). JsonUtility leaves these
        // at 0 for other commands.
        public int ping_id;
        public double t0_ms;
    }

    // Monotonic Quest-side clock for latency timestamps. Stopwatch is high-resolution and safe to
    // read from the WebRTC callback thread (unlike UnityEngine.Time, which is main-thread only).
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double QuestNowMs() => _clock.Elapsed.TotalMilliseconds;
    private static readonly System.Globalization.CultureInfo _inv = System.Globalization.CultureInfo.InvariantCulture;

    // Throttle for periodic video-latency reports to the study manager.
    private float _nextVideoReportTime;
    private const float VideoReportInterval = 0.5f;

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
        if (settingsController == null)
        {
            settingsController = FindFirstObjectByType<SettingsController>(FindObjectsInactive.Include);
        }
        if (teleopUIController == null)
        {
            teleopUIController = FindFirstObjectByType<TeleopUIController>(FindObjectsInactive.Include);
        }

        // Mirror genuine in-VR toggle changes back to the study manager so its button
        // UI reflects what the operator does. These onValueChanged listeners fire ONLY
        // on real user interaction — remote syncs from the manager use
        // SetIsOnWithoutNotify (no event), so there is no echo back to the manager.
        // (The streaming feed's reverse report is sent from ToggleStreamingConnection,
        // which reflects the real connection state rather than mere viewport visibility.)
        if (settingsController != null)
        {
            if (settingsController.hapticsToggle != null)
            {
                settingsController.hapticsToggle.onValueChanged.AddListener(OnVibrotactileToggledByUser);
            }
            if (settingsController.audioHapticsToggle != null)
            {
                settingsController.audioHapticsToggle.onValueChanged.AddListener(OnAudioHapticsToggledByUser);
            }
        }
    }

    // Tracks the last neck-tracking state pushed to WebRTCController so Update only acts on change.
    private bool _lastNeckTracking = false;
    // Tracks the last FPV-display open/closed state reported to the manager, so Update reports only
    // on change. Baselined to the live value at the handshake so the manager's authoritative connect-
    // time push isn't fought by a spurious pre-push report.
    private bool _lastReportedDisplayVisible = false;
    // Set when the unity_cmds channel arrives; Update announces readiness to the manager once.
    private bool _announceReady = false;

    void Update()
    {
        if (webRTCController == null) return;

        // Once the manager's command channel is up, tell the manager we're ready so it pushes its
        // authoritative toggle/streaming state — AFTER the channel is open, so the push can't be
        // dropped. This is what lets the manager overwrite whatever the operator set in VR before
        // connecting (and sync the in-VR toggle UI). Fires once per (re)connection.
        if (_announceReady)
        {
            // Baseline the display-state report to the current value so the manager's authoritative
            // connect-time push (which may change it) isn't pre-empted by a stale report. Clear the
            // flag ONLY when the announce actually went out over an open unity_state channel —
            // clearing on a dropped send is how unity_ready used to be lost on every connect.
            if (stereoReceiver != null) _lastReportedDisplayVisible = stereoReceiver.videoStreamVisible;
            if (webRTCController.SendUnityState("{\"type\":\"unity_state\",\"command\":\"unity_ready\"}"))
            {
                _announceReady = false;
            }
        }

        // Keep WebRTCController's neck gate in sync with the actual stereo streaming state, so
        // body pose streams for Ostrich-neck head tracking whenever the camera feed is up — even
        // before teleop starts (look-around). Polled (a cheap bool compare) so it covers EVERY
        // way the stream can start/stop: auto-start on launch, the in-VR Streaming Connection
        // button, and the study manager's Streaming Feed button.
        if (stereoReceiver == null) return;
        // Gate neck body-pose on a STABLE "camera feed is up" signal (the viewport is active and
        // shown), NOT on the live connection state (IsStreamingActive). The connection state dips
        // briefly during every latency-watchdog resync, which would chop the body-pose stream and
        // make the neck stutter/lag pre-teleop. The viewport active+visible flags don't change
        // during a resync, so the neck stays smooth — matching the stable gate teleop uses.
        bool active = stereoReceiver.gameObject.activeInHierarchy && stereoReceiver.videoStreamVisible;
        if (active != _lastNeckTracking)
        {
            _lastNeckTracking = active;
            webRTCController.SetNeckTrackingActive(active);
        }

        // Mirror the FPV display's open/closed state back to the study manager so its toggle reflects
        // whether the video canvas is shown in the headset. Poll-based so it covers EVERY way it can
        // change (manager command, in-VR button, auto-start). Uses the STABLE videoStreamVisible flag,
        // NOT the live connection (IsStreamingActive dips on each latency-watchdog resync → would spam).
        bool displayVisible = stereoReceiver.videoStreamVisible;
        if (displayVisible != _lastReportedDisplayVisible)
        {
            _lastReportedDisplayVisible = displayVisible;
            ReportToggleState("show_streaming_viewport", displayVisible);
        }

        // Periodically report the video receive-latency breakdown (jitter buffer + decode + net) to
        // the study manager, which adds estimated capture/encode + display latency for the full
        // glass-to-glass budget.
        if (Time.unscaledTime >= _nextVideoReportTime && stereoReceiver.HasVideoLatency)
        {
            _nextVideoReportTime = Time.unscaledTime + VideoReportInterval;
            string json = "{\"type\":\"unity_state\",\"command\":\"video_latency\"" +
                          ",\"jitter_ms\":" + stereoReceiver.VideoJitterMs.ToString("F2", _inv) +
                          ",\"decode_ms\":" + stereoReceiver.VideoDecodeMs.ToString("F2", _inv) +
                          ",\"net_ms\":"    + stereoReceiver.VideoNetMs.ToString("F2", _inv) + "}";
            webRTCController.SendUnityState(json);
        }
    }

    // ── Reverse reporting (Unity → study manager) ──────────────────────────────
    // One listener per in-VR toggle; fired only on real operator interaction.

    void OnAudioHapticsToggledByUser(bool isOn)
    {
        ReportToggleState("toggle_audio_haptics", isOn);
    }

    void OnVibrotactileToggledByUser(bool isOn)
    {
        ReportToggleState("toggle_vibrotactile_haptics", isOn);
    }

    /// <summary>
    /// Send the new state of an in-VR toggle back to the study manager over the
    /// 'unity_state' channel so its button UI stays in sync with operator actions.
    /// The command strings match what the manager sends, so it can route the update
    /// to the matching button. Drops harmlessly if the channel is not open yet.
    /// </summary>
    void ReportToggleState(string command, bool enabled)
    {
        if (webRTCController == null) return;
        string json = "{\"type\":\"unity_state\",\"command\":\"" + command +
                      "\",\"enabled\":" + (enabled ? "true" : "false") + "}";
        webRTCController.SendUnityState(json);
        if (showDebugLogs)
        {
            Debug.Log($"[UnityCommandReceiver] Reported '{command}' enabled={enabled} to study manager.");
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
            // Capture the arrival time HERE (on the WebRTC thread) so the latency clock-sync isn't
            // skewed by the main-thread dispatch delay below.
            double recvMs = QuestNowMs();
            // Data-channel callbacks can fire off the main thread; marshal back before
            // touching GameObjects / Unity APIs.
            UnityMainThreadDispatcher.Instance().Enqueue(() => HandleMessage(message, recvMs));
        };

        if (showDebugLogs)
        {
            Debug.Log($"[UnityCommandReceiver] '{channel.Label}' channel connected.");
        }
        // NOTE: the study manager is authoritative on connect — it pushes all of its toggle
        // states (incl. streaming) to overwrite the XR app, so Unity does NOT report its own
        // state up at connect time (that would fight the manager's push). Runtime operator
        // changes are still mirrored back via the reverse 'unity_state' reports below.
        // Announce readiness once this channel actually OPENS (this method runs at channel
        // CREATION, before the offer is even POSTed — arming the flag here directly meant
        // Update() consumed it while unity_state was still closed and the announce was ALWAYS
        // silently dropped; the manager's state push then survived only via its timer fallbacks).
        // Update() additionally retries the send until it actually goes out (see SendUnityState).
        if (channel.ReadyState == RTCDataChannelState.Open) _announceReady = true;
        else channel.OnOpen = () => { _announceReady = true; };
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
            // Showing should always yield a LIVE canvas. EnsureLiveStream (not an IsStreamingActive
            // gate): after a spontaneous drop the pc sits in Disconnected with isConnecting latched,
            // which made IsStreamingActive read true and permanently skipped the reconnect — the
            // "manager FPV button does nothing" bug. EnsureLiveStream is idempotent on a live or
            // freshly-negotiating feed and recovers hung/dead ones. Hiding keeps the connection up
            // so a re-show is instant and low-latency.
            if (show)
            {
                stereoReceiver.EnsureLiveStream();
            }
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
        // The manager's FPV-display toggle now tracks canvas VISIBILITY (videoStreamVisible), which
        // ToggleVideoStream(true) above just set — the poll in Update() reports it, so no explicit
        // connection-state report is needed here.
    }

    /// <summary>
    /// Remote entry point for the study manager's Streaming Feed button. Drives the actual
    /// stream connection (not just visibility) so the manager can disconnect/reconnect the
    /// feed exactly like the in-VR Streaming Connection button: connect=true starts the
    /// stream, connect=false cancels it. The viewport is kept shown either way (matching the
    /// in-VR button, which does not hide on cancel so a snagged attempt can be retried).
    /// </summary>
    public void SetStreamingConnection(bool connect)
    {
        if (stereoReceiver == null)
        {
            Debug.LogWarning("[UnityCommandReceiver] No stereoReceiver assigned; ignoring SetStreamingConnection.");
            return;
        }
        // The manager's Streaming Feed button OPENS (on) / CLOSES (off) the video feed in the XR
        // app. The DISPLAY is updated unconditionally so the headset always follows the manager;
        // the CONNECTION is idempotent so the manager's connect-time push doesn't re-offer an
        // already-connected peer (which would break it → "press to retry") or tear down a live feed.
        if (connect)
        {
            stereoReceiver.ToggleVideoStream(true);                                  // open the display
            if (streamingDisplayToggle != null) streamingDisplayToggle.SetIsOnWithoutNotify(true);
            stereoReceiver.EnsureLiveStream();   // idempotent on live/negotiating; recovers hung/dead
        }
        else
        {
            // Unconditional: CancelConnection clears the supervisor's streaming intent, so an
            // explicit manager disconnect always stops the auto-reconnect loop even when the
            // feed is between retry attempts (IsStreamingActive false).
            stereoReceiver.CancelConnection("Disconnected by study manager.");
            stereoReceiver.ToggleVideoStream(false);                                 // close the display
            if (streamingDisplayToggle != null) streamingDisplayToggle.SetIsOnWithoutNotify(false);
        }
    }

    void HandleMessage(string message, double recvMs)
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

        // Latency clock-sync: echo the manager's t0 plus our receive (t1) and send (t2) times so the
        // manager can compute the round-trip on the teleop connection. Handled before the debug log
        // and the switch since it fires ~1 Hz and isn't an operator-facing command.
        if (cmd.command == "latency_ping")
        {
            if (webRTCController != null)
            {
                string pong = "{\"type\":\"unity_state\",\"command\":\"latency_pong\",\"ping_id\":" + cmd.ping_id +
                              ",\"t0_ms\":" + cmd.t0_ms.ToString("F3", _inv) +
                              ",\"t1_ms\":" + recvMs.ToString("F3", _inv) +
                              ",\"t2_ms\":" + QuestNowMs().ToString("F3", _inv) + "}";
                webRTCController.SendUnityState(pong);
            }
            return;
        }

        if (showDebugLogs)
        {
            Debug.Log($"[UnityCommandReceiver] Command '{cmd.command}' enabled={cmd.enabled}");
        }

        switch (cmd.command)
        {
            case "toggle_audio_haptics":
                // Prefer the SettingsController so the in-VR Audio Haptics checkbox follows
                // the remote state (no visual mismatch). Fall back to driving the gate
                // directly if no menu is present in this scene.
                if (settingsController != null)
                {
                    settingsController.SyncAudioHapticsToggle(cmd.enabled);
                }
                else if (hapticReceiver != null)
                {
                    hapticReceiver.SetAudioHapticsEnabled(cmd.enabled);
                }
                else
                {
                    Debug.LogWarning("[UnityCommandReceiver] No SettingsController or WebRTCHapticReceiver assigned; ignoring toggle_audio_haptics.");
                }
                break;

            case "toggle_vibrotactile_haptics":
                // Prefer the SettingsController so the in-VR Haptics checkbox follows the
                // remote state. Fall back to the gate directly if no menu is present.
                if (settingsController != null)
                {
                    settingsController.SyncHapticsToggle(cmd.enabled);
                }
                else if (hapticReceiver != null)
                {
                    hapticReceiver.SetVibrotactileEnabled(cmd.enabled);
                }
                else
                {
                    Debug.LogWarning("[UnityCommandReceiver] No SettingsController or WebRTCHapticReceiver assigned; ignoring toggle_vibrotactile_haptics.");
                }
                break;

            case "show_streaming_viewport":
                // Visibility-only (kept for back-compat); the manager now drives the
                // connection via toggle_streaming_connection below.
                SetStreamingViewport(cmd.enabled);
                break;

            case "toggle_streaming_connection":
                SetStreamingConnection(cmd.enabled);
                break;

            case "teleop_active":
                // Study manager's green Start Teleop (true) / Stop (false) button — flip the
                // in-VR Play/Pause button to match.
                if (teleopUIController != null)
                {
                    teleopUIController.SetTeleopActiveFromRemote(cmd.enabled);
                }
                else
                {
                    Debug.LogWarning("[UnityCommandReceiver] No TeleopUIController; ignoring teleop_active.");
                }
                break;

            default:
                Debug.LogWarning($"[UnityCommandReceiver] Unknown command '{cmd.command}'.");
                break;
        }
    }
}
