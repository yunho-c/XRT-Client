using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.WebRTC;
using UnityEngine.Networking;
using TMPro;
using UnityEngine.UI;


[System.Serializable]
public class OrientationState
{
  public float yaw;
  public float pitch;
  public float roll;
  public float fov_x = 90.0f;
}


public class WebRTCController : MonoBehaviour
{
  [Header("Signaling Server")]
  [Tooltip("Default URL of signaling server (overriden by PlayerPrefs)")]
  public string serverUrl = "http://localhost:8080/offer";

  [Header("VR Camera")]
  [Tooltip("The VR camera to track")]
  public Camera vrCamera;

  [Header("Body Tracking")]
  // #if UNITY_ANDROID
  [Tooltip("The BodyPoseProvider to get body pose data from")]
  public BodyPoseProvider bodyPoseProvider;
  [Tooltip("The AprilTag tracker to get tag pose data from")]
  public QuestAprilTagTracker aprilTagTracker;
  // #endif

  [Header("UI Elements")]
  [SerializeField] private TMP_Text statusText;
  [SerializeField] private RenderTexture videoRenderTexture;
  [SerializeField] private Material videoMaterial;
  // The TELEOP's OWN single-track video display GO (the teleop server currently sends no video,
  // so this is normally null). It must NOT be the stereo camera viewport (VideoStreamingViewport),
  // which is owned by MediaMTXReceiver — see ToggleVideoStream for why.
  [SerializeField] private GameObject videoDisplayObject;
  [SerializeField] private TMP_InputField ipAddressInputField;

  [Header("WebRTC Settings")]
  [Tooltip("Enable to automatically start the WebRTC connection on start")]
  public bool autoStartConnection = false;
  [Tooltip("Seconds to wait for the connection to establish before giving up so the power " +
           "button can't get stuck on 'connecting' (e.g. unreachable/malformed server).")]
  public float connectTimeoutSeconds = 12f;
  [Tooltip("Enable to receive video stream")]
  public bool receiveVideo = true;
  [Tooltip("Default state for video stream visibility (overridden by PlayerPrefs)")]
  public bool videoStreamVisible = true;
  [Tooltip("Use Google STUN for NAT traversal. Leave OFF for the LAN-only robot network — a cold " +
           "STUN/DNS lookup adds seconds to the FIRST connect (the classic 'second try works' failure).")]
  public bool useStun = false;
  private const ulong HIGH_WATER_MARK = 1 * 1024 * 1024; // 1 MB

  private RTCPeerConnection pc;
  private RTCDataChannel cameraChannel;
  private RTCDataChannel bodyPoseChannel;
  private RTCDataChannel aprilTagChannel;
  private RTCDataChannel unityStateChannel;
  // Manager→XR control channels. CLIENT-created (opened by the Quest in the offer) because
  // aiortc-as-answerer-created channels don't reliably surface on Unity's libwebrtc-as-offerer,
  // whereas offerer(Quest)-created channels do — the same proven path as body_pose / unity_state.
  // The study manager keeps SENDING on them via its existing send_to_datachannel calls (the server
  // stores client-created channels in the same per-peer channel map).
  private RTCDataChannel hapticsChannel;
  private RTCDataChannel unityCmdsChannel;
  private RTCDataChannel motorStatsChannel;
  private VideoStreamTrack videoTrack;
  private Coroutine _sendBodyPoseCoroutine;
  private Coroutine _connectWatchdog;
  // Tracked so StopConnection can kill in-flight signaling — otherwise a stale answer from a
  // torn-down attempt could be applied to a NEW pc (wrong ufrag/DTLS fingerprint), poisoning it.
  private Coroutine _startWebRTC;
  // True while the watchdog's silent first-attempt retry is in progress. Gates the power-button
  // auto-reset so the operator never sees the bad first attempt flicker.
  private bool _retrying = false;
  // Bumped by every StartConnection/StopConnection; the silent retry re-checks it after its wait
  // so an operator press during the retry gap always wins over the automatic retry.
  private int _connectEpoch = 0;
  // Auto-reconnect state: only a link that WAS established auto-retries (once) on Failed.
  private bool _wasEstablished = false;
  private bool _autoRetryUsed = false;

  // Teleop gate: the WebRTC connection (power button) can be up while teleop is paused.
  // Body pose is only streamed to the server while _teleopActive is true (driven by the
  // in-VR Play/Pause button via SetTeleopActive). Connecting alone does NOT teleop.
  private bool _teleopActive = false;

  // Neck gate: while the camera streaming feed is active (but BEFORE teleop starts) we still
  // stream body pose so the study manager can drive the robot's Ostrich neck from the head
  // bone — letting the operator look around with the live feed before pressing Play. The
  // manager only runs full-body IK while teleop is active, so a paused/idle robot still tracks
  // the head. Set by UnityCommandReceiver from the stereo stream's connection state.
  private bool _neckActive = false;

  /// <summary>Fired (on the main thread) when the peer connection becomes Connected (true)
  /// or drops to Disconnected/Failed/Closed (false). The UI uses this to highlight the power
  /// button and to auto-reset to the "off" state when the link is lost.</summary>
  public event System.Action<bool> OnConnectionStateChanged;

  /// <summary>True while the peer connection is established.</summary>
  public bool IsConnected =>
    pc != null && pc.ConnectionState == RTCPeerConnectionState.Connected;

  /// <summary>True while teleop is actively streaming body pose (Play, not Pause).</summary>
  public bool IsTeleopActive => _teleopActive;

  /// <summary>
  /// Enable/disable streaming body pose for neck (head) tracking while the camera feed is up
  /// but teleop has not started. Lets the operator look around before pressing Play. Body pose
  /// is sent whenever EITHER this or teleop is active.
  /// </summary>
  public void SetNeckTrackingActive(bool active) { _neckActive = active; ApplyPerfState(); }

  // Quest power/clock management. The render loop drives BOTH the WebRTC video decode AND body-pose
  // production (BodyPoseProvider fires OnPoseUpdated once per rendered frame), so any OS down-clock —
  // which the Quest applies when the operator is relatively still — shows up as BOTH camera lag AND
  // teleop/neck pose lag. Teleop needs consistent responsiveness, so pin CPU/GPU to SustainedHigh and
  // the display to its max refresh whenever the app is running (NOT just during active teleop —
  // down-clocking during connect/idle was adding pose+video latency). Power/thermals are a non-issue
  // for a tethered study. Called from Start; re-asserted on neck/teleop changes (idempotent).
  private void ApplyPerfState()
  {
    try
    {
      OVRManager.suggestedCpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
      OVRManager.suggestedGpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
      var avail = OVRPlugin.systemDisplayFrequenciesAvailable;
      if (avail != null && avail.Length > 0)
      {
        float max = 0f;
        foreach (var f in avail) if (f > max) max = f;
        if (max > 0f) OVRPlugin.systemDisplayFrequency = max;
      }
    }
    catch (System.Exception e)
    {
      Debug.LogWarning($"[WebRTCController] ApplyPerfState failed: {e.Message}");
    }
  }

  // Use a single volatile variable to store the latest pose data.
  // This avoids queuing and accumulating latency.
  private volatile byte[] _latestBodyPoseData = null;
  private readonly object _bodyPoseDataLock = new object();


  void Start()
  {
    string savedUrl = PlayerPrefs.GetString("serverUrl");
    if (!string.IsNullOrEmpty(savedUrl))
    {
      serverUrl = savedUrl;
    }
    // Ensure the URL has an http(s):// scheme. A host like "mel06293d:8080/offer" with no
    // scheme is malformed — the offer POST can't connect and the power button hangs at
    // "trying to connect". Normalising here (and re-saving) fixes a bad stored value.
    serverUrl = NormalizeServerUrl(serverUrl);
    PlayerPrefs.SetString("serverUrl", serverUrl);
    PlayerPrefs.Save();

    // Populate the VideoStreaming address field at launch from the (freshly loaded) pose IP.
    // MediaMTXReceiver owns that field but its GameObject starts inactive, so its own Start() does
    // not run until the video display is first shown — do it here so the field is correct up front.
    var media = FindFirstObjectByType<MediaMTXReceiver>(FindObjectsInactive.Include);
    if (media != null) media.InitializeBaseAddress();

    // Set the initial Quest clock state (idle until neck/teleop go active; see ApplyPerfState).
    ApplyPerfState();

    // Load video stream visibility setting
    bool savedVideoVisible = PlayerPrefs.GetInt("videoStreamVisible", videoStreamVisible ? 1 : 0) == 1;
    ToggleVideoStream(savedVideoVisible);

    statusText.text = "Ready to connect.";

    if (ipAddressInputField != null)
    {
      if (!string.IsNullOrEmpty(serverUrl))
      {
        // Extract IP address from the server URL
        try
        {
          System.Uri uri = new System.Uri(serverUrl);
          ipAddressInputField.text = uri.Host;
        }
        catch (System.Exception e)
        {
          Debug.LogError("Error parsing server URL: " + e.Message);
        }
      }
    }

    if (autoStartConnection)
    {
      StartConnection();
    }
  }

  void OnEnable()
  {
    // #if UNITY_ANDROID
    if (bodyPoseProvider != null)
    {
      bodyPoseProvider.OnPoseUpdated += OnBodyPoseUpdated;
    }
    if (aprilTagTracker != null)
    {
      aprilTagTracker.OnTagsDetected += OnAprilTagsDetected;
    }
    // #endif
  }

  void OnDisable()
  {
    // #if UNITY_ANDROID
    if (bodyPoseProvider != null)
    {
      bodyPoseProvider.OnPoseUpdated -= OnBodyPoseUpdated;
    }
    if (aprilTagTracker != null)
    {
      aprilTagTracker.OnTagsDetected -= OnAprilTagsDetected;
    }
    if (_sendBodyPoseCoroutine != null)
    {
      StopCoroutine(_sendBodyPoseCoroutine);
      _sendBodyPoseCoroutine = null;
    }
    // #endif
  }

  void Update()
  {
    if (cameraChannel != null && cameraChannel.ReadyState == RTCDataChannelState.Open)
    {
      SendOrientation();
    }
    // if (videoTrack != null && videoTrack.Enabled)
    // {
    //   // NOTE: WebRTC.Update() invokes texture update for video tracks
    //   // Debug.Log("Updated texture");
    //   WebRTC.Update();
    // }
  }

  public void SetServerIp(string ipAddress)
  {
    serverUrl = "http://" + ipAddress + ":8080/offer";
    PlayerPrefs.SetString("serverUrl", serverUrl);
    PlayerPrefs.Save();
    statusText.text = $"Server URL set to: {serverUrl}";
    Debug.Log("Server URL set to: " + serverUrl);

    // Keep the VideoStreaming address in sync: it auto-derives from this pose IP as
    // "{ip}:8889/zed_stereo" so the operator enters the host only once. This makes the pose the
    // source of truth again (clearing any earlier manual video override); the user can still edit
    // the VideoStreaming field afterwards, and that manual value then persists across sessions.
    // MediaMTXReceiver lives on an inactive GameObject, so search inactive too.
    var media = FindFirstObjectByType<MediaMTXReceiver>(FindObjectsInactive.Include);
    if (media != null) media.ApplyPoseDerivedBase(ipAddress);
  }

  /// <summary>Prepend http:// when the server URL has no scheme, so a host like
  /// "mel06293d:8080/offer" doesn't break the offer POST.</summary>
  private string NormalizeServerUrl(string url)
  {
    if (string.IsNullOrEmpty(url)) return "http://localhost:8080/offer";
    url = url.Trim();
    if (!url.Contains("://")) url = "http://" + url;
    return url;
  }

  public void StartConnection()
  {
    // Block a duplicate attempt for any LIVE peer connection — including the brief "New"
    // state right after CreatePeerConnection(). Two onValueChanged listeners (a leftover
    // persistent one + the runtime TeleopUIController one) can both call this in the same
    // frame; starting a second peer connection would overwrite the first mid-negotiation
    // and the connection would hang. DEAD states (Closed/Failed/Disconnected) are cleaned
    // up and restarted: previously a pc parked in Disconnected (any unclean session end —
    // doff, WiFi loss, manager restart; libwebrtc can sit there forever without reaching
    // Failed) silently swallowed the next Power press — the "first connect gives no
    // control, second works" bug, because Power OFF ran the cleanup the guard skipped.
    if (pc != null)
    {
      var st = pc.ConnectionState;
      if (st == RTCPeerConnectionState.Closed ||
          st == RTCPeerConnectionState.Failed ||
          st == RTCPeerConnectionState.Disconnected)
      {
        StopConnection();   // full cleanup (channels, coroutines, pc) before the fresh attempt
      }
      else
      {
        Debug.LogWarning($"WebRTC already {st}; ignoring duplicate StartConnection.");
        return;
      }
    }
    _connectEpoch++;
    _retrying = false;
    serverUrl = NormalizeServerUrl(serverUrl);
    statusText.text = "Starting WebRTC...";
    _startWebRTC = StartCoroutine(StartWebRTC());
    if (_connectWatchdog != null) StopCoroutine(_connectWatchdog);
    _connectWatchdog = StartCoroutine(ConnectWatchdog(false));
  }

  /// <summary>True when the core data channels are open — the session is genuinely usable.</summary>
  private bool ChannelsOpen() =>
    bodyPoseChannel   != null && bodyPoseChannel.ReadyState   == RTCDataChannelState.Open &&
    unityStateChannel != null && unityStateChannel.ReadyState == RTCDataChannelState.Open &&
    unityCmdsChannel  != null && unityCmdsChannel.ReadyState  == RTCDataChannelState.Open;

  /// <summary>True once the session is verified usable (Connected AND core channels open).</summary>
  public bool SessionUsable => IsConnected && ChannelsOpen();

  // Safety net + session verification. Stage 1: wait for Connected (connectTimeoutSeconds).
  // Stage 2: after Connected, verify the core data channels actually reach Open (up to 4s) —
  // a session can be "Connected" (ICE/DTLS up) with the channels never opening, which used to
  // present as "power lit but no neck / can't start teleop". If the first attempt isn't fully
  // usable, tear down and silently retry ONCE (the operator never sees the bad attempt); only
  // a failed retry resets the power button. Exits with zero steady-state cost when healthy.
  private IEnumerator ConnectWatchdog(bool isRetry)
  {
    float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, connectTimeoutSeconds);
    while (Time.realtimeSinceStartup < deadline && !IsConnected) yield return null;   // stage 1

    bool healthy = false;
    if (IsConnected)                                                                   // stage 2
    {
      float chDeadline = Time.realtimeSinceStartup + 4f;
      while (Time.realtimeSinceStartup < chDeadline && IsConnected && !ChannelsOpen()) yield return null;
      healthy = IsConnected && ChannelsOpen();
    }

    if (healthy)
    {
      _retrying = false;
      _connectWatchdog = null;
      yield break;
    }

    if (!isRetry)
    {
      Debug.LogWarning("[WebRTCController] First connect attempt not usable — retrying silently.");
      _connectWatchdog = null;    // clear FIRST so StopConnection() can't kill this coroutine
      StopConnection();           // bumps epoch, clears _retrying
      _retrying = true;           // set AFTER StopConnection; suppresses the power-button reset
      if (statusText != null) statusText.text = "Retrying connection...";
      int epoch = _connectEpoch;
      yield return new WaitForSeconds(0.5f);
      if (_connectEpoch != epoch) { _retrying = false; yield break; }  // operator intervened
      _startWebRTC = StartCoroutine(StartWebRTC());
      _connectWatchdog = StartCoroutine(ConnectWatchdog(true));
      yield break;                // _retrying stays true until the retry watchdog decides
    }

    _retrying = false;
    _connectWatchdog = null;
    Debug.LogWarning($"[WebRTCController] Connection not usable after retry (server: {serverUrl}).");
    if (statusText != null) statusText.text = "Connection failed. Press to retry.";
    StopConnection();
    OnConnectionStateChanged?.Invoke(false);
  }

  // One automatic reconnect after an established link hard-fails (WiFi blip, manager restart
  // finishing). Single-shot per established session (_autoRetryUsed); never re-arms teleop.
  private IEnumerator RetryOnce()
  {
    int epoch = _connectEpoch;
    yield return new WaitForSeconds(1f);
    if (_connectEpoch != epoch) yield break;   // operator already acted — stand down
    Debug.LogWarning("[WebRTCController] Link failed after being established — auto-reconnecting once.");
    StartConnection();                          // guard routes the Failed pc through StopConnection
  }

  public void StopConnection()
  {
    _connectEpoch++;            // cancels any pending silent retry
    _retrying = false;
    _wasEstablished = false;
    if (_startWebRTC != null) { StopCoroutine(_startWebRTC); _startWebRTC = null; }
    if (_connectWatchdog != null) { StopCoroutine(_connectWatchdog); _connectWatchdog = null; }
    if (cameraChannel != null)
    {
      cameraChannel.Close();
      cameraChannel = null;
    }
    if (bodyPoseChannel != null)
    {
      bodyPoseChannel.Close();
      bodyPoseChannel = null;
    }
    if (aprilTagChannel != null)
    {
      aprilTagChannel.Close();
      aprilTagChannel = null;
    }
    if (hapticsChannel != null) { hapticsChannel.Close(); hapticsChannel = null; }
    if (unityCmdsChannel != null) { unityCmdsChannel.Close(); unityCmdsChannel = null; }
    if (motorStatsChannel != null) { motorStatsChannel.Close(); motorStatsChannel = null; }
    if (videoTrack != null)
    {
      videoTrack.Dispose();
      videoTrack = null;
    }
    if (_sendBodyPoseCoroutine != null)
    {
      StopCoroutine(_sendBodyPoseCoroutine);
      _sendBodyPoseCoroutine = null;
    }
    if (pc != null)
    {
      pc.Close();
      pc = null;
    }
    _teleopActive = false;
    statusText.text = "Disconnected.";
    Debug.Log("WebRTC connection closed.");
  }

  /// <summary>
  /// Play/Pause gate for teleop. The in-VR Play button calls this with true to begin
  /// streaming body pose (start teleoping) and false to pause (freeze — connection stays up).
  /// No-op while disconnected so Play can't stream into a dead channel.
  /// </summary>
  public void SetTeleopActive(bool active)
  {
    if (active && !IsConnected)
    {
      Debug.LogWarning("[WebRTCController] SetTeleopActive(true) ignored — not connected.");
      _teleopActive = false;
      ApplyPerfState();
      return;
    }
    _teleopActive = active;
    ApplyPerfState();
    Debug.Log($"[WebRTCController] Teleop {(active ? "started" : "paused")}.");
  }

  public void ToggleConnection(bool isOn)
  {
    if (isOn)
    {
      StartConnection();
    }
    else
    {
      StopConnection();
    }
  }

public void ToggleVideoStream(bool isOn)
  {
    if (videoTrack != null)
    {
      videoTrack.Enabled = isOn;
    }

    // NEVER toggle the active state of the stereo camera viewport from here. That GameObject is
    // owned by MediaMTXReceiver, and deactivating it stops MediaMTXReceiver's WebRTC frame pump
    // (started in Start, runs once per activation) — which leaves a frozen white overlay on the
    // FPV display when it's reopened. videoDisplayObject is meant to be the teleop's OWN video
    // display; guard against a mis-wired reference to the camera viewport just in case.
    if (videoDisplayObject != null && videoDisplayObject.GetComponent<MediaMTXReceiver>() == null)
    {
      videoDisplayObject.SetActive(isOn);
    }

    // Save the video stream visibility setting
    PlayerPrefs.SetInt("videoStreamVisible", isOn ? 1 : 0);
    PlayerPrefs.Save();
  }

  // #if UNITY_ANDROID
  private void OnBodyPoseUpdated(BodyPoseProvider.PoseData poseData)
  {
    // This method is called from the body tracking thread.
    // We serialize the data and store it in a volatile variable.
    // The sending coroutine on the main thread will pick it up.
    if (poseData.bones != null && poseData.bones.Count > 0)
    {
      byte[] binaryData = SerializePoseData(poseData);
      lock (_bodyPoseDataLock)
      {
        _latestBodyPoseData = binaryData;
      }
    }
  }

  /// <summary>
  /// Send a JSON UI-state report to the study manager over the 'unity_state' data
  /// channel (Unity → server). Used by <see cref="UnityCommandReceiver"/> to mirror
  /// in-VR toggle changes back to the manager's button UI. Returns true only when the
  /// message was actually handed to an OPEN channel — callers that must not lose their
  /// report (e.g. the unity_ready announce) retry until this succeeds; fire-and-forget
  /// callers can ignore the result (drops silently when the channel isn't open).
  /// </summary>
  public bool SendUnityState(string json)
  {
    if (unityStateChannel != null && unityStateChannel.ReadyState == RTCDataChannelState.Open)
    {
      try
      {
        unityStateChannel.Send(System.Text.Encoding.UTF8.GetBytes(json));
        return true;
      }
      catch (System.Exception e)
      {
        Debug.LogWarning($"[WebRTCController] unity_state send failed: {e.Message}");
        return false;
      }
    }
    return false;
  }

  private void OnAprilTagsDetected(QuestAprilTagTracker.TagResult[] tags)
  {
      if (aprilTagChannel != null && aprilTagChannel.ReadyState == RTCDataChannelState.Open)
      {
          if (tags == null || tags.Length == 0)
          {
              // Option A: Could send an empty payload to indicate no tags, 
              // or Option B: just drop the frame. We will send empty sequence so the receiver knows the tag is lost.
              byte[] emptyData = SerializeAprilTagData(new QuestAprilTagTracker.TagResult[0]);
              if (aprilTagChannel.BufferedAmount < HIGH_WATER_MARK)
              {
                  aprilTagChannel.Send(emptyData);
              }
              return;
          }

          byte[] binaryData = SerializeAprilTagData(tags);
          if (binaryData != null && aprilTagChannel.BufferedAmount < HIGH_WATER_MARK)
          {
              aprilTagChannel.Send(binaryData);
          }
      }
  }

  // #endif

  private IEnumerator StartWebRTC()
  {
    CreatePeerConnection();

    // Add video transceiver if video is enabled
    if (receiveVideo)
    {
      var videoTransceiver = pc.AddTransceiver(TrackKind.Video);
      videoTransceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;
    }

    // Create data channel
    cameraChannel = pc.CreateDataChannel("camera");
    SetupDataChannelEvents(cameraChannel);

    // Create pose data channel: Unreliable and Unordered
    // This is critical for low-latency real-time data.
    var bodyPoseChannelOptions = new RTCDataChannelInit()
    {
      ordered = false,
      maxRetransmits = 0
    };
    bodyPoseChannel = pc.CreateDataChannel("body_pose", bodyPoseChannelOptions);
    SetupBodyPoseDataChannel(bodyPoseChannel);

    aprilTagChannel = pc.CreateDataChannel("apriltag_pose", bodyPoseChannelOptions);
    SetupDataChannelEvents(aprilTagChannel);

    // Reverse UI-state channel (Unity → study manager). Reliable/ordered (default
    // options) because these are discrete one-shot toggle reports that must not be
    // dropped — the manager mirrors them onto its button UI. Client-created so the
    // server picks it up via its 'unity_state' datachannel handler (same pattern as
    // body_pose / apriltag_pose).
    unityStateChannel = pc.CreateDataChannel("unity_state");
    SetupDataChannelEvents(unityStateChannel);

    // Manager→XR control channels, CLIENT-created here so they ride the offer's SCTP section and use
    // the proven offerer-opens-channel path (answerer/server-opened channels don't reliably reach
    // Unity's libwebrtc — this is why these previously never applied). The study manager keeps sending
    // on them unchanged. haptics is unreliable/unordered (avoid head-of-line blocking → low haptic
    // latency); unity_cmds and motor_stats are reliable/ordered. Route each to its receiver exactly as
    // the old OnDataChannel switch did, but with an inactive-inclusive search — MotorStatsReceiver
    // lives on an inactive GameObject, so a plain FindObjectOfType would miss it.
    var hapticsChannelOptions = new RTCDataChannelInit() { ordered = false, maxRetransmits = 0 };
    hapticsChannel = pc.CreateDataChannel("haptics", hapticsChannelOptions);
    unityCmdsChannel = pc.CreateDataChannel("unity_cmds");
    motorStatsChannel = pc.CreateDataChannel("motor_stats");

    var hapticReceiver = FindFirstObjectByType<WebRTCHapticReceiver>(FindObjectsInactive.Include);
    if (hapticReceiver != null) hapticReceiver.OnHapticsChannelReceived(hapticsChannel);
    else SetupDataChannelEvents(hapticsChannel);

    var commandReceiver = FindFirstObjectByType<UnityCommandReceiver>(FindObjectsInactive.Include);
    if (commandReceiver != null) commandReceiver.OnUnityCommandChannelReceived(unityCmdsChannel);
    else SetupDataChannelEvents(unityCmdsChannel);

    var motorStatsReceiver = FindFirstObjectByType<MotorStatsReceiver>(FindObjectsInactive.Include);
    if (motorStatsReceiver != null) motorStatsReceiver.OnMotorStatsChannelReceived(motorStatsChannel);
    else SetupDataChannelEvents(motorStatsChannel);

    // Create offer
    var offer = pc.CreateOffer();
    yield return offer;

    if (offer.IsError)
    {
      Debug.LogError("Error creating offer: " + offer.Error.message);
      yield break;
    }

    var desc = offer.Desc;
    var localDescOp = pc.SetLocalDescription(ref desc);
    yield return localDescOp;

    if (localDescOp.IsError)
    {
      Debug.LogError("Error setting local description: " + localDescOp.Error.message);
      yield break;
    }

    // This signaling is single-shot (one POST, answer in the response — NO trickle ICE), and
    // aiortc cannot form check pairs from a candidate-less offer (it would have to wait for
    // peer-reflexive discovery). So wait (bounded) for ICE gathering and POST the LIVE local
    // description, which includes the gathered host candidates. With STUN off (LAN default)
    // gathering completes in tens of ms; the 1.5s cap bounds any pathological case.
    float gatherEnd = Time.realtimeSinceStartup + 1.5f;
    while (pc.GatheringState != RTCIceGatheringState.Complete && Time.realtimeSinceStartup < gatherEnd)
      yield return null;
    string offerSdp;
    try { offerSdp = pc.LocalDescription.sdp; }                        // includes gathered candidates
    catch (System.InvalidOperationException) { offerSdp = desc.sdp; }  // fallback == pre-gather offer

    // Send offer to server
    statusText.text = $"Sending offer to {serverUrl}...";
    Debug.Log($"[WebRTCController] Initiating WebRTC signaling to URL: {serverUrl}");
    SignalingMessage offerMessage = new SignalingMessage { type = "offer", sdp = offerSdp };
    string jsonOffer = JsonUtility.ToJson(offerMessage);

    using (UnityWebRequest www = new UnityWebRequest(serverUrl, "POST"))
    {
      byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonOffer);
      www.uploadHandler = new UploadHandlerRaw(bodyRaw);
      www.downloadHandler = new DownloadHandlerBuffer();
      www.SetRequestHeader("Content-Type", "application/json");
      // Bounded so an unreachable/wrong server can't hang the offer POST forever. Kept BELOW
      // the watchdog window (0.7x) so a stage-1 retry can never overlap a still-live POST.
      www.timeout = Mathf.Max(1, Mathf.CeilToInt(connectTimeoutSeconds * 0.7f));

      yield return www.SendWebRequest();

      if (www.result != UnityWebRequest.Result.Success)
      {
        Debug.LogError("Error sending offer: " + www.error);
        statusText.text = $"Error sending offer: {www.error}";
        yield break;
      }

      statusText.text = "Offer sent, waiting for answer...";
      string jsonAnswer = www.downloadHandler.text;
      SignalingMessage answerMessage = JsonUtility.FromJson<SignalingMessage>(jsonAnswer);
      // Inlined (not a detached StartCoroutine) so stopping _startWebRTC also stops the
      // answer application — a stale answer must never touch a newer attempt's pc.
      yield return OnGotAnswer(answerMessage.sdp);
    }
  }

  private void CreatePeerConnection()
  {
    var configuration = GetSelectedSdpSemantics();
    pc = new RTCPeerConnection(ref configuration);
    Debug.Log("Peer Connection created.");

    pc.OnConnectionStateChange = state =>
    {
      Debug.Log("Connection state changed to: " + state);
      if (state == RTCPeerConnectionState.Connected)
      {
        UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
              statusText.text = "Peers connected!";
              // NOTE: the watchdog is deliberately NOT stopped here — it continues into its
              // channels-open verification stage and self-terminates once the session is usable.
              _wasEstablished = true;
              _autoRetryUsed = false;
              OnConnectionStateChanged?.Invoke(true);
            });
      }
      else if (state == RTCPeerConnectionState.Disconnected ||
               state == RTCPeerConnectionState.Failed ||
               state == RTCPeerConnectionState.Closed)
      {
        // Link lost: pause teleop and notify the UI so the power button auto-resets to "off".
        _teleopActive = false;
        UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
              // Suppress the UI reset while the watchdog's silent retry is tearing down the
              // bad first attempt (the retry's own Closed event would flicker the power button).
              if (!_retrying) OnConnectionStateChanged?.Invoke(false);
              // A previously-established link that hard-FAILS gets ONE automatic reconnect.
              // Failed only — never Disconnected, which libwebrtc often self-heals in 2-5s and
              // a teardown would kill that recovery. Teleop stays off until the operator
              // presses Play again (_teleopActive was already forced false above), so the
              // robot can never re-engage without operator intent.
              if (state == RTCPeerConnectionState.Failed && _wasEstablished && !_autoRetryUsed && !_retrying)
              {
                _autoRetryUsed = true;
                StartCoroutine(RetryOnce());
              }
            });
      }
    };

    pc.OnDataChannel = channel =>
    {
      Debug.Log($"Data Channel received: {channel.Label}!");
      if (channel.Label == "camera")
      {
        cameraChannel = channel;
      }
      else if (channel.Label == "body_pose")
      {
        bodyPoseChannel = channel;
        SetupBodyPoseDataChannel(channel);

      }
      else if (channel.Label == "apriltag_pose")
      {
        aprilTagChannel = channel;
        SetupDataChannelEvents(channel);
      }
      else if (channel.Label == "haptics")
      {
        // Notify WebRTCHapticReceiver if it exists
        WebRTCHapticReceiver hapticReceiver = FindObjectOfType<WebRTCHapticReceiver>();
        if (hapticReceiver != null)
        {
          hapticReceiver.OnHapticsChannelReceived(channel);
        }
        else
        {
          SetupDataChannelEvents(channel);
        }
      }
      else if (channel.Label == "motor_stats")
      {
        MotorStatsReceiver motorStatsReceiver = FindObjectOfType<MotorStatsReceiver>();
        if (motorStatsReceiver != null)
        {
          motorStatsReceiver.OnMotorStatsChannelReceived(channel);
        }
        else
        {
          SetupDataChannelEvents(channel);
        }
      }
      else if (channel.Label == "unity_cmds")
      {
        // High-level UI commands from the user study manager (audio/vibrotactile
        // haptics toggles, streaming-display toggle).
        UnityCommandReceiver commandReceiver = FindObjectOfType<UnityCommandReceiver>();
        if (commandReceiver != null)
        {
          commandReceiver.OnUnityCommandChannelReceived(channel);
        }
        else
        {
          SetupDataChannelEvents(channel);
        }
      }
      else
      {
        SetupDataChannelEvents(channel);
      }
    };

    // The client receives the video stream
    pc.OnTrack = (RTCTrackEvent e) =>
    {
      if (e.Track.Kind == TrackKind.Video)
      {
        Debug.Log(e.Track);
        Debug.Log("Video channel created.");
        if (e.Track is VideoStreamTrack track)
        {
          videoTrack = track;
          videoTrack.OnVideoReceived += (texture) =>
              {
                Debug.Log("Received first video frame (and set texture).");
                videoMaterial.mainTexture = texture;
                StartCoroutine(WebRTC.Update());
              };
        }
      }
    };
  }

  private IEnumerator OnGotAnswer(string sdp)
  {
    if (pc == null) yield break;   // attempt was torn down while the answer was in flight
    var remoteDesc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
    var remoteDescOp = pc.SetRemoteDescription(ref remoteDesc);
    yield return remoteDescOp;

    if (remoteDescOp.IsError)
    {
      Debug.LogError("Error setting remote description on answer: " + remoteDescOp.Error.message);
    }
  }

  private void SetupDataChannelEvents(RTCDataChannel channel)
  {
    channel.OnOpen = () =>
    {
      Debug.Log($"{channel.Label} Channel is open!");
      UnityMainThreadDispatcher.Instance().Enqueue(() =>
          {
            statusText.text = $"{channel.Label} channel open.";
          });
    };

    channel.OnClose = () =>
    {
      Debug.Log($"{channel.Label} Channel is closed!");
      UnityMainThreadDispatcher.Instance().Enqueue(() =>
          {
            statusText.text = $"{channel.Label} channel closed.";
          });
    };

    channel.OnMessage = bytes =>
    {
      // Handle incoming messages if needed
      Debug.Log($"Received message on {channel.Label} channel: {System.Text.Encoding.UTF8.GetString(bytes)}");
    };
  }

  private void SetupBodyPoseDataChannel(RTCDataChannel channel)
  {
    SetupDataChannelEvents(channel);

    channel.OnOpen = () =>
    {
      Debug.Log($"{channel.Label} Channel is open!");
      UnityMainThreadDispatcher.Instance().Enqueue(() =>
          {
            statusText.text = $"{channel.Label} channel open.";
          });
      if (_sendBodyPoseCoroutine == null)
      {
        _sendBodyPoseCoroutine = StartCoroutine(SendBodyPoseCoroutine());
      }
    };
  }

  private IEnumerator SendBodyPoseCoroutine()
  {
    // Send at a fixed rate (e.g., 90 Hz) to control the data flow.
    var wait = new WaitForSeconds(1.0f / 90.0f);

    while (true)
    {
      byte[] dataToSend = null;
      lock (_bodyPoseDataLock)
      {
        // Check if there is new data since the last send.
        if (_latestBodyPoseData != null)
        {
          dataToSend = _latestBodyPoseData;
          _latestBodyPoseData = null; // Consume the data to avoid re-sending.
        }
      }

      // Send while teleop is ACTIVE (Play) OR while the camera feed is up for neck tracking
      // (look-around before teleop). When neither is set the connection stays up but no pose
      // streams, so the robot holds its last pose (freeze). The manager only runs full-body IK
      // while teleop is active; with neck-only it just tracks the head bone. Also gate on new
      // data and an uncongested buffer to keep latency low.
      // ReadyState guard + try/catch: RTCDataChannel.Send THROWS on a non-open channel, and an
      // unhandled throw kills this coroutine permanently (the handle stays non-null so OnOpen
      // won't restart it) — "connected but no neck/teleop" after any channel blip. The guard is
      // one native enum read per pending tick; the try/catch is free on the non-throw path.
      if ((_teleopActive || _neckActive) && dataToSend != null &&
          bodyPoseChannel != null && bodyPoseChannel.ReadyState == RTCDataChannelState.Open &&
          bodyPoseChannel.BufferedAmount < HIGH_WATER_MARK)
      {
        try { bodyPoseChannel.Send(dataToSend); }
        catch (System.Exception e) { Debug.LogWarning($"[WebRTCController] body_pose send failed: {e.Message}"); }
      }
      // If the buffer is full or there's no new data, we effectively "drop" the frame,
      // prioritizing low latency and sending the most recent data in the next cycle.

      yield return wait;
    }
  }

  private byte[] SerializePoseData(BodyPoseProvider.PoseData poseData)
  {
    using (var memoryStream = new MemoryStream())
    {
      using (var writer = new BinaryWriter(memoryStream))
      {
        writer.Write(poseData.bones.Count);
        foreach (var bone in poseData.bones)
        {
          writer.Write((int)bone.id);

          writer.Write(bone.position.x);
          writer.Write(bone.position.y);
          writer.Write(bone.position.z);

          writer.Write(bone.rotation.x);
          writer.Write(bone.rotation.y);
          writer.Write(bone.rotation.z);
          writer.Write(bone.rotation.w);
        }
      }
      return memoryStream.ToArray();
    }
  }

  private byte[] SerializeAprilTagData(QuestAprilTagTracker.TagResult[] tags)
  {
    using (var memoryStream = new MemoryStream())
    {
      using (var writer = new BinaryWriter(memoryStream))
      {
        writer.Write(tags.Length);
        foreach (var tag in tags)
        {
          writer.Write((int)tag.id);

          writer.Write(tag.position.x);
          writer.Write(tag.position.y);
          writer.Write(tag.position.z);

          writer.Write(tag.rotation.x);
          writer.Write(tag.rotation.y);
          writer.Write(tag.rotation.z);
          writer.Write(tag.rotation.w);
        }
      }
      return memoryStream.ToArray();
    }
  }

  private void SendOrientation()
  {
    if (vrCamera != null)
    {
      OrientationState state = new OrientationState
      {
        yaw = vrCamera.transform.eulerAngles.y,
        pitch = -vrCamera.transform.eulerAngles.x, // Invert pitch for correct mapping
        roll = vrCamera.transform.eulerAngles.z
      };
      string jsonState = JsonUtility.ToJson(state);
      cameraChannel.Send(jsonState);
    }
  }

  private void OnApplicationQuit()
  {
    PlayerPrefs.SetString("serverUrl", serverUrl);
    PlayerPrefs.Save();
    StopConnection();
  }

  // Robot-safety gate: taking the headset off (or any OS pause — system menu, app switch)
  // pauses teleop and tells the manager, so re-donning can NEVER resume pose streaming until
  // the operator explicitly presses Play (which resumes via the manager's existing blend).
  // Without this, _teleopActive stayed true across a doff and the pose stream snapped back on
  // re-don, sweeping the robot to wherever the operator's arms happened to be. If the report
  // doesn't flush before the OS suspends us, the manager's stale-pose guard still HOLDs.
  private void OnApplicationPause(bool paused)
  {
    if (paused && _teleopActive)
    {
      SetTeleopActive(false);
      SendUnityState("{\"type\":\"unity_state\",\"command\":\"teleop_active\",\"enabled\":false}");
      Debug.Log("[WebRTCController] App paused (headset doffed?) — teleop paused for safety.");
    }
  }

  private RTCConfiguration GetSelectedSdpSemantics()
  {
    // LAN default: NO ICE servers. Google STUN on a LAN-only robot network adds a cold
    // DNS+STUN stall of up to several seconds to the FIRST connect of a session (the
    // "first connect fails, second works" signature) and host candidates are all that is
    // needed on a single-hop network. Flip useStun in the Inspector for NAT deployments.
    if (useStun)
    {
      return new RTCConfiguration
      {
        iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
      };
    }
    return new RTCConfiguration { iceServers = new RTCIceServer[0] };
  }
}
