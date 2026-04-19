using UnityEngine;
using Unity.WebRTC;
using System.Collections;
using Bhaptics.SDK2.Glove;
using Bhaptics.SDK2;

/// <summary>
/// Receives haptic messages from WebRTC and sends them to bHaptics gloves.
///
/// Supported hand sensor types:
///   Psyonic  – original format; values already 0-1, linear intensity mapping.
///   Inspire  – Inspire RH56DFTP touch sensor averages normalized to 0-1 on the
///              Python side (HAPTICS_SENSOR_MAX = 1024). A power curve
///              (inspireIntensityPower < 1) lifts faint contacts so light touches
///              are perceptible through the glove, and separate pulse duration
///              limits give a crisper feel suited to the faster Inspire sensor.
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

    [Header("bHaptics Integration")]
    [Tooltip("The bHaptics Physics Glove component. Leave null to use singleton instance.")]
    public BhapticsPhysicsGlove bHapticsGlove;

    [Header("Psyonic Haptic Mapping")]
    [Tooltip("Min pulse duration ms (Psyonic, high frequency)")]
    [Range(10, 100)]
    public int minPulseDurationMs = 20;

    [Tooltip("Max pulse duration ms (Psyonic, low frequency)")]
    [Range(50, 500)]
    public int maxPulseDurationMs = 200;

    [Tooltip("Minimum intensity threshold (0-1) for Psyonic. Values below this won't trigger haptics.")]
    [Range(0f, 0.1f)]
    public float minIntensityThreshold = 0.01f;

    [Header("Inspire Haptic Mapping")]
    [Tooltip("Power-curve exponent applied to Inspire 0-1 values before mapping to intensity.\n" +
             "Values < 1 boost faint contacts (e.g. 0.5 = sqrt). 1 = linear (same as Psyonic).")]
    [Range(0.1f, 1f)]
    public float inspireIntensityPower = 0.5f;

    [Tooltip("Min pulse duration ms (Inspire, high frequency)")]
    [Range(10, 100)]
    public int inspireMinPulseDurationMs = 15;

    [Tooltip("Max pulse duration ms (Inspire, low frequency)")]
    [Range(50, 500)]
    public int inspireMaxPulseDurationMs = 150;

    [Tooltip("Minimum intensity threshold (0-1) for Inspire touch sensors.")]
    [Range(0f, 0.1f)]
    public float inspireMinIntensityThreshold = 0.02f;

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

    // Finger index mapping: thumb=0, index=1, middle=2, ring=3, little=4, palm=5 (wrist)
    private readonly int[] fingerIndices = new int[] { 0, 1, 2, 3, 4, 5 };

    // WebRTC data channel reference
    private RTCDataChannel hapticsChannel;

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
    }

    void InitializeComponents()
    {
        // Auto-detect WebRTCController if not assigned
        if (webRTCController == null)
        {
            webRTCController = FindObjectOfType<WebRTCController>();
            if (webRTCController == null)
            {
                Debug.LogError("[WebRTCHapticReceiver] WebRTCController not found! Please assign it in the Inspector.");
                return;
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

                if (hapticMsg != null && hapticMsg.type == "haptics")
                {
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
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[WebRTCHapticReceiver] Error parsing haptic message: {e.Message}");
            }
        };
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
    /// Sends haptic feedback for a specific hand.
    /// Branches on handSensorType to apply the appropriate intensity curve and pulse parameters.
    /// </summary>
    void SendHapticsForHand(bool isLeft, HapticData hapticData)
    {
        if (bHapticsGlove == null || hapticData == null)
            return;

        bool isInspire = handSensorType == HandSensorType.Inspire;
        float threshold  = isInspire ? inspireMinIntensityThreshold : minIntensityThreshold;
        int   minPulse   = isInspire ? inspireMinPulseDurationMs    : minPulseDurationMs;
        int   maxPulse   = isInspire ? inspireMaxPulseDurationMs    : maxPulseDurationMs;

        // Create motor array (6 motors: thumb, index, middle, ring, little, wrist/palm)
        int[] motors = new int[6];

        float[] fingerValues = new float[]
        {
            hapticData.thumb,
            hapticData.index,
            hapticData.middle,
            hapticData.ring,
            hapticData.little,
            hapticData.palm
        };

        float maxMapped = 0f;
        bool hasActiveHaptics = false;

        for (int i = 0; i < fingerValues.Length && i < motors.Length; i++)
        {
            float value = Mathf.Clamp01(fingerValues[i]);

            if (value < threshold)
            {
                motors[i] = 0;
                continue;
            }

            hasActiveHaptics = true;

            // Apply power curve for Inspire to boost faint contacts.
            float mapped = isInspire ? Mathf.Pow(value, inspireIntensityPower) : value;
            motors[i] = Mathf.RoundToInt(mapped * 100f);
            maxMapped = Mathf.Max(maxMapped, mapped);
        }

        if (!hasActiveHaptics)
            return;

        // Higher mapped value → higher frequency → shorter pulse duration.
        float normalizedFrequency = Mathf.Clamp01(maxMapped);
        int durationMs = Mathf.RoundToInt(
            maxPulse - (normalizedFrequency * (maxPulse - minPulse))
        );

        // Position: 8 = GloveL, 9 = GloveR
        int position = isLeft ? 8 : 9;

        try
        {
            BhapticsLibrary.PlayMotors(position, motors, durationMs);

            if (showDebugLogs)
            {
                string handName = isLeft ? "Left" : "Right";
                Debug.Log($"[WebRTCHapticReceiver] [{handSensorType}] {handName} – Thumb:{motors[0]} Index:{motors[1]} Middle:{motors[2]} Ring:{motors[3]} Little:{motors[4]} Palm:{motors[5]} ({durationMs}ms)");
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

    void OnDestroy()
    {
        if (continuousHapticCoroutine != null)
        {
            StopCoroutine(continuousHapticCoroutine);
        }
    }
}

