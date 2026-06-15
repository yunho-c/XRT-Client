using UnityEngine;
using Unity.WebRTC;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Bhaptics.SDK2.Glove;
using Bhaptics.SDK2;

/// <summary>
/// Receives haptic messages from WebRTC and sends them to bHaptics gloves.
///
/// Design notes (per-finger contact force haptics for teleoperation):
///   - Logarithmic continuous force-to-intensity mapping (Cellini et al. T2H
///     form): f = log(1 + alpha * p) / log(1 + alpha). Toggleable via
///     enableContinuousLogMapping. When disabled, the signal is silent between
///     transition kicks (step-pulse-only mode).
///   - Optional level-transition kicks layered on top of the continuous signal.
///     Four boundaries (no-contact -> light, light -> medium, medium -> high,
///     high -> maximum). Each level has independent kick amplitude AND pulse
///     count, so heavier levels can be encoded as multi-pulse bursts (e.g.,
///     1 pulse for light, 2 for medium, 3 for high, 4 for maximum). The whole
///     kick layer is toggleable via enableTransitionPulse.
///   - Per-finger perceptual threshold calibration via inspector sliders.
///   - Carrier frequency is fixed by the LRA resonance (~170-230 Hz for
///     bHaptics actuators, near the 250 Hz Pacinian peak). Only amplitude
///     (duty cycle) is modulated.
///   - This component is intentionally per-operator and self-contained. No
///     cross-operator coupling here; cooperative cues are handled elsewhere in
///     the system.
///
/// Supported hand sensor types:
///   Psyonic  – original format; values already 0-1.
///   Inspire  – Inspire RH56DFTP touch sensor averages normalized to 0-1 on the
///              Python side (HAPTICS_SENSOR_MAX = 1024).
/// </summary>
public class WebRTCHapticReceiver : MonoBehaviour
{
    public enum HandSensorType { Psyonic, Inspire }

    [Header("Hand Sensor Type")]
    [Tooltip("Select the robot hand whose sensor data is being streamed.")]
    public HandSensorType handSensorType = HandSensorType.Inspire;

    [Header("WebRTC Integration")]
    [Tooltip("Reference to WebRTCController to access data channels. Auto-detected if not assigned.")]
    public WebRTCController webRTCController;

    [Tooltip("Name of the haptics data channel (default: 'haptics')")]
    public string hapticsChannelName = "haptics";

    [Header("Editor Local Debug Input")]
    [Tooltip("Listen for local UDP haptics JSON for Play Mode debugging (no Quest/WebRTC peer required).")]
    public bool enableUdpDebugInput = false;

    [Tooltip("Local UDP port for debug haptics JSON packets.")]
    [Range(1024, 65535)]
    public int udpDebugPort = 8765;

    [Header("bHaptics Integration")]
    [Tooltip("The bHaptics Physics Glove component. Leave null to use singleton instance.")]
    public BhapticsPhysicsGlove bHapticsGlove;

    [Tooltip("Master toggle for vibrotactile (bHaptics glove) output. Toggled remotely by the " +
             "user study manager via the 'unity_cmds' channel (UnityCommandReceiver). Independent " +
             "of audio haptics — both, either, or neither can be active.")]
    public bool vibrotactileEnabled = true;

    [Header("Audio Haptics Mode")]
    [Tooltip("When enabled, routes fingertip forces to FingertipAudioHaptics (spatial piano notes). " +
             "Independent of vibrotactile output — enabling this no longer disables the gloves. " +
             "Toggled remotely by the user study manager via the 'unity_cmds' channel.")]
    public bool useAudioHaptics = false;
    [Tooltip("FingertipAudioHaptics component to drive when audio mode is enabled.")]
    public FingertipAudioHaptics audioHapticsPlayer;

    [Header("Sensor Floor and Saturation (normalized 0-1)")]
    [Tooltip("Force below this is treated as no contact (silent). Maps to the sensor's " +
             "physical resolution floor (~0.5 N for Inspire RH56DFTP).")]
    [Range(0f, 0.2f)]
    public float sensorFloorNormalized = 0.05f;

    [Tooltip("Normalized force at which output saturates at maximum amplitude.")]
    [Range(0.1f, 1f)]
    public float sensorSaturationNormalized = 0.85f;

    [Header("Continuous Logarithmic Mapping")]
    [Tooltip("Master toggle for the continuous logarithmic signal. When ON, the underlying " +
             "force-to-amplitude mapping is the Cellini et al. T2H log curve. When OFF, " +
             "between transition kicks the output is silent (step-pulse-only mode). Useful " +
             "for ablating continuous vs. event-only feedback.")]
    public bool enableContinuousLogMapping = true;

    [Tooltip("Logarithmic mapping curvature (Cellini et al. T2H form). " +
             "Higher = stronger low-end emphasis (more resolution near contact onset). " +
             "Recommended starting point: 9.")]
    [Range(0.1f, 30f)]
    public float alpha = 9f;

    [Tooltip("Extra response shaping for Inspire sensors. Higher values produce stronger " +
             "haptics for the same detected force.")]
    [Range(0f, 1f)]
    public float inspireIntensityPower = 0.6f;

    [Tooltip("Maximum sustained amplitude (0-100). Cap below 100 to avoid skin desensitization " +
             "and LRA overheating during long sessions.")]
    [Range(20, 100)]
    public int maxAmplitude = 95;

    [Header("Per-Finger Perceptual Calibration")]
    [Tooltip("Lower amplitude threshold for the thumb (0-100). Set once via ramp calibration: " +
             "operator presses button at first perception of vibration.")]
    [Range(0, 100)]
    public int thumbLowerThreshold = 15;

    [Tooltip("Lower amplitude threshold for the index finger (0-100).")]
    [Range(0, 100)]
    public int indexLowerThreshold = 15;

    [Tooltip("Lower amplitude threshold for the middle finger (0-100).")]
    [Range(0, 100)]
    public int middleLowerThreshold = 15;

    [Tooltip("Lower amplitude threshold for the ring finger (0-100).")]
    [Range(0, 100)]
    public int ringLowerThreshold = 15;

    [Tooltip("Lower amplitude threshold for the little finger (0-100).")]
    [Range(0, 100)]
    public int littleLowerThreshold = 15;

    [Tooltip("Lower amplitude threshold for the palm/wrist actuator (0-100).")]
    [Range(0, 100)]
    public int palmLowerThreshold = 15;

    [Tooltip("Safety margin added above lower threshold to guarantee silent region (0-100).")]
    [Range(0, 20)]
    public int thresholdMargin = 5;

    [Header("Level Transition Kicks (layered on continuous signal)")]
    [Tooltip("Master toggle for the level-transition kick layer. When ON, crossing a level " +
             "boundary fires a brief kick pulse before the continuous signal resumes. When " +
             "OFF, output is pure logarithmic continuous with no discrete cues.")]
    public bool enableTransitionPulse = true;

    [Tooltip("Normalized force boundary for LIGHT level (above no-contact, below medium).")]
    [Range(0f, 1f)]
    public float lightLevelCutoff = 0.10f;

    [Tooltip("Normalized force boundary entering MEDIUM level.")]
    [Range(0f, 1f)]
    public float mediumLevelCutoff = 0.35f;

    [Tooltip("Normalized force boundary entering HIGH level.")]
    [Range(0f, 1f)]
    public float highLevelCutoff = 0.60f;

    [Tooltip("Normalized force boundary entering MAXIMUM level.")]
    [Range(0f, 1f)]
    public float maximumLevelCutoff = 0.85f;

    [Tooltip("Kick amplitude (0-100) when force crosses INTO LIGHT (i.e., no-contact -> light).")]
    [Range(0, 100)]
    public int lightKickIntensity = 70;

    [Tooltip("Kick amplitude (0-100) when force crosses INTO MEDIUM.")]
    [Range(0, 100)]
    public int mediumKickIntensity = 80;

    [Tooltip("Kick amplitude (0-100) when force crosses INTO HIGH.")]
    [Range(0, 100)]
    public int highKickIntensity = 90;

    [Tooltip("Kick amplitude (0-100) when force crosses INTO MAXIMUM.")]
    [Range(0, 100)]
    public int maximumKickIntensity = 100;

    [Tooltip("Number of short pulses fired when crossing INTO LIGHT.")]
    [Range(1, 8)]
    public int lightPulseCount = 1;

    [Tooltip("Number of short pulses fired when crossing INTO MEDIUM.")]
    [Range(1, 8)]
    public int mediumPulseCount = 2;

    [Tooltip("Number of short pulses fired when crossing INTO HIGH.")]
    [Range(1, 8)]
    public int highPulseCount = 3;

    [Tooltip("Number of short pulses fired when crossing INTO MAXIMUM.")]
    [Range(1, 8)]
    public int maximumPulseCount = 4;

    [Tooltip("Duration of each individual kick pulse (ms).")]
    [Range(5, 120)]
    public int transitionPulseDurationMs = 35;

    [Tooltip("Silent gap between consecutive pulses in a multi-pulse burst (ms). " +
             "Keep small enough that the burst reads as one cue, large enough that " +
             "individual pulses stay countable.")]
    [Range(10, 200)]
    public int interPulseGapMs = 45;

    [Tooltip("Fire kicks on downward transitions too (e.g., high -> medium). When OFF, only " +
             "upward crossings fire kicks.")]
    public bool kickOnDownwardTransitions = false;

    [Header("Continuous Signal")]
    [Tooltip("Duration of each continuous command (ms). Should match or slightly exceed " +
             "the update period to maintain a smooth signal.")]
    [Range(5, 100)]
    public int continuousCommandDurationMs = 30;

    [Header("Common Settings")]
    [Tooltip("Enable continuous haptic updates (sends haptics every frame when values change)")]
    public bool enableContinuousHaptics = true;

    [Header("Timeout Settings")]
    [Tooltip("Timeout in seconds. If no haptic messages are received within this time, haptics will stop.")]
    [Range(0.1f, 10f)]
    public float messageTimeoutSeconds = 1.0f;

    [Tooltip("Enable timeout system. If disabled, haptics will continue using last received values.")]
    public bool enableTimeout = true;

    [Header("Debug")]
    [Tooltip("Show debug logs for received haptic messages")]
    public bool showDebugLogs = false;

    // Current haptic values for each hand
    private HapticData currentLeftHaptics = new HapticData();
    private HapticData currentRightHaptics = new HapticData();

    // Track last message receive time for timeout detection
    private float lastLeftHandMessageTime = 0f;
    private float lastRightHandMessageTime = 0f;

    // Coroutine for continuous haptic updates
    private Coroutine continuousHapticCoroutine;

    // WebRTC data channel reference
    private RTCDataChannel hapticsChannel;
    private const int FingerCount = 6;
    private const string HapticsMessageType = "haptics";

    // UDP debug input state
    private UdpClient udpDebugClient;
    private Thread udpDebugThread;
    private volatile bool udpDebugRunning;
    private readonly object udpMessageLock = new object();
    private HapticMessage pendingUdpMessage;

    /// <summary>
    /// Discrete force levels for the stepped mapping.
    /// </summary>
    private enum ForceLevel
    {
        None = 0,
        Light = 1,
        Medium = 2,
        High = 3,
        Maximum = 4
    }

    private class HandDynamicsState
    {
        // Per-finger level state for transition detection.
        public ForceLevel[] fingerLastLevel = new ForceLevel[FingerCount];

        // Per-finger active burst state. When pulsesRemaining > 0, the finger is in the
        // middle of a multi-pulse burst that overrides continuous output.
        public int[] pulsesRemaining = new int[FingerCount];
        public int[] burstKickAmplitude = new int[FingerCount];
        // Time (Time.unscaledTime seconds) at which the current pulse ON or OFF phase ends.
        public float[] phaseEndTime = new float[FingerCount];
        // True while in the ON phase of a pulse (motor driven at kick amplitude);
        // false during the inter-pulse silent gap.
        public bool[] inOnPhase = new bool[FingerCount];

        public bool wasActiveLastFrame;
    }

    private readonly HandDynamicsState leftDynamicsState = new HandDynamicsState();
    private readonly HandDynamicsState rightDynamicsState = new HandDynamicsState();

    [System.Serializable]
    private class HapticMessage
    {
        public string type;
        public double timestamp;
        public HapticData left;
        public HapticData right;
    }

    [System.Serializable]
    private class HapticData
    {
        public float thumb;
        public float index;
        public float middle;
        public float ring;
        public float little;
        public float palm;
    }

    void Start()
    {
        InitializeComponents();
        SetupHapticsChannel();
        StartUdpDebugInputIfEnabled();
    }

    void InitializeComponents()
    {
        // Auto-detect WebRTCController if not assigned
        if (webRTCController == null)
        {
            webRTCController = FindObjectOfType<WebRTCController>();
            if (webRTCController == null)
            {
                if (!enableUdpDebugInput)
                {
                    Debug.LogWarning("[WebRTCHapticReceiver] WebRTCController not found. Assign it for WebRTC input, or enable UDP debug input for local testing.");
                }
            }
        }

        // Get bHaptics glove instance if not assigned
        if (bHapticsGlove == null)
        {
            bHapticsGlove = BhapticsPhysicsGlove.Instance;
            if (bHapticsGlove == null)
            {
                BhapticsPhysicsGlove[] allGloves = FindObjectsOfType<BhapticsPhysicsGlove>();
                if (allGloves != null && allGloves.Length > 0)
                {
                    bHapticsGlove = allGloves[0];
                    if (showDebugLogs)
                    {
                        Debug.Log("[WebRTCHapticReceiver] Found BhapticsPhysicsGlove via FindObjectsOfType");
                    }
                }
            }
            else if (showDebugLogs)
            {
                Debug.Log("[WebRTCHapticReceiver] Found BhapticsPhysicsGlove singleton instance");
            }

            if (bHapticsGlove == null)
            {
                Debug.LogWarning("[WebRTCHapticReceiver] No BhapticsPhysicsGlove instance found. Haptic feedback will be disabled.");
            }
        }

        if (showDebugLogs)
        {
            Debug.Log("[WebRTCHapticReceiver] Initialized");
        }
    }

    void SetupHapticsChannel()
    {
        // Start continuous haptic coroutine if enabled
        if (enableContinuousHaptics)
        {
            continuousHapticCoroutine = StartCoroutine(ContinuousHapticUpdate());
        }
    }

    /// <summary>
    /// Called by WebRTCController when the haptics data channel is received.
    /// </summary>
    public void OnHapticsChannelReceived(RTCDataChannel channel)
    {
        if (channel.Label == hapticsChannelName)
        {
            hapticsChannel = channel;
            SetupChannelEvents(channel);
            if (showDebugLogs)
            {
                Debug.Log($"[WebRTCHapticReceiver] Haptics channel '{hapticsChannelName}' received and set up!");
            }
        }
    }


    void SetupChannelEvents(RTCDataChannel channel)
    {
        channel.OnMessage = bytes =>
        {
            try
            {
                string message = System.Text.Encoding.UTF8.GetString(bytes);
                if (showDebugLogs)
                {
                    Debug.Log($"[WebRTCHapticReceiver] Received haptic message: {message}");
                }

                // Parse JSON message
                HapticMessage hapticMsg = JsonUtility.FromJson<HapticMessage>(message);

                ApplyHapticMessage(hapticMsg);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[WebRTCHapticReceiver] Error parsing haptic message: {e.Message}");
            }
        };
    }

    void Update()
    {
        if (!enableUdpDebugInput)
            return;

        HapticMessage next = null;
        lock (udpMessageLock)
        {
            if (pendingUdpMessage != null)
            {
                next = pendingUdpMessage;
                pendingUdpMessage = null;
            }
        }

        if (next != null)
        {
            ApplyHapticMessage(next);
        }
    }

    void StartUdpDebugInputIfEnabled()
    {
        if (!enableUdpDebugInput)
            return;

        try
        {
            udpDebugClient = new UdpClient(udpDebugPort);
            udpDebugRunning = true;
            udpDebugThread = new Thread(UdpDebugListenLoop)
            {
                IsBackground = true,
                Name = "HapticsUdpDebugListener"
            };
            udpDebugThread.Start();

            if (showDebugLogs)
            {
                Debug.Log($"[WebRTCHapticReceiver] UDP debug listener started on 127.0.0.1:{udpDebugPort}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[WebRTCHapticReceiver] Failed to start UDP debug listener on port {udpDebugPort}: {e.Message}");
        }
    }

    void UdpDebugListenLoop()
    {
        IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
        while (udpDebugRunning)
        {
            try
            {
                byte[] data = udpDebugClient.Receive(ref remoteEndpoint);
                if (data == null || data.Length == 0)
                {
                    continue;
                }

                string json = Encoding.UTF8.GetString(data);
                HapticMessage message = JsonUtility.FromJson<HapticMessage>(json);
                if (message != null && message.type == HapticsMessageType)
                {
                    lock (udpMessageLock)
                    {
                        // Keep only the latest packet to avoid stale replay if editor stalls.
                        pendingUdpMessage = message;
                    }
                }
            }
            catch (SocketException)
            {
                if (!udpDebugRunning)
                {
                    break;
                }
            }
            catch (System.ObjectDisposedException)
            {
                break;
            }
            catch (System.Exception e)
            {
                if (showDebugLogs)
                {
                    Debug.LogWarning($"[WebRTCHapticReceiver] UDP debug receive error: {e.Message}");
                }
            }
        }
    }

    void StopUdpDebugInput()
    {
        udpDebugRunning = false;

        if (udpDebugClient != null)
        {
            udpDebugClient.Close();
            udpDebugClient = null;
        }

        if (udpDebugThread != null && udpDebugThread.IsAlive)
        {
            udpDebugThread.Join(200);
        }
        udpDebugThread = null;
    }

    void ApplyHapticMessage(HapticMessage hapticMsg)
    {
        if (hapticMsg == null || hapticMsg.type != HapticsMessageType)
            return;

        // Update current haptic values and message receive times
        if (hapticMsg.left != null)
        {
            currentLeftHaptics = hapticMsg.left;
            lastLeftHandMessageTime = Time.time;
        }
        if (hapticMsg.right != null)
        {
            currentRightHaptics = hapticMsg.right;
            lastRightHandMessageTime = Time.time;
        }

        // Send haptics immediately (if continuous mode is disabled)
        if (!enableContinuousHaptics)
        {
            SendHapticsForHand(true, currentLeftHaptics);
            SendHapticsForHand(false, currentRightHaptics);
        }
    }

    /// <summary>
    /// Continuously sends haptic updates based on current values.
    /// Respects timeout settings - stops sending if no messages received within timeout period.
    /// </summary>
    IEnumerator ContinuousHapticUpdate()
    {
        while (true)
        {
            if (bHapticsGlove != null)
            {
                float currentTime = Time.time;

                // Check timeout for left hand
                bool leftHandActive = true;
                if (enableTimeout)
                {
                    if (lastLeftHandMessageTime > 0f)
                    {
                        float timeSinceLastMessage = currentTime - lastLeftHandMessageTime;
                        if (timeSinceLastMessage > messageTimeoutSeconds)
                        {
                            leftHandActive = false;
                            // Clear haptic values if timeout exceeded
                            if (currentLeftHaptics != null)
                            {
                                currentLeftHaptics.thumb = 0f;
                                currentLeftHaptics.index = 0f;
                                currentLeftHaptics.middle = 0f;
                                currentLeftHaptics.ring = 0f;
                                currentLeftHaptics.little = 0f;
                                currentLeftHaptics.palm = 0f;
                            }
                            if (showDebugLogs)
                            {
                                Debug.Log($"[WebRTCHapticReceiver] Left hand timeout exceeded ({timeSinceLastMessage:F2}s > {messageTimeoutSeconds}s). Stopping haptics.");
                            }
                            ResetHandDynamics(leftDynamicsState);
                        }
                    }
                    else
                    {
                        // No message received yet
                        leftHandActive = false;
                    }
                }

                // Check timeout for right hand
                bool rightHandActive = true;
                if (enableTimeout)
                {
                    if (lastRightHandMessageTime > 0f)
                    {
                        float timeSinceLastMessage = currentTime - lastRightHandMessageTime;
                        if (timeSinceLastMessage > messageTimeoutSeconds)
                        {
                            rightHandActive = false;
                            // Clear haptic values if timeout exceeded
                            if (currentRightHaptics != null)
                            {
                                currentRightHaptics.thumb = 0f;
                                currentRightHaptics.index = 0f;
                                currentRightHaptics.middle = 0f;
                                currentRightHaptics.ring = 0f;
                                currentRightHaptics.little = 0f;
                                currentRightHaptics.palm = 0f;
                            }
                            if (showDebugLogs)
                            {
                                Debug.Log($"[WebRTCHapticReceiver] Right hand timeout exceeded ({timeSinceLastMessage:F2}s > {messageTimeoutSeconds}s). Stopping haptics.");
                            }
                            ResetHandDynamics(rightDynamicsState);
                        }
                    }
                    else
                    {
                        // No message received yet
                        rightHandActive = false;
                    }
                }

                // Only send haptics if hand is active (not timed out)
                if (leftHandActive)
                {
                    SendHapticsForHand(true, currentLeftHaptics);
                }
                if (rightHandActive)
                {
                    SendHapticsForHand(false, currentRightHaptics);
                }
            }
            yield return null; // Update every frame
        }
    }

    /// <summary>
    /// Classifies a normalized sensor reading into a discrete ForceLevel using the
    /// inspector-configured cutoffs. Cutoffs are sorted internally so the user can
    /// drag them in the inspector without worrying about order. Levels are used only
    /// for transition kick detection; the underlying continuous signal is independent.
    /// </summary>
    ForceLevel ClassifyLevel(float normalizedForce)
    {
        float f = Mathf.Clamp01(normalizedForce);

        // Sort the four boundary cutoffs ascending so out-of-order inspector values
        // still produce a monotonic step function.
        float c1 = lightLevelCutoff;
        float c2 = mediumLevelCutoff;
        float c3 = highLevelCutoff;
        float c4 = maximumLevelCutoff;
        if (c1 > c2) { float t = c1; c1 = c2; c2 = t; }
        if (c3 > c4) { float t = c3; c3 = c4; c4 = t; }
        if (c1 > c3) { float t = c1; c1 = c3; c3 = t; }
        if (c2 > c4) { float t = c2; c2 = c4; c4 = t; }
        if (c2 > c3) { float t = c2; c2 = c3; c3 = t; }

        if (f < sensorFloorNormalized || f < c1) return ForceLevel.None;
        if (f < c2) return ForceLevel.Light;
        if (f < c3) return ForceLevel.Medium;
        if (f < c4) return ForceLevel.High;
        return ForceLevel.Maximum;
    }

    /// <summary>
    /// Returns the per-finger calibrated lower amplitude threshold.
    /// Finger index order: 0=thumb, 1=index, 2=middle, 3=ring, 4=little, 5=palm.
    /// </summary>
    int GetLowerThresholdForFinger(int fingerIndex)
    {
        switch (fingerIndex)
        {
            case 0: return thumbLowerThreshold;
            case 1: return indexLowerThreshold;
            case 2: return middleLowerThreshold;
            case 3: return ringLowerThreshold;
            case 4: return littleLowerThreshold;
            case 5: return palmLowerThreshold;
            default: return 0;
        }
    }

    /// <summary>
    /// Returns the configured kick amplitude for crossing INTO the given level.
    /// </summary>
    int KickIntensityForLevel(ForceLevel level)
    {
        switch (level)
        {
            case ForceLevel.Light:   return Mathf.Clamp(lightKickIntensity, 0, 100);
            case ForceLevel.Medium:  return Mathf.Clamp(mediumKickIntensity, 0, 100);
            case ForceLevel.High:    return Mathf.Clamp(highKickIntensity, 0, 100);
            case ForceLevel.Maximum: return Mathf.Clamp(maximumKickIntensity, 0, 100);
            default: return 0;
        }
    }

    /// <summary>
    /// Returns the configured pulse count for crossing INTO the given level.
    /// </summary>
    int PulseCountForLevel(ForceLevel level)
    {
        switch (level)
        {
            case ForceLevel.Light:   return Mathf.Max(1, lightPulseCount);
            case ForceLevel.Medium:  return Mathf.Max(1, mediumPulseCount);
            case ForceLevel.High:    return Mathf.Max(1, highPulseCount);
            case ForceLevel.Maximum: return Mathf.Max(1, maximumPulseCount);
            default: return 0;
        }
    }

    /// <summary>
    /// Maps a normalized sensor reading [0,1] to a continuous amplitude command [0,100]
    /// for one finger using the Cellini et al. T2H logarithmic form. Returns 0 below the
    /// sensor floor and saturates at maxAmplitude at and above sensorSaturationNormalized.
    /// </summary>
    int MapForceToAmplitude(float normalizedForce, int fingerIndex)
    {
        float clamped = Mathf.Clamp01(normalizedForce);

        if (clamped < sensorFloorNormalized)
            return 0;

        // Renormalize within the active range [floor, saturation] -> [0, 1].
        float upperBound = Mathf.Max(sensorSaturationNormalized, sensorFloorNormalized + 0.001f);
        float p = Mathf.InverseLerp(sensorFloorNormalized, upperBound, clamped);
        p = Mathf.Clamp01(p);

        // Logarithmic mapping (Cellini et al. T2H form).
        float a = Mathf.Max(0.0001f, alpha);
        float logShaped = Mathf.Log(1f + a * p) / Mathf.Log(1f + a);

        if (handSensorType == HandSensorType.Inspire)
        {
            float inspireExponent = Mathf.Lerp(1.2f, 0.45f, Mathf.Clamp01(inspireIntensityPower));
            logShaped = Mathf.Pow(logShaped, inspireExponent);
        }

        int lower = Mathf.Clamp(GetLowerThresholdForFinger(fingerIndex), 0, 100);
        int lowEdge = Mathf.Clamp(lower + thresholdMargin, 0, 100);
        int highEdge = Mathf.Clamp(maxAmplitude, lowEdge, 100);

        return Mathf.RoundToInt(Mathf.Lerp(lowEdge, highEdge, logShaped));
    }

    /// <summary>
    /// Sends haptic feedback for a specific hand. Each finger is an independent state
    /// machine: it can be in CONTINUOUS mode (continuous log signal if enabled, else
    /// silent) or in BURST mode (playing out N pulses at the kick amplitude separated
    /// by silent gaps). A level-boundary crossing transitions the finger from CONTINUOUS
    /// into a fresh BURST whose pulse count and amplitude come from the target level.
    /// </summary>
    void SendHapticsForHand(bool isLeft, HapticData hapticData)
    {
        if (hapticData == null) return;

        // Audio haptics: route fingertip forces to piano-note feedback. Independent of the
        // vibrotactile path below — the study manager toggles the two separately, so this no
        // longer short-circuits the gloves.
        if (useAudioHaptics && audioHapticsPlayer != null)
        {
            audioHapticsPlayer.SendHapticsForHand(isLeft, new float[]
            {
                hapticData.thumb,
                hapticData.index,
                hapticData.middle,
                hapticData.ring,
                hapticData.little,
                hapticData.palm
            });
        }

        // Vibrotactile haptics: drive the bHaptics gloves. Gated independently of audio.
        if (!vibrotactileEnabled || bHapticsGlove == null) return;

        HandDynamicsState dynamics = isLeft ? leftDynamicsState : rightDynamicsState;
        int position = isLeft ? 8 : 9; // Position: 8 = GloveL, 9 = GloveR
        float now = Time.unscaledTime;

        float[] fingerValues = new float[]
        {
            hapticData.thumb,
            hapticData.index,
            hapticData.middle,
            hapticData.ring,
            hapticData.little,
            hapticData.palm
        };

        int[] outputMotors = new int[FingerCount];
        ForceLevel[] currentLevels = new ForceLevel[FingerCount];
        bool hasAnyContact = false;

        float pulseSec = Mathf.Max(0.001f, transitionPulseDurationMs / 1000f);
        float gapSec = Mathf.Max(0.001f, interPulseGapMs / 1000f);

        // Per-finger update: classify, detect transitions, advance burst state machine,
        // and decide whether this frame's output is a burst pulse or continuous.
        for (int i = 0; i < FingerCount; i++)
        {
            float force = Mathf.Clamp01(fingerValues[i]);
            ForceLevel level = ClassifyLevel(force);
            currentLevels[i] = level;
            int continuousAmplitude = MapForceToAmplitude(force, i);

            // 1. Detect a level transition and arm a new burst if appropriate.
            if (enableTransitionPulse && level != dynamics.fingerLastLevel[i] && level != ForceLevel.None)
            {
                bool isUpward = (int)level > (int)dynamics.fingerLastLevel[i];
                if (isUpward || kickOnDownwardTransitions)
                {
                    // Arm a new burst. This overrides any in-progress burst on this finger.
                    dynamics.pulsesRemaining[i] = PulseCountForLevel(level);
                    dynamics.burstKickAmplitude[i] = KickIntensityForLevel(level);
                    dynamics.inOnPhase[i] = true;
                    dynamics.phaseEndTime[i] = now + pulseSec;
                }
            }
            // If finger dropped to None, cancel any in-progress burst.
            if (level == ForceLevel.None)
            {
                dynamics.pulsesRemaining[i] = 0;
                dynamics.inOnPhase[i] = false;
            }
            dynamics.fingerLastLevel[i] = level;

            // 2. Advance the burst state machine if a burst is active.
            // The "burst is active" condition is pulsesRemaining > 0 (more pulses to play)
            // OR (pulsesRemaining == 0 AND inOnPhase is true) for the very last pulse's ON phase.
            // We model this by treating pulsesRemaining as the number of pulses NOT YET STARTED,
            // and decrement at the moment we transition from ON to OFF (or finish the last ON).
            bool burstActiveThisFrame = false;
            int burstAmplitudeThisFrame = 0;

            if (dynamics.pulsesRemaining[i] > 0 || dynamics.inOnPhase[i])
            {
                // Advance phases until "now" lands inside one.
                while ((dynamics.pulsesRemaining[i] > 0 || dynamics.inOnPhase[i]) && now >= dynamics.phaseEndTime[i])
                {
                    if (dynamics.inOnPhase[i])
                    {
                        // ON phase finished -> consume one pulse, start gap if more pulses remain.
                        dynamics.pulsesRemaining[i] = Mathf.Max(0, dynamics.pulsesRemaining[i] - 1);
                        if (dynamics.pulsesRemaining[i] > 0)
                        {
                            dynamics.inOnPhase[i] = false;
                            dynamics.phaseEndTime[i] = now + gapSec;
                        }
                        else
                        {
                            // Last pulse finished, burst is done.
                            dynamics.inOnPhase[i] = false;
                            break;
                        }
                    }
                    else
                    {
                        // Gap finished -> start next ON pulse.
                        dynamics.inOnPhase[i] = true;
                        dynamics.phaseEndTime[i] = now + pulseSec;
                    }
                }

                // Now read state at "now".
                if (dynamics.pulsesRemaining[i] > 0 || dynamics.inOnPhase[i])
                {
                    burstActiveThisFrame = true;
                    burstAmplitudeThisFrame = dynamics.inOnPhase[i] ? dynamics.burstKickAmplitude[i] : 0;
                }
            }

            // 3. Decide output for this finger.
            int output;
            if (burstActiveThisFrame)
            {
                if (dynamics.inOnPhase[i])
                {
                    // During an ON pulse, output is the kick amplitude (clamped not to dip below
                    // any concurrent continuous signal so it never sounds like a regression).
                    int floor = enableContinuousLogMapping ? continuousAmplitude : 0;
                    output = Mathf.Max(burstAmplitudeThisFrame, floor);
                }
                else
                {
                    // During the silent gap between pulses, we stay silent so the gap is unambiguous.
                    // This is the whole point of distinguishable pulse counts.
                    output = 0;
                }
            }
            else
            {
                // No active burst. Continuous signal if enabled, else silent.
                output = enableContinuousLogMapping ? continuousAmplitude : 0;
            }

            outputMotors[i] = Mathf.Clamp(output, 0, 100);

            if (level != ForceLevel.None || output > 0)
                hasAnyContact = true;
        }

        // If nothing is in contact and no burst is active anywhere, clear motors once and bail.
        if (!hasAnyContact)
        {
            if (dynamics.wasActiveLastFrame)
            {
                try
                {
                    BhapticsLibrary.PlayMotors(position, new int[FingerCount], continuousCommandDurationMs);
                }
                catch { }
                dynamics.wasActiveLastFrame = false;
            }
            return;
        }

        try
        {
            BhapticsLibrary.PlayMotors(position, outputMotors, continuousCommandDurationMs);
            dynamics.wasActiveLastFrame = true;

            if (showDebugLogs)
            {
                string handName = isLeft ? "Left" : "Right";
                string levels = "";
                string phases = "";
                for (int i = 0; i < FingerCount; i++)
                {
                    levels += ((int)currentLevels[i]).ToString();
                    bool inBurst = dynamics.pulsesRemaining[i] > 0 || dynamics.inOnPhase[i];
                    phases += inBurst ? (dynamics.inOnPhase[i] ? "P" : "g") : ".";
                }
                Debug.Log($"[WebRTCHapticReceiver] [{handSensorType}] {handName} L[{levels}] burst[{phases}] " +
                          $"T:{outputMotors[0]} I:{outputMotors[1]} M:{outputMotors[2]} R:{outputMotors[3]} L:{outputMotors[4]} P:{outputMotors[5]}");
            }
        }
        catch (System.Exception e)
        {
            if (showDebugLogs)
            {
                Debug.LogError($"[WebRTCHapticReceiver] Error sending haptic to {(isLeft ? "left" : "right")} hand: {e.Message}");
            }
        }
    }

    void ResetHandDynamics(HandDynamicsState state)
    {
        for (int i = 0; i < FingerCount; i++)
        {
            state.fingerLastLevel[i] = ForceLevel.None;
            state.pulsesRemaining[i] = 0;
            state.burstKickAmplitude[i] = 0;
            state.phaseEndTime[i] = 0f;
            state.inOnPhase[i] = false;
        }
        state.wasActiveLastFrame = false;
    }

    // ── Remote command API (driven by UnityCommandReceiver / study manager) ────

    /// <summary>
    /// Enable/disable vibrotactile (bHaptics glove) output at runtime. When disabling,
    /// immediately zeroes both gloves and resets burst state so no residual vibration lingers.
    /// </summary>
    public void SetVibrotactileEnabled(bool enabled)
    {
        vibrotactileEnabled = enabled;
        if (!enabled)
        {
            ClearAllMotors();
        }
        if (showDebugLogs)
        {
            Debug.Log($"[WebRTCHapticReceiver] Vibrotactile haptics {(enabled ? "enabled" : "disabled")}");
        }
    }

    /// <summary>
    /// Enable/disable audio (piano-note) haptics at runtime.
    /// </summary>
    public void SetAudioHapticsEnabled(bool enabled)
    {
        useAudioHaptics = enabled;
        if (showDebugLogs)
        {
            Debug.Log($"[WebRTCHapticReceiver] Audio haptics {(enabled ? "enabled" : "disabled")}");
        }
    }

    /// <summary>
    /// Immediately zero both bHaptics gloves and reset per-hand burst state. Used when
    /// vibrotactile output is switched off so the motors stop on the same frame.
    /// </summary>
    void ClearAllMotors()
    {
        try
        {
            BhapticsLibrary.PlayMotors(8, new int[FingerCount], continuousCommandDurationMs); // GloveL
            BhapticsLibrary.PlayMotors(9, new int[FingerCount], continuousCommandDurationMs); // GloveR
        }
        catch { }
        ResetHandDynamics(leftDynamicsState);
        ResetHandDynamics(rightDynamicsState);
    }

    void OnDestroy()
    {
        StopUdpDebugInput();

        if (continuousHapticCoroutine != null)
        {
            StopCoroutine(continuousHapticCoroutine);
        }
    }
}