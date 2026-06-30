using System.Collections;
using UnityEngine;
using Unity.WebRTC;
using TMPro;

public class MediaMTXReceiver : MonoBehaviour
{
    [Header("WebRTC Settings")]
    [Tooltip("Enable to automatically start the WebRTC connection on start")]
    public bool autoStartConnection = false;

    // Base address for user input (e.g., "localhost:8889/stream" or "192.168.0.101:8889/zed")
    [SerializeField] private string defaultBaseAddress = "192.168.0.104:8889/zed";

    [Header("Stereo source")]
    [Tooltip("ON: ONE WebRTC connection to a single side-by-side [left|right] stream (one decoder, " +
             "perfect L/R sync, lower latency — set the base address to that stream's path, e.g. " +
             "'192.168.0.104:8889/zed_stereo'). OFF: two connections (left/right eye) — the original " +
             "dual-stream path. Requires the matching ZEDStereoPassthrough _SBS material mode.")]
    [SerializeField] private bool singleStreamSbs = true;   // DEFAULT: single SBS stream (zed_stereo).

    [Tooltip("Single-SBS only: the stitch ORIENTATION (left|right vs top/bottom) is auto-detected " +
             "from the decoded frame's shape, so it works whatever the sender produces. This toggle " +
             "just flips which half goes to which eye if the stereo looks reversed (and toggle the " +
             "material's _FlipY if the image is upside-down).")]
    [SerializeField] private bool sbsSwapEyes = false;

    [Header("UI Elements")]
    // Reference to the InputField (to load the saved URL)
    [SerializeField] private TMP_InputField ipAddressInputField;
    // Reference to the Text component (to show status)
    [SerializeField] private TMP_Text statusText;
    // Reference to the connect toggle to disable during connection
    [SerializeField] private UnityEngine.UI.Toggle connectToggle;

    // Single material with both texture slots
    [SerializeField] private Material stereoMaterial;

    // GameObject to hide/show the stereo display
    [SerializeField] private GameObject stereoDisplayObject;

    // Default state for video stream visibility
    [Tooltip("Default state for video stream visibility (overridden by PlayerPrefs)")]
    public bool videoStreamVisible = true;

    [Header("Connection")]
    [Tooltip("Seconds to wait for the WHEP offer POST before giving up (so a bad address/server can't hang forever).")]
    public float connectTimeoutSeconds = 10f;

    [Header("Latency watchdog")]
    [Tooltip("Auto-resync the stereo stream when the receiver's playout latency creeps above this many seconds, to keep the feed low-latency. The libwebrtc jitter buffer ratchets its target playout delay UP with clock drift / reordering / stalls and is slow to recover; Unity.WebRTC exposes no cap, so we flush by reconnecting. Watchdog triggers on the WORST of the experienced jitter-buffer delay and the (ratcheting) target delay, plus on freezes. Set 0 to disable.")]
    public float maxReceiveLatencySeconds = 0.18f;
    [Tooltip("How often (seconds) the watchdog samples receive latency via WebRTC stats. Lower = reacts faster to a latency ramp.")]
    public float latencyCheckIntervalSeconds = 0.5f;
    [Tooltip("Consecutive over-threshold samples required before resyncing, so a transient spike doesn't trigger a flush.")]
    public int latencyStrikesBeforeResync = 2;
    [Tooltip("Minimum seconds between resyncs. Each resync is a brief (1-3s) WHEP reconnect blackout, so this cap prevents the watchdog from thrashing reconnects on a genuinely jittery network. MANDATORY safety with the aggressive threshold above.")]
    public float minResyncIntervalSeconds = 5f;
    [Tooltip("Log per-sample jitter/target/decode/freeze latency to the console (primary eye) for on-device tuning of the threshold. Target should read ~50-500ms; if it reads wildly larger than jitter the stat normalization differs on this device. Leave off in normal use.")]
    public bool logLatencyStats = false;

    private string urlLeft;
    private string urlRight;

    private RTCPeerConnection pcLeft;
    private RTCPeerConnection pcRight;
    private MediaStream receiveStreamLeft;
    private MediaStream receiveStreamRight;

    // References to the actual video tracks
    private VideoStreamTrack _videoTrackLeft;
    private VideoStreamTrack _videoTrackRight;

    // Guard against repeated connection attempts
    private bool isConnecting = false;

    // Tracked connect coroutines so we can cancel them without killing WebRTC.Update().
    private Coroutine _coLeft;
    private Coroutine _coRight;

    // Latency-watchdog state: per-eye baseline of the cumulative stats counters, so we can compute
    // the RECENT average jitter-buffer delay and decode time from the delta between samples.
    private class EyeLatency
    {
        public double lastJbDelay;     public ulong lastJbEmitted;
        public double lastDecodeTime;  public uint  lastFramesDecoded;
        public double lastJbTarget;        // cumulative jitterBufferTargetDelay — the value that ratchets up
        public double lastFreezeDuration;  // cumulative totalFreezesDuration
        public bool   hasBaseline;
    }
    private readonly EyeLatency _latLeft  = new EyeLatency();
    private readonly EyeLatency _latRight = new EyeLatency();
    private int _latencyStrikes;
    private float _lastResyncTime = -999f;   // Time.realtimeSinceStartup of the last resync (cooldown gate)

    // Latest measured video RECEIVE-latency breakdown (ms), from the primary (left) eye's WebRTC
    // stats — read by UnityCommandReceiver and reported to the study manager for the end-to-end
    // glass-to-glass latency budget. NetMs is the camera-connection RTT/2 (one-way). These cover
    // only the receiver pipeline; capture/encode and display→photons are estimated manager-side.
    public bool  HasVideoLatency { get; private set; }
    public float VideoJitterMs   { get; private set; }
    public float VideoDecodeMs   { get; private set; }
    public float VideoNetMs      { get; private set; }

    // Cached decoder textures bound to the stereo material, set ONLY from OnVideoReceived
    // (the authoritative "a real frame arrived" signal) and cleared on disconnect. The
    // display MeshRenderer is gated on BOTH being present: while there is no live video the
    // renderer is disabled so the camera passthrough shows through (the camera clears to
    // transparent over an OVRPassthroughLayer), instead of the blend shader painting a
    // released/blank RenderTexture as a white semi-transparent overlay or an opaque black void.
    private Texture _matTexLeft;
    private Texture _matTexRight;
    private MeshRenderer _meshRenderer;

    void Start()
    {
        _meshRenderer = GetComponent<MeshRenderer>();
        // Dual-texture path = 0. In SBS mode the split mode (1-4) is set per-frame in OnVideoReceived
        // from the decoded frame's shape (auto-detected orientation), so leave it 0 here.
        if (stereoMaterial != null) stereoMaterial.SetFloat("_SBS", 0f);
        // Nothing to show until a frame arrives — keep passthrough until then.
        UpdateDisplayVisibility();

        // 1. Load saved stream visibility state and apply it
        bool savedVideoVisible = PlayerPrefs.GetInt("stereoStreamVisible", videoStreamVisible ? 1 : 0) == 1;
        ToggleVideoStream(savedVideoVisible);

        // 2. Load and set the server URL (sets the internal urlLeft/urlRight)
        string savedBaseAddress = PlayerPrefs.GetString("stereoBaseUrl", defaultBaseAddress);
        SetBaseStreamUrl(savedBaseAddress);

        // 3. Initialize Input Field and Status Text
        if (ipAddressInputField != null)
        {
            ipAddressInputField.text = savedBaseAddress;
        }
        if (statusText != null)
        {
            statusText.text = "Ready to connect.";
        }

        // Always run the global WebRTC frame pump while this receiver is alive.
        StartCoroutine(WebRTC.Update());

        // Keep the live feed low-latency over long sessions: the libwebrtc receive jitter buffer
        // slowly accumulates with sender/receiver clock drift (latency creeps up to multiple
        // seconds and only resets on reconnect). Unity.WebRTC exposes no jitter-buffer cap, so a
        // watchdog samples the receive latency and resyncs to flush it when it crosses the limit.
        StartCoroutine(LatencyWatchdog());

        // Do NOT disturb a connection that was already kicked off before Start ran. This
        // GameObject starts inactive and is activated on demand — e.g. the Streaming Connection
        // button (ToggleStreamingConnection) and the study manager (SetStreamingConnection) call
        // ToggleConnection()/StartStream() synchronously right after activating it, which runs
        // BEFORE this Start(). Re-initializing here would dispose those in-progress peer
        // connections and surface a spurious "Connection Closed. Press to retry." So only
        // auto-connect on a clean first activation (e.g. the display shown with no connect
        // already underway).
        if (!isConnecting && !IsConnected())
        {
            SetConnectToggleInteractable(true);
            InitializePeerConnections();
            Debug.Log($"Auto-starting connection to: {savedBaseAddress}");
            UpdateStatusText($"Auto-connecting to: {savedBaseAddress}...");
            StartStream();
        }
    }

    // Creates fresh peer connections (disposing any previous ones first). Does NOT start a
    // connection — call StartStream() for that. Safe to call repeatedly for clean retries.
    private void InitializePeerConnections()
    {
        if (_coLeft  != null) { StopCoroutine(_coLeft);  _coLeft  = null; }
        if (_coRight != null) { StopCoroutine(_coRight); _coRight = null; }

        pcLeft?.Close();  pcLeft?.Dispose();  pcLeft = null;
        pcRight?.Close(); pcRight?.Dispose(); pcRight = null;
        receiveStreamLeft?.Dispose();  receiveStreamLeft = null;
        receiveStreamRight?.Dispose(); receiveStreamRight = null;
        _videoTrackLeft = null;
        _videoTrackRight = null;

        // Drop references to the old (now disposed) decoder textures and hide the display
        // (show passthrough) until fresh frames arrive on reconnect — so a torn-down or
        // failed stream never lingers as a white overlay / black void on the shared material.
        _matTexLeft = null;
        _matTexRight = null;
        UpdateDisplayVisibility();

        RTCConfiguration config = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer {urls = new[] {"stun:stun.l.google.com:19302"}}
            }
        };

        // Initialize left eye stream
        pcLeft = new RTCPeerConnection(ref config);
        receiveStreamLeft = new MediaStream();

        pcLeft.OnIceConnectionChange = state =>
        {
            Debug.Log($"Left ICE Connection State: {state}");
            UpdateStatusText($"Left ICE State: {state}");
        };

        pcLeft.OnConnectionStateChange = state =>
        {
            Debug.Log($"Left Connection State: {state}");
            if (state == RTCPeerConnectionState.Connected)
            {
                // In single-stream (SBS) mode pcLeft IS the whole feed → active on connect.
                if (singleStreamSbs) ResetConnectionState("Streaming active.");
                else UpdateStatusText("Left Peer connected!");
            }
            else if (state == RTCPeerConnectionState.Failed ||
                     state == RTCPeerConnectionState.Disconnected ||
                     state == RTCPeerConnectionState.Closed)
            {
                // Only reset if right is also not connected
                if (pcRight == null ||
                    pcRight.ConnectionState == RTCPeerConnectionState.Failed ||
                    pcRight.ConnectionState == RTCPeerConnectionState.Disconnected ||
                    pcRight.ConnectionState == RTCPeerConnectionState.Closed)
                {
                    ResetConnectionState($"Connection {state}. Press to retry.");
                }
            }
        };

        pcLeft.OnTrack = e =>
        {
            receiveStreamLeft.AddTrack(e.Track);
        };

        receiveStreamLeft.OnAddTrack = e =>
        {
            if (e.Track is VideoStreamTrack videoTrack)
            {
                _videoTrackLeft = videoTrack;
                _videoTrackLeft.Enabled = videoStreamVisible;

                videoTrack.OnVideoReceived += (tex) =>
                {
                    // Smooth (bilinear) sampling + clamp so the 720p feed upscaled across the wide
                    // Quest FOV doesn't look blocky/aliased. The decoder texture can default to point.
                    if (tex != null)
                    {
                        tex.filterMode = FilterMode.Bilinear;
                        tex.wrapMode   = TextureWrapMode.Clamp;
                    }
                    _matTexLeft = tex;
                    if (stereoMaterial != null) stereoMaterial.SetTexture("_Left", tex);
                    // Single SBS stream: this one texture carries both eyes — mirror it to the right
                    // cache/slot so the renderer-gating (needs both) passes, and AUTO-DETECT the stitch
                    // orientation from the frame shape (wide = [left|right], tall = top/bottom). This
                    // is robust to whatever the sender stitches; sbsSwapEyes flips half↔eye if reversed.
                    if (singleStreamSbs)
                    {
                        _matTexRight = tex;
                        if (stereoMaterial != null && tex != null)
                        {
                            stereoMaterial.SetTexture("_Right", tex);
                            bool horizontal = tex.width >= tex.height;
                            stereoMaterial.SetFloat("_SBS",
                                horizontal ? (sbsSwapEyes ? 2f : 1f) : (sbsSwapEyes ? 4f : 3f));
                        }
                    }
                    UpdateDisplayVisibility();
                };
            }
        };

        RTCRtpTransceiverInit initLeft = new RTCRtpTransceiverInit();
        initLeft.direction = RTCRtpTransceiverDirection.RecvOnly;
        pcLeft.AddTransceiver(TrackKind.Video, initLeft);

        // Single stitched [left|right] stream: pcLeft is the entire feed — skip the right-eye
        // connection entirely (that's the whole latency/decode win).
        if (singleStreamSbs) return;

        // Initialize right eye stream
        pcRight = new RTCPeerConnection(ref config);
        receiveStreamRight = new MediaStream();

        pcRight.OnIceConnectionChange = state =>
        {
            Debug.Log($"Right ICE Connection State: {state}");
            UpdateStatusText($"Right ICE State: {state}");
        };

        pcRight.OnConnectionStateChange = state =>
        {
            Debug.Log($"Right Connection State: {state}");
            if (state == RTCPeerConnectionState.Connected)
            {
                // Both streams connected - reset the guard and re-enable button
                if (pcLeft != null && pcLeft.ConnectionState == RTCPeerConnectionState.Connected)
                {
                    ResetConnectionState("Streaming active.");
                }
                else
                {
                    UpdateStatusText("Right Peer connected! Waiting for left...");
                }
            }
            else if (state == RTCPeerConnectionState.Failed ||
                     state == RTCPeerConnectionState.Disconnected ||
                     state == RTCPeerConnectionState.Closed)
            {
                ResetConnectionState($"Connection {state}. Press to retry.");
            }
        };

        pcRight.OnTrack = e =>
        {
            receiveStreamRight.AddTrack(e.Track);
        };

        receiveStreamRight.OnAddTrack = e =>
        {
            if (e.Track is VideoStreamTrack videoTrack)
            {
                _videoTrackRight = videoTrack;
                _videoTrackRight.Enabled = videoStreamVisible;

                videoTrack.OnVideoReceived += (tex) =>
                {
                    if (tex != null)
                    {
                        tex.filterMode = FilterMode.Bilinear;
                        tex.wrapMode   = TextureWrapMode.Clamp;
                    }
                    _matTexRight = tex;
                    if (stereoMaterial != null) stereoMaterial.SetTexture("_Right", tex);
                    UpdateDisplayVisibility();
                };
            }
        };

        RTCRtpTransceiverInit initRight = new RTCRtpTransceiverInit();
        initRight.direction = RTCRtpTransceiverDirection.RecvOnly;
        pcRight.AddTransceiver(TrackKind.Video, initRight);
    }

    // Helper method to safely update the status text
    private void UpdateStatusText(string message)
    {
        if (statusText != null)
        {
            statusText.text = message;
        }
        Debug.Log($"Status: {message}");
    }

    // Helper method to safely set toggle interactable state
    private void SetConnectToggleInteractable(bool interactable)
    {
        if (connectToggle != null)
        {
            connectToggle.interactable = interactable;
        }
    }

    // Helper method to reset connection state and re-enable UI
    private void ResetConnectionState(string statusMessage)
    {
        isConnecting = false;
        SetConnectToggleInteractable(true);
        UpdateStatusText(statusMessage);
    }

    private bool IsConnected()
    {
        if (pcLeft == null || pcLeft.ConnectionState != RTCPeerConnectionState.Connected)
            return false;
        if (singleStreamSbs)
            return true;   // single stitched stream → only pcLeft is used
        return pcRight != null && pcRight.ConnectionState == RTCPeerConnectionState.Connected;
    }

    /// <summary>
    /// True while a stream connection is established or in progress. Used by the study
    /// manager's Streaming Feed button to reflect the real state at connect time (Unity
    /// auto-starts the stream on launch).
    /// </summary>
    public bool IsStreamingActive => isConnecting || IsConnected();

    /// <summary>
    /// Single entry point for the Streaming Connection button: connect when idle, or cancel the
    /// in-progress / established connection when pressed again (so a snagged attempt can be retried
    /// without reloading the scene).
    /// </summary>
    /// <returns>The new intended state: true if a connection was started, false if cancelled.</returns>
    public bool ToggleConnection()
    {
        if (isConnecting || IsConnected())
        {
            CancelConnection("Streaming connection cancelled — press to retry.");
            return false;
        }
        StartStream();
        return true;
    }

    // Public function to be called by a dedicated "Connect" button.
    public void StartStream()
    {
        // Already mid-connect: do nothing (cancel is handled by ToggleConnection).
        if (isConnecting)
        {
            Debug.Log("Connection already in progress.");
            return;
        }

        // Ensure we have fresh peer connections (recreate if missing or in a dead state). In SBS
        // mode only pcLeft is used.
        bool deadLeft  = pcLeft == null || pcLeft.ConnectionState == RTCPeerConnectionState.Closed || pcLeft.ConnectionState == RTCPeerConnectionState.Failed;
        bool deadRight = pcRight == null || pcRight.ConnectionState == RTCPeerConnectionState.Closed || pcRight.ConnectionState == RTCPeerConnectionState.Failed;
        if (deadLeft || (!singleStreamSbs && deadRight))
        {
            InitializePeerConnections();
        }

        // NOTE: intentionally do NOT disable the connect toggle here — it must stay pressable so
        // the user can press again to cancel a snagged connection.
        isConnecting = true;

        UpdateStatusText($"Connecting to {urlLeft}{(singleStreamSbs ? "" : " / " + urlRight)}...");

        _coLeft = StartCoroutine(createOffer(pcLeft, urlLeft));
        if (!singleStreamSbs)
            _coRight = StartCoroutine(createOffer(pcRight, urlRight));
    }

    /// <summary>
    /// Cancel any in-progress / established connection and re-create fresh peer connections so the
    /// next StartStream() is a clean retry. Keeps the GameObject active and WebRTC.Update running.
    /// </summary>
    public void CancelConnection(string reason = "Disconnected.")
    {
        isConnecting = false;
        SetConnectToggleInteractable(true);
        InitializePeerConnections();   // stops tracked coroutines + disposes + recreates
        UpdateStatusText(reason);
    }

    // Public method to manually stop the connection.
    public void StopStream()
    {
        CancelConnection("Disconnected.");
    }

    // ── Latency watchdog ────────────────────────────────────────────────────────
    // The libwebrtc receive jitter buffer slowly grows with sender/receiver clock drift, pushing
    // video latency up to several seconds over a session (it only resets on reconnect). Unity.WebRTC
    // 3.x exposes no jitter-buffer cap, so we sample the receive latency and resync the stream to
    // flush the buffer when it creeps past the configured limit — keeping the feed low-latency.
    private IEnumerator LatencyWatchdog()
    {
        var wait = new WaitForSeconds(Mathf.Max(0.25f, latencyCheckIntervalSeconds));
        while (true)
        {
            yield return wait;

            if (maxReceiveLatencySeconds <= 0f) { _latencyStrikes = 0; continue; }
            if (!IsConnected())
            {
                // Not fully connected: drop baselines so a fresh connection re-establishes them.
                _latencyStrikes = 0;
                _latLeft.hasBaseline = false;
                _latRight.hasBaseline = false;
                continue;
            }

            float worst = -1f;
            yield return SampleVideoStats(pcLeft,  _latLeft,  true,  v => { if (v > worst) worst = v; });
            yield return SampleVideoStats(pcRight, _latRight, false, v => { if (v > worst) worst = v; });

            if (worst < 0f) continue;   // no fresh delta yet (need two samples to measure)

            if (worst > maxReceiveLatencySeconds)
            {
                _latencyStrikes++;
                Debug.LogWarning($"[MediaMTXReceiver] Receive latency ~{worst:F2}s > {maxReceiveLatencySeconds:F2}s " +
                                 $"(strike {_latencyStrikes}/{Mathf.Max(1, latencyStrikesBeforeResync)}).");
                bool cooldownOk = (Time.realtimeSinceStartup - _lastResyncTime) > Mathf.Max(0f, minResyncIntervalSeconds);
                if (_latencyStrikes >= Mathf.Max(1, latencyStrikesBeforeResync) && cooldownOk)
                {
                    Debug.LogWarning("[MediaMTXReceiver] Flushing accumulated video latency (resync).");
                    _lastResyncTime = Time.realtimeSinceStartup;
                    _latencyStrikes = 0;
                    _latLeft.hasBaseline = false;
                    _latRight.hasBaseline = false;
                    CancelConnection("Re-syncing video to clear latency...");
                    yield return null;     // let InitializePeerConnections settle
                    StartStream();
                    yield return wait;     // give the new connection a beat before sampling again
                }
                // else: over threshold but inside the cooldown — keep the strike count latched so we
                // flush the moment the cooldown elapses, without thrashing reconnects in the meantime.
            }
            else
            {
                _latencyStrikes = 0;
            }
        }
    }

    // Sample one eye's RECENT video receive latency from WebRTC stats, derived from the delta of the
    // cumulative counters since the previous sample (so it reflects current conditions, not the
    // session average): jitter-buffer delay (seconds, the part that drifts) + decode time + the
    // candidate-pair RTT. onJitter reports the recent average jitter-buffer delay (seconds) — used by
    // the watchdog to decide a resync. When isPrimary, the full breakdown is published to the public
    // Video*Ms fields for the manager-side end-to-end budget. Re-baselines silently on reconnect.
    private IEnumerator SampleVideoStats(RTCPeerConnection pc, EyeLatency state, bool isPrimary, System.Action<float> onJitter)
    {
        if (pc == null) yield break;
        var op = pc.GetStats();
        yield return op;
        if (op.IsError || op.Value == null) yield break;

        using (var report = op.Value)
        {
            float jitterSec = -1f, decodeMs = -1f, netMs = -1f, targetSec = -1f, freezeDelta = 0f;
            foreach (var entry in report.Stats.Values)
            {
                if (entry is RTCInboundRTPStreamStats inbound && inbound.kind == "video")
                {
                    double jb = inbound.jitterBufferDelay;     ulong jbE = inbound.jitterBufferEmittedCount;
                    double dt = inbound.totalDecodeTime;       uint  fd  = inbound.framesDecoded;
                    double tgt = inbound.jitterBufferTargetDelay;   // cumulative target playout delay (ratchets up)
                    double frz = inbound.totalFreezesDuration;      // cumulative freeze time
                    if (state.hasBaseline)
                    {
                        if (jbE > state.lastJbEmitted && jb >= state.lastJbDelay)
                            jitterSec = (float)((jb - state.lastJbDelay) / (jbE - state.lastJbEmitted));
                        // jitterBufferTargetDelay is emitted-count-weighted just like jitterBufferDelay,
                        // so normalize by the SAME emitted-count delta to get the current target latency (s).
                        if (jbE > state.lastJbEmitted && tgt >= state.lastJbTarget)
                            targetSec = (float)((tgt - state.lastJbTarget) / (jbE - state.lastJbEmitted));
                        if (fd > state.lastFramesDecoded && dt >= state.lastDecodeTime)
                            decodeMs = (float)((dt - state.lastDecodeTime) / (fd - state.lastFramesDecoded) * 1000.0);
                        if (frz >= state.lastFreezeDuration)
                            freezeDelta = (float)(frz - state.lastFreezeDuration);
                    }
                    state.lastJbDelay = jb;  state.lastJbEmitted = jbE;
                    state.lastDecodeTime = dt; state.lastFramesDecoded = fd;
                    state.lastJbTarget = tgt;  state.lastFreezeDuration = frz;
                    state.hasBaseline = true;
                }
                else if (entry is RTCIceCandidatePairStats pair && pair.nominated)
                {
                    double rtt = pair.currentRoundTripTime;   // seconds (round trip)
                    if (rtt > 0) netMs = (float)(rtt * 0.5 * 1000.0);   // one-way ≈ RTT/2
                }
            }

            // Watchdog decision uses the WORST of the actual experienced jitter-buffer delay and the
            // (ratcheting) target playout delay — a multi-second spike usually lives in the target, not
            // the windowed-average experienced delay, which is why the old average-only watchdog missed it.
            // A real freeze (decoder stall / buffer step) forces an over-threshold strike so we flush fast.
            float watch = Mathf.Max(jitterSec, targetSec);
            if (watch >= 0f) onJitter(watch);
            if (freezeDelta > 0.10f) onJitter(maxReceiveLatencySeconds + 1f);

            if (isPrimary && logLatencyStats)
                Debug.Log($"[MediaMTXReceiver][lat] jitter={jitterSec * 1000f:F0}ms target={targetSec * 1000f:F0}ms " +
                          $"decode={decodeMs:F0}ms freezeD={freezeDelta * 1000f:F0}ms net={netMs:F0}ms");

            if (isPrimary)
            {
                // Telemetry stays HONEST: report the real experienced jitter-buffer delay (not the
                // watchdog's max-with-target) so the study manager's glass-to-glass budget isn't inflated.
                if (jitterSec >= 0f) VideoJitterMs = jitterSec * 1000f;
                if (decodeMs  >= 0f) VideoDecodeMs = decodeMs;
                if (netMs     >= 0f) VideoNetMs    = netMs;
                if (jitterSec >= 0f || decodeMs >= 0f) HasVideoLatency = true;
            }
        }
    }

    // Public method to be called from a UI InputField's On End Edit (String) event
    public void SetBaseStreamUrl(string baseAddressAndPort)
    {
        if (string.IsNullOrEmpty(baseAddressAndPort)) return;

        // 1. Save the new base address
        PlayerPrefs.SetString("stereoBaseUrl", baseAddressAndPort);
        PlayerPrefs.Save();

        // 2. Construct the final URLs with the "backwards" logic
        if (singleStreamSbs)
        {
            // One stitched [left|right] stream: the base address IS that stream's path.
            urlLeft = $"http://{baseAddressAndPort}/whep";
            urlRight = null;
        }
        else
        {
            urlLeft = $"http://{baseAddressAndPort}/right/whep";
            urlRight = $"http://{baseAddressAndPort}/left/whep";
        }

        UpdateStatusText($"URL set: {baseAddressAndPort}");
    }

    public void ToggleVideoStream(bool isOn)
    {
        videoStreamVisible = isOn;

        // Activate the viewport GameObject the FIRST time it's shown (it starts inactive), but
        // NEVER deactivate it on hide. Keeping it active keeps the WebRTC frame pump (started in
        // Start, which runs only once per activation) AND the connection alive across a hide/show,
        // so reopening shows LIVE video instead of a frozen white frame. Hiding is done purely by
        // disabling the MeshRenderer (passthrough) in UpdateDisplayVisibility; the decoder tracks
        // stay enabled so the texture stays live for an instant re-show.
        if (isOn && stereoDisplayObject != null && !stereoDisplayObject.activeSelf)
        {
            stereoDisplayObject.SetActive(true);
        }

        UpdateDisplayVisibility();

        PlayerPrefs.SetInt("stereoStreamVisible", isOn ? 1 : 0);
        PlayerPrefs.Save();
    }

    // Show the display only when it's meant to be visible AND both eyes have a live decoder
    // texture; otherwise disable the MeshRenderer so the transparent-clearing camera reveals
    // passthrough (instead of a white overlay from a blank/released RenderTexture, an opaque
    // black void, or a frozen frame). Once a texture is bound it keeps updating in place as
    // WebRTC pumps new frames, so no per-frame re-binding is needed.
    private void UpdateDisplayVisibility()
    {
        if (_meshRenderer != null)
        {
            _meshRenderer.enabled = videoStreamVisible && _matTexLeft != null && _matTexRight != null;
        }
    }

    private IEnumerator createOffer(RTCPeerConnection pc, string url)
    {
        var op = pc.CreateOffer();
        yield return op;
        if (op.IsError) {
            Debug.LogError($"CreateOffer() failed for {url}");
            CancelConnection($"Error creating offer for {url}. Press to retry.");
            yield break;
        }

        yield return setLocalDescription(pc, op.Desc, url);
    }

    private IEnumerator setLocalDescription(RTCPeerConnection pc, RTCSessionDescription offer, string url)
    {
        var op = pc.SetLocalDescription(ref offer);
        yield return op;
        if (op.IsError) {
            Debug.LogError($"SetLocalDescription() failed for {url}");
            CancelConnection($"Error setting local description for {url}. Press to retry.");
            yield break;
        }

        yield return postOffer(pc, offer, url);
    }

    private IEnumerator postOffer(RTCPeerConnection pc, RTCSessionDescription offer, string url)
    {
        UpdateStatusText($"Sending offer to {url}...");
        var content = new System.Net.Http.StringContent(offer.sdp);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sdp");

        float timeout = Mathf.Max(1f, connectTimeoutSeconds);
        var task = System.Threading.Tasks.Task.Run(async () => {
            using (var client = new System.Net.Http.HttpClient())
            {
                // Bounded timeout so an unreachable server can't hang the connection forever.
                client.Timeout = System.TimeSpan.FromSeconds(timeout);
                var res = await client.PostAsync(new System.UriBuilder(url).Uri, content);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync();
            }
        });
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Exception != null) {
            Debug.LogError($"PostOffer() failed for {url}: {task.Exception.InnerException?.Message ?? task.Exception.Message}");
            CancelConnection($"Connection failed: {task.Exception.InnerException?.Message ?? task.Exception.Message}. Press to retry.");
            yield break;
        }

        UpdateStatusText($"Received answer from {url}. Setting remote description...");
        yield return setRemoteDescription(pc, task.Result, url);
    }

    private IEnumerator setRemoteDescription(RTCPeerConnection pc, string answer, string url)
    {
        RTCSessionDescription desc = new RTCSessionDescription();
        desc.type = RTCSdpType.Answer;
        desc.sdp = answer;
        var op = pc.SetRemoteDescription(ref desc);
        yield return op;
        if (op.IsError) {
            Debug.LogError($"SetRemoteDescription() failed for {url}");
            CancelConnection($"Error setting remote description for {url}. Press to retry.");
            yield break;
        }

        UpdateStatusText("WebRTC negotiation complete. Waiting for stream...");
        yield break;
    }

    void OnDestroy()
    {
        // Stop all running coroutines
        StopAllCoroutines();

        // Save the current visibility state
        PlayerPrefs.SetInt("stereoStreamVisible", videoStreamVisible ? 1 : 0);

        // Save the latest successful base address
        if (!string.IsNullOrEmpty(urlLeft))
        {
             // Extract the base address part from one of the final URLs
             string baseAddress = urlLeft.Replace("http://", "").Replace("/right/whep", "");
             PlayerPrefs.SetString("stereoBaseUrl", baseAddress);
        }
        PlayerPrefs.Save();

        pcLeft?.Close();
        pcLeft?.Dispose();
        receiveStreamLeft?.Dispose();

        pcRight?.Close();
        pcRight?.Dispose();
        receiveStreamRight?.Dispose();
    }
}
