using UnityEngine;
using UnityEngine.UI;
using Bhaptics.SDK2.Glove;
using System.Reflection;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using TMPro;

/// <summary>
/// Adds colliders to fingertips from HandVisual (Meta XR SDK) and triggers bHaptics haptic feedback
/// when fingertips collide with virtual UI elements (including Meta Quest keyboard).
/// </summary>
public class BHapticsFingertipHaptics : MonoBehaviour
{
    [Header("Hand Visual Reference")]
    [Tooltip("The HandVisual component (from OVRLeftHandVisual/OVRRightHandVisual). Auto-detected if not assigned.")]
    public MonoBehaviour handVisual;
    
    [Tooltip("Manual fingertip transform assignments (if auto-detection fails)")]
    public Transform thumbTip;
    public Transform indexTip;
    public Transform middleTip;
    public Transform ringTip;
    public Transform pinkyTip;

    [Header("bHaptics Integration")]
    [Tooltip("The bHaptics Physics Glove component. Leave null to use singleton instance.")]
    public BhapticsPhysicsGlove bHapticsGlove;

    [Tooltip("Is this the left hand? (true for left, false for right)")]
    public bool isLeftHand = false;

    [Header("Meta XR SDK Integration")]
    [Tooltip("PokeInteractor for this hand (auto-detected if not assigned). Used to detect actual button presses.")]
    public PokeInteractor pokeInteractor;
    
    [Tooltip("RayInteractor for this hand (auto-detected if not assigned). Used to detect pinch interactions with distant UI.")]
    public RayInteractor rayInteractor;

    [Header("Fingertip Collider Settings")]
    [Tooltip("Radius of the sphere collider for each fingertip (in meters)")]
    [Range(0.005f, 0.02f)]
    public float colliderRadius = 0.005f; // Half of previous default (0.01f)

    [Tooltip("Should colliders be triggers? (Recommended: true for UI interaction)")]
    public bool isTrigger = true;

    [Header("Contact Haptics (Minor Pulse) - Any Finger Touching UI")]
    [Tooltip("Base intensity of minor haptic pulses sent to fingers while touching UI canvas/surface (0-100). This is the minimum intensity when not moving.")]
    [Range(0f, 100f)]
    public float contactHapticIntensity = 15f;

    [Tooltip("Base rate/interval: Time between minor haptic pulses while a finger is touching UI (seconds). Lower = faster pulses. This is the slowest rate when not moving.")]
    [Range(0.05f, 1f)]
    public float contactHapticInterval = 0.15f;

    [Header("Velocity-Based Haptic Scaling")]
    [Tooltip("Enable velocity-based haptic feedback. Intensity and pulse rate will increase with finger movement speed.")]
    public bool enableVelocityBasedHaptics = true;

    [Tooltip("Minimum finger speed (m/s) to start scaling haptics. Below this speed, uses base intensity/rate.")]
    [Range(0f, 1f)]
    public float minSpeedForScaling = 0.01f;

    [Tooltip("Maximum finger speed (m/s) for haptic scaling. At this speed or above, uses max intensity/rate.")]
    [Range(0.1f, 4f)]
    public float maxSpeedForScaling = 0.5f;

    [Tooltip("Maximum intensity multiplier when moving at max speed. Intensity = baseIntensity * (1 + speedMultiplier * normalizedSpeed).")]
    [Range(0f, 10f)]
    public float maxIntensityMultiplier = 7f;

    [Tooltip("Maximum pulse rate multiplier when moving at max speed. Interval = baseInterval / (1 + rateMultiplier * normalizedSpeed).")]
    [Range(0f, 10f)]
    public float maxRateMultiplier = 3f;

    [Tooltip("Smoothing factor for velocity calculation (0-1). Higher = smoother but more delayed response.")]
    [Range(0f, 1f)]
    public float velocitySmoothing = 0.5f;

    [Tooltip("Minimum finger speed (m/s) required to trigger continuous haptics. Below this speed, no continuous haptics are sent (only initial touch haptic).")]
    [Range(0f, 1f)]
    public float minVelocityForContinuousHaptics = 0.1f;

    [Header("Button Press Haptics (Strong Pulse) - Finger Pressing Button")]
    [Tooltip("Intensity of strong haptic pulse sent to the finger that actually presses a button (0-100).")]
    [Range(0f, 100f)]
    public float buttonPressHapticIntensity = 80f;

    [Header("Pinch Interaction Haptics - Ray Interactor")]
    [Tooltip("Intensity of haptic pulse sent to thumb and index finger during pinch interactions with distant UI (0-100).")]
    [Range(0f, 100f)]
    public float pinchHapticIntensity = 50f;
    
    [Tooltip("Number of haptic pulses to send for pinch interactions (to make them more noticeable).")]
    [Range(1, 5)]
    public int pinchPulseCount = 2;
    
    [Tooltip("Time between multiple pinch pulses (seconds). Lower = faster pulses.")]
    [Range(0.01f, 0.1f)]
    public float pinchPulseInterval = 0.05f;

    [Header("Meta Virtual Keyboard Haptics")]
    [Tooltip("Enable haptic feedback when typing on Meta's virtual keyboard (appears when InputField is focused).")]
    public bool enableKeyboardHaptics = true;
    
    [Tooltip("Intensity of haptic pulse sent to index finger when a keyboard key is pressed (0-100).")]
    [Range(0f, 100f)]
    public float keyboardKeyHapticIntensity = 40f;
    
    [Tooltip("Which finger to trigger haptics for keyboard input (0=Thumb, 1=Index, 2=Middle, 3=Ring, 4=Pinky). Typically Index finger (1) is used for typing.")]
    [Range(0, 4)]
    public int keyboardHapticFinger = 1; // Index finger by default

    [Header("Advanced Haptic Settings")]
    [Tooltip("Time to pause contact haptics after a button press (seconds). Prevents contact haptics from overriding button press haptics.")]
    [Range(0.1f, 2f)]
    public float buttonPressContactCooldown = 0.5f;

    [Tooltip("Number of haptic pulses to send for button presses (to make them stronger and more noticeable).")]
    [Range(1, 5)]
    public int buttonPressPulseCount = 3;

    [Tooltip("Time between multiple button press pulses (seconds). Lower = faster pulses.")]
    [Range(0.01f, 0.1f)]
    public float buttonPressPulseInterval = 0.03f;

    [Header("Collision Filtering")]
    [Tooltip("Only trigger haptics when colliding with UI elements. Enable this to prevent haptics from firing when moving.")]
    public bool onlyTriggerOnUI = true;

    [Tooltip("Layer mask for objects that should trigger haptics (only used if Only Trigger On UI is enabled). Default: UI layer (5)")]
    public LayerMask allowedLayers = 1 << 5; // Layer 5 = UI by default

    [Tooltip("Check for UI components (Canvas, GraphicRaycaster, etc.) in addition to layer check")]
    public bool checkForUIComponents = true;

    [Tooltip("Exclude collisions with hand skeleton/player model (objects containing 'Hand' or 'StylizedCharacter' in name)")]
    public bool excludeHandSkeleton = true;

    [Header("Debug")]
    [Tooltip("Show debug logs when collisions occur")]
    public bool showDebugLogs = false;

    [Tooltip("Track which functions are called (for code cleanup - remove unused functions)")]
    public bool trackFunctionCalls = true;

    // Fingertip references and colliders
    private Transform[] fingertipTransforms = new Transform[5]; // Thumb, Index, Middle, Ring, Pinky
    private SphereCollider[] fingertipColliders = new SphereCollider[5];
    private float[] lastContactHapticTime = new float[5]; // For contact haptics
    private float[] lastButtonPressTime = new float[5]; // Track when button press happened on each finger
    private bool[] hasSentInitialTouchHaptic = new bool[5]; // Track if initial touch haptic has been sent for each finger

    // Track currently hovering/selecting interactables for button press detection
    private HashSet<IInteractable> hoveringInteractables = new HashSet<IInteractable>();
    private Dictionary<IInteractable, int> interactableToFingerIndex = new Dictionary<IInteractable, int>();

    // Track which fingers are currently touching UI elements
    private HashSet<int> fingersTouchingUI = new HashSet<int>();
    private Dictionary<int, HashSet<Collider>> fingerToUIColliders = new Dictionary<int, HashSet<Collider>>(); // Track which colliders each finger is touching

    // Velocity tracking for velocity-based haptics
    private Vector3[] previousFingerPositions = new Vector3[5];
    private float[] previousFingerTimes = new float[5];
    private float[] smoothedFingerVelocities = new float[5]; // Smoothed velocity magnitude for each finger

    // Meta Virtual Keyboard tracking
    private List<InputField> monitoredInputFields = new List<InputField>();
    private List<TMP_InputField> monitoredTMPInputFields = new List<TMP_InputField>();
    private Dictionary<InputField, string> inputFieldPreviousText = new Dictionary<InputField, string>();
    private Dictionary<TMP_InputField, string> tmpInputFieldPreviousText = new Dictionary<TMP_InputField, string>();
    private float lastKeyboardHapticTime = 0f;
    private const float keyboardHapticCooldown = 0.05f; // Minimum time between keyboard haptics (20 Hz max)
    
    // Track which hand is active for keyboard interactions
    private static float lastLeftHandInteractionTime = 0f;
    private static float lastRightHandInteractionTime = 0f;
    private const float keyboardHandTimeout = 2f; // Time in seconds before hand tracking expires (assumes other hand is active)

    // Joint indices for fingertips in HandVisual's OpenXR joint transforms array
    // For OpenXR (used by Meta XR SDK Building Blocks), fingertips are at:
    // HandThumbTip = 5, HandIndexTip = 10, HandMiddleTip = 15, HandRingTip = 20, HandLittleTip = 25
    private readonly int[] fingertipJointIndicesOpenXR = new int[]
    {
        5,  // HandThumbTip
        10, // HandIndexTip
        15, // HandMiddleTip
        20, // HandRingTip
        25  // HandLittleTip (Pinky)
    };

    private readonly string[] fingerNames = new string[]
    {
        "Thumb", "Index", "Middle", "Ring", "Pinky"
    };

    void Start()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] Start() called");
        InitializeComponents();
        FindPokeInteractor();
        FindRayInteractor();
        WaitForSkeletonInitialization();
        StartContactHapticCoroutine();
        
        // Start monitoring InputFields for Meta virtual keyboard interactions
        if (enableKeyboardHaptics)
        {
            StartMonitoringInputFields();
        }
    }

    void Update()
    {
        // if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] Update() called");
        // Update velocity tracking for all fingers currently touching UI
        if (enableVelocityBasedHaptics)
        {
            UpdateFingerVelocities();
        }
        
        // Monitor InputFields for Meta virtual keyboard text changes
        if (enableKeyboardHaptics)
        {
            CheckInputFieldTextChanges();
        }
    }

    /// <summary>
    /// Finds and starts monitoring all InputFields in the scene for Meta virtual keyboard interactions.
    /// </summary>
    void StartMonitoringInputFields()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] StartMonitoringInputFields() called");
        
        // Find all InputFields (standard Unity UI)
        InputField[] allInputFields = FindObjectsOfType<InputField>();
        foreach (var inputField in allInputFields)
        {
            if (inputField != null && !monitoredInputFields.Contains(inputField))
            {
                monitoredInputFields.Add(inputField);
                inputFieldPreviousText[inputField] = inputField.text ?? "";
                
                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] Started monitoring InputField: {inputField.name}");
                }
            }
        }
        
        // Find all TMP_InputFields (TextMeshPro)
        TMP_InputField[] allTMPInputFields = FindObjectsOfType<TMP_InputField>();
        foreach (var tmpInputField in allTMPInputFields)
        {
            if (tmpInputField != null && !monitoredTMPInputFields.Contains(tmpInputField))
            {
                monitoredTMPInputFields.Add(tmpInputField);
                tmpInputFieldPreviousText[tmpInputField] = tmpInputField.text ?? "";
                
                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] Started monitoring TMP_InputField: {tmpInputField.name}");
                }
            }
        }
        
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] Monitoring {monitoredInputFields.Count} InputField(s) and {monitoredTMPInputFields.Count} TMP_InputField(s) for Meta virtual keyboard interactions");
        }
    }

    /// <summary>
    /// Checks all monitored InputFields for text changes (indicating keyboard key presses).
    /// Triggers haptic feedback when text changes while InputField is focused.
    /// </summary>
    void CheckInputFieldTextChanges()
    {
        // if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] CheckInputFieldTextChanges() called");
        
        // Check standard InputFields
        for (int i = monitoredInputFields.Count - 1; i >= 0; i--)
        {
            InputField inputField = monitoredInputFields[i];
            if (inputField == null)
            {
                monitoredInputFields.RemoveAt(i);
                continue;
            }
            
            // Only trigger haptics if InputField is focused (keyboard is active)
            if (inputField.isFocused)
            {
                string currentText = inputField.text ?? "";
                string previousText = inputFieldPreviousText.ContainsKey(inputField) ? inputFieldPreviousText[inputField] : "";
                
                // Check if text changed (key was pressed)
                if (currentText != previousText)
                {
                    // Text changed - trigger haptic feedback
                    SendKeyboardHaptic();
                    inputFieldPreviousText[inputField] = currentText;
                    
                    if (showDebugLogs)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Meta keyboard key pressed detected on InputField '{inputField.name}' - text changed from '{previousText}' to '{currentText}'");
                    }
                }
            }
            else
            {
                // Update previous text even when not focused (to avoid false triggers when refocusing)
                inputFieldPreviousText[inputField] = inputField.text ?? "";
            }
        }
        
        // Check TMP_InputFields
        for (int i = monitoredTMPInputFields.Count - 1; i >= 0; i--)
        {
            TMP_InputField tmpInputField = monitoredTMPInputFields[i];
            if (tmpInputField == null)
            {
                monitoredTMPInputFields.RemoveAt(i);
                continue;
            }
            
            // Only trigger haptics if TMP_InputField is focused (keyboard is active)
            if (tmpInputField.isFocused)
            {
                string currentText = tmpInputField.text ?? "";
                string previousText = tmpInputFieldPreviousText.ContainsKey(tmpInputField) ? tmpInputFieldPreviousText[tmpInputField] : "";
                
                // Check if text changed (key was pressed)
                if (currentText != previousText)
                {
                    // Text changed - trigger haptic feedback
                    SendKeyboardHaptic();
                    tmpInputFieldPreviousText[tmpInputField] = currentText;
                    
                    if (showDebugLogs)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Meta keyboard key pressed detected on TMP_InputField '{tmpInputField.name}' - text changed from '{previousText}' to '{currentText}'");
                    }
                }
            }
            else
            {
                // Update previous text even when not focused (to avoid false triggers when refocusing)
                tmpInputFieldPreviousText[tmpInputField] = tmpInputField.text ?? "";
            }
        }
    }

    /// <summary>
    /// Determines which hand is currently active for keyboard interactions.
    /// Returns true if this hand should handle the keyboard haptic.
    /// </summary>
    bool IsThisHandActiveForKeyboard()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] IsThisHandActiveForKeyboard() called");
        
        float currentTime = Time.time;
        float leftTimeSinceInteraction = currentTime - lastLeftHandInteractionTime;
        float rightTimeSinceInteraction = currentTime - lastRightHandInteractionTime;
        
        // Check if either hand has interacted recently (within timeout)
        bool leftHandActive = leftTimeSinceInteraction < keyboardHandTimeout;
        bool rightHandActive = rightTimeSinceInteraction < keyboardHandTimeout;
        
        // If only one hand is active, use that one
        if (leftHandActive && !rightHandActive)
        {
            return isLeftHand;
        }
        if (rightHandActive && !leftHandActive)
        {
            return !isLeftHand;
        }
        
        // If both hands are active (or neither), use the most recently active one
        if (leftHandActive && rightHandActive)
        {
            // Use the hand that interacted more recently
            return isLeftHand ? (lastLeftHandInteractionTime >= lastRightHandInteractionTime) : 
                                (lastRightHandInteractionTime >= lastLeftHandInteractionTime);
        }
        
        // Fallback: If neither hand has interacted recently, use hand position relative to camera
        // (hand closer to camera forward direction is likely the active one)
        return IsThisHandCloserToKeyboard();
    }
    
    /// <summary>
    /// Determines which hand is actively typing at this moment by checking hand position
    /// relative to the keyboard area. This is checked dynamically for each key press.
    /// Since Meta's keyboard is system-level, we can't detect which finger pressed which key,
    /// so we use hand position as the best indicator.
    /// </summary>
    bool IsThisHandCloserToKeyboard()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] IsThisHandCloserToKeyboard() called");
        
        // Get camera (typically the main camera or VR camera)
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            mainCamera = FindObjectOfType<Camera>();
        }
        
        if (mainCamera == null || fingertipTransforms[keyboardHapticFinger] == null)
        {
            // Can't determine, default to this hand
            return true;
        }
        
        // Get index finger position for this hand
        Vector3 thisHandPosition = fingertipTransforms[keyboardHapticFinger].position;
        
        // Find the other hand's BHapticsFingertipHaptics instance
        BHapticsFingertipHaptics[] allInstances = FindObjectsOfType<BHapticsFingertipHaptics>();
        BHapticsFingertipHaptics otherHandInstance = null;
        foreach (var instance in allInstances)
        {
            if (instance != this && instance.isLeftHand != this.isLeftHand)
            {
                otherHandInstance = instance;
                break;
            }
        }
        
        if (otherHandInstance == null || otherHandInstance.fingertipTransforms[keyboardHapticFinger] == null)
        {
            // Other hand not found, use this hand
            return true;
        }
        
        Vector3 otherHandPosition = otherHandInstance.fingertipTransforms[keyboardHapticFinger].position;
        
        // Keyboard is typically positioned in front of the camera at a comfortable typing distance
        Vector3 cameraForward = mainCamera.transform.forward;
        Vector3 cameraRight = mainCamera.transform.right;
        Vector3 cameraPosition = mainCamera.transform.position;
        
        // Estimate keyboard position (typically 0.5-0.7m in front of camera, slightly below eye level)
        Vector3 estimatedKeyboardPosition = cameraPosition + cameraForward * 0.6f + Vector3.down * 0.2f;
        
        // Calculate distance from each hand to the estimated keyboard position
        float thisHandDistance = Vector3.Distance(thisHandPosition, estimatedKeyboardPosition);
        float otherHandDistance = Vector3.Distance(otherHandPosition, estimatedKeyboardPosition);
        
        // Check forward projection (hand more forward is likely typing)
        Vector3 thisHandToCamera = thisHandPosition - cameraPosition;
        Vector3 otherHandToCamera = otherHandPosition - cameraPosition;
        float thisHandForward = Vector3.Dot(thisHandToCamera, cameraForward);
        float otherHandForward = Vector3.Dot(otherHandToCamera, cameraForward);
        
        // Primary decision: Use distance to keyboard (most reliable indicator)
        // If distances are similar (within 5cm), use forward position as tiebreaker
        float distanceDifference = Mathf.Abs(thisHandDistance - otherHandDistance);
        bool thisHandIsCloser = thisHandDistance <= otherHandDistance;
        
        // If distances are very close, use forward position
        if (distanceDifference < 0.05f)
        {
            thisHandIsCloser = thisHandForward >= otherHandForward;
        }
        
        // Additional check: If one hand is significantly more forward (by 10cm or more), prefer it
        // This handles cases where hands are at similar distances but one is clearly in typing position
        float forwardDifference = thisHandForward - otherHandForward;
        if (Mathf.Abs(forwardDifference) > 0.1f)
        {
            // One hand is significantly more forward
            if (forwardDifference > 0.1f)
            {
                thisHandIsCloser = true;
            }
            else if (forwardDifference < -0.1f)
            {
                thisHandIsCloser = false;
            }
        }
        
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] Keyboard hand detection: {(isLeftHand ? "Left" : "Right")} hand (dist={thisHandDistance:F3}m, forward={thisHandForward:F3}m) vs Other hand (dist={otherHandDistance:F3}m, forward={otherHandForward:F3}m) -> Using {(thisHandIsCloser ? (isLeftHand ? "Left" : "Right") : (isLeftHand ? "Right" : "Left"))} hand");
        }
        
        return thisHandIsCloser;
    }

    /// <summary>
    /// Sends haptic feedback for Meta virtual keyboard key presses.
    /// Since Meta's keyboard freezes the avatar and hand tracking may be paused,
    /// we use the hand that last interacted with UI (before keyboard appeared) to determine
    /// which hand should receive haptics. If both hands interacted recently, we try to use
    /// position-based detection as a fallback.
    /// </summary>
    void SendKeyboardHaptic()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] SendKeyboardHaptic() called");
        
        // Since hand tracking may be frozen when keyboard is active, use a combination of:
        // 1. Last interaction time (which hand last touched UI before keyboard appeared)
        // 2. Position-based detection (if hand tracking is still working)
        
        float currentTime = Time.time;
        float leftTimeSinceInteraction = currentTime - lastLeftHandInteractionTime;
        float rightTimeSinceInteraction = currentTime - lastRightHandInteractionTime;
        
        // Check if we can use interaction time (within reasonable window - 5 seconds)
        bool canUseInteractionTime = leftTimeSinceInteraction < 5f || rightTimeSinceInteraction < 5f;
        
        bool shouldSendHaptic = false;
        
        if (canUseInteractionTime)
        {
            // Use the hand that interacted more recently (likely the one that opened the keyboard)
            if (isLeftHand)
            {
                shouldSendHaptic = lastLeftHandInteractionTime >= lastRightHandInteractionTime;
            }
            else
            {
                shouldSendHaptic = lastRightHandInteractionTime >= lastLeftHandInteractionTime;
            }
            
            if (showDebugLogs && !shouldSendHaptic)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Skipping keyboard haptic for {(isLeftHand ? "Left" : "Right")} hand - other hand interacted more recently (left: {leftTimeSinceInteraction:F2}s ago, right: {rightTimeSinceInteraction:F2}s ago)");
            }
        }
        else
        {
            // Fallback: Try position-based detection (in case hand tracking is still working)
            shouldSendHaptic = IsThisHandCloserToKeyboard();
            
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Using position-based detection for keyboard haptic (interaction times expired) - {(isLeftHand ? "Left" : "Right")} hand: {shouldSendHaptic}");
            }
        }
        
        if (!shouldSendHaptic)
        {
            return;
        }
        
        // Cooldown to prevent too many haptics (keyboard can trigger very rapidly)
        if (Time.time - lastKeyboardHapticTime < keyboardHapticCooldown)
        {
            return;
        }
        
        if (bHapticsGlove == null)
        {
            // Try to reacquire bHapticsGlove instance if it's null
            bHapticsGlove = BhapticsPhysicsGlove.Instance;
            if (bHapticsGlove == null)
            {
                BhapticsPhysicsGlove[] allGloves = FindObjectsOfType<BhapticsPhysicsGlove>();
                if (allGloves != null && allGloves.Length > 0)
                {
                    bHapticsGlove = allGloves[0];
                }
                else
                {
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Cannot send keyboard haptic: bHapticsGlove is null for {(isLeftHand ? "Left" : "Right")} hand. No gloves found in scene.");
                    }
                    return;
                }
            }
        }
        
        // Validate finger index
        int fingerIndex = keyboardHapticFinger;
        if (fingerIndex < 0 || fingerIndex >= fingertipTransforms.Length)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Invalid keyboard haptic finger index: {fingerIndex} for {(isLeftHand ? "Left" : "Right")} hand");
            }
            return;
        }
        
        // Map finger index to bHaptics finger index (0=Thumb, 1=Index, 2=Middle, 3=Ring, 4=Pinky)
        int bHapticsFingerIndex = fingerIndex;
        
        // Send haptic pulse for keyboard key press
        try
        {
            if (bHapticsGlove == null)
            {
                if (showDebugLogs)
                {
                    Debug.LogWarning($"[BHapticsFingertipHaptics] bHapticsGlove became null right before sending keyboard haptic to {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]}");
                }
                return;
            }
            
            // Send haptic pulse
            bHapticsGlove.SendEnterHaptic(isLeftHand, bHapticsFingerIndex);
            lastKeyboardHapticTime = Time.time;
            
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} Meta keyboard haptic pulse sent (intensity: {keyboardKeyHapticIntensity:F1})");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[BHapticsFingertipHaptics] Error sending keyboard haptic to {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]}: {e.Message}. isLeftHand={isLeftHand}, fingerIndex={bHapticsFingerIndex}, bHapticsGlove={(bHapticsGlove != null ? "Found" : "NULL")}");
        }
    }

    /// <summary>
    /// Updates velocity tracking for all fingers currently touching UI.
    /// Calculates smoothed velocity based on position changes over time.
    /// </summary>
    void UpdateFingerVelocities()
    {
        // if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] UpdateFingerVelocities() called");
        float deltaTime = Time.deltaTime;
        if (deltaTime <= 0f) return;

        foreach (int fingerIndex in fingersTouchingUI)
        {
            if (fingerIndex < 0 || fingerIndex >= fingertipTransforms.Length)
                continue;

            if (fingertipTransforms[fingerIndex] == null)
                continue;

            Vector3 currentPosition = fingertipTransforms[fingerIndex].position;
            float currentTime = Time.time;

            // Calculate instantaneous velocity
            float timeDelta = currentTime - previousFingerTimes[fingerIndex];
            if (timeDelta > 0f && timeDelta < 0.1f) // Only use recent samples (within 100ms)
            {
                float distance = Vector3.Distance(currentPosition, previousFingerPositions[fingerIndex]);
                float instantaneousVelocity = distance / timeDelta;

                // Apply smoothing
                smoothedFingerVelocities[fingerIndex] = Mathf.Lerp(
                    smoothedFingerVelocities[fingerIndex],
                    instantaneousVelocity,
                    1f - velocitySmoothing
                );
            }
            else
            {
                // If too much time has passed, reset velocity
                smoothedFingerVelocities[fingerIndex] = 0f;
            }

            // Update previous values for next frame
            previousFingerPositions[fingerIndex] = currentPosition;
            previousFingerTimes[fingerIndex] = currentTime;
        }
    }

    /// <summary>
    /// Gets the normalized velocity (0-1) for a finger based on min/max speed thresholds.
    /// </summary>
    float GetNormalizedVelocity(int fingerIndex)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] GetNormalizedVelocity({fingerIndex}) called");
        if (fingerIndex < 0 || fingerIndex >= smoothedFingerVelocities.Length)
            return 0f;

        float velocity = smoothedFingerVelocities[fingerIndex];
        
        if (velocity < minSpeedForScaling)
            return 0f; // Below minimum, no scaling
        
        if (velocity >= maxSpeedForScaling)
            return 1f; // At or above maximum, full scaling
        
        // Linear interpolation between min and max
        return (velocity - minSpeedForScaling) / (maxSpeedForScaling - minSpeedForScaling);
    }

    /// <summary>
    /// Calculates velocity-based intensity multiplier.
    /// </summary>
    float GetVelocityBasedIntensity(int fingerIndex)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] GetVelocityBasedIntensity({fingerIndex}) called");
        if (!enableVelocityBasedHaptics)
            return contactHapticIntensity;

        float normalizedVelocity = GetNormalizedVelocity(fingerIndex);
        float intensityMultiplier = 1f + (maxIntensityMultiplier * normalizedVelocity);
        
        return contactHapticIntensity * intensityMultiplier;
    }

    /// <summary>
    /// Calculates velocity-based pulse interval (lower = faster pulses).
    /// </summary>
    float GetVelocityBasedInterval(int fingerIndex)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] GetVelocityBasedInterval({fingerIndex}) called");
        if (!enableVelocityBasedHaptics)
            return contactHapticInterval;

        float normalizedVelocity = GetNormalizedVelocity(fingerIndex);
        float rateMultiplier = 1f + (maxRateMultiplier * normalizedVelocity);
        
        // Divide interval by rate multiplier to make pulses faster at higher speeds
        return contactHapticInterval / rateMultiplier;
    }

    void StartContactHapticCoroutine()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] StartContactHapticCoroutine() called");
        // Start coroutine to send continuous minor haptic pulses to fingers touching UI
        StartCoroutine(ContactHapticPulseLoop());
    }

    System.Collections.IEnumerator ContactHapticPulseLoop()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] ContactHapticPulseLoop() called");
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] Contact haptic pulse loop started for {(isLeftHand ? "Left" : "Right")} hand (interval: {contactHapticInterval}s). Continuous haptics only sent when fingers are moving (velocity >= {minVelocityForContinuousHaptics} m/s).");
        }

        while (true)
        {
            // Calculate the minimum interval among all active fingers for efficient checking
            // This ensures we check frequently enough for fast-moving fingers
            float minInterval = contactHapticInterval;
            if (enableVelocityBasedHaptics && fingersTouchingUI.Count > 0)
            {
                foreach (int fingerIndex in fingersTouchingUI)
                {
                    if (fingerIndex >= 0 && fingerIndex < fingertipTransforms.Length && fingertipTransforms[fingerIndex] != null)
                    {
                        float fingerInterval = GetVelocityBasedInterval(fingerIndex);
                        if (fingerInterval < minInterval)
                        {
                            minInterval = fingerInterval;
                        }
                    }
                }
            }
            
            // Wait for the minimum interval (or base interval if no fingers touching)
            yield return new WaitForSeconds(minInterval);

            // Check if bHapticsGlove is still valid
            if (bHapticsGlove == null)
            {
                // Try to reacquire it
                bHapticsGlove = BhapticsPhysicsGlove.Instance;
                if (bHapticsGlove == null)
                {
                    BhapticsPhysicsGlove[] allGloves = FindObjectsOfType<BhapticsPhysicsGlove>();
                    if (allGloves != null && allGloves.Length > 0)
                    {
                        bHapticsGlove = allGloves[0];
                        if (showDebugLogs)
                        {
                            Debug.Log($"[BHapticsFingertipHaptics] Reacquired bHapticsGlove for {(isLeftHand ? "Left" : "Right")} hand in contact haptic loop");
                        }
                    }
                    else
                    {
                        continue; // Skip this iteration if still null
                    }
                }
            }

            // Send continuous contact haptics to fingers that are touching UI AND moving (velocity >= threshold)
            // Initial touch haptics are handled separately in OnFingertipTrigger
            // Create a copy of the set to avoid modification during iteration
            int[] fingersToProcess = new int[fingersTouchingUI.Count];
            fingersTouchingUI.CopyTo(fingersToProcess);
            
            if (showDebugLogs && fingersToProcess.Length > 0)
            {
                Debug.Log($"[BHapticsFingertipHaptics] ContactHapticPulseLoop: Processing {fingersToProcess.Length} finger(s) touching UI for {(isLeftHand ? "Left" : "Right")} hand: [{string.Join(", ", fingersToProcess)}]");
            }
            
            foreach (int fingerIndex in fingersToProcess)
            {
                if (fingerIndex < 0 || fingerIndex >= fingertipTransforms.Length)
                {
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Invalid finger index {fingerIndex} in fingersTouchingUI for {(isLeftHand ? "Left" : "Right")} hand");
                    }
                    continue;
                }
                    
                if (fingertipTransforms[fingerIndex] == null)
                {
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Fingertip transform is null for finger {fingerIndex} ({fingerNames[fingerIndex]}) on {(isLeftHand ? "Left" : "Right")} hand");
                    }
                    continue;
                }

                // Only send continuous haptics if finger is moving (velocity above threshold)
                // Initial touch haptic is handled separately in OnFingertipTrigger
                float currentVelocity = smoothedFingerVelocities[fingerIndex];
                bool isMoving = enableVelocityBasedHaptics && currentVelocity >= minVelocityForContinuousHaptics;
                
                if (!isMoving)
                {
                    // Finger is not moving, skip continuous haptics
                    if (showDebugLogs && Time.frameCount % 60 == 0) // Log occasionally to avoid spam
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Skipping continuous haptic for {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} - velocity ({currentVelocity:F4} m/s) below threshold ({minVelocityForContinuousHaptics:F4} m/s)");
                    }
                    continue;
                }
                
                // Get velocity-based interval for this finger
                float fingerInterval = GetVelocityBasedInterval(fingerIndex);
                
                // Check if enough time has passed since last contact haptic
                // BUTTON PRESS HAPTICS TAKE PRIORITY - check button press cooldown first
                // Only check button press cooldown if a button press has actually happened (lastButtonPressTime > 0)
                bool canSendContactHaptic = (lastButtonPressTime[fingerIndex] == 0 || 
                                            Time.time - lastButtonPressTime[fingerIndex] >= buttonPressContactCooldown) &&
                                            Time.time - lastContactHapticTime[fingerIndex] >= fingerInterval;
                
                if (canSendContactHaptic)
                {
                    // Send continuous contact haptic pulse to this finger (with velocity-based intensity)
                    SendContactHapticPulse(fingerIndex);
                    lastContactHapticTime[fingerIndex] = Time.time;
                }
                else if (showDebugLogs && lastButtonPressTime[fingerIndex] > 0 && 
                         Time.time - lastButtonPressTime[fingerIndex] < buttonPressContactCooldown)
                {
                    // Log when contact haptics are skipped due to button press priority (only for index finger)
                    if (fingerIndex == 1)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Skipping contact haptic for {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} - button press haptic has priority (cooldown: {buttonPressContactCooldown - (Time.time - lastButtonPressTime[fingerIndex]):F2}s remaining)");
                    }
                }
            }
        }
    }

    void OnDestroy()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] OnDestroy() called");
        // Unsubscribe from PokeInteractor events
        if (pokeInteractor != null)
        {
            pokeInteractor.WhenInteractableSet.Action -= OnPokeInteractableSet;
            pokeInteractor.WhenInteractableUnset.Action -= OnPokeInteractableUnset;
            pokeInteractor.WhenInteractableSelected.Action -= OnPokeInteractableSelected;
            pokeInteractor.WhenInteractableUnselected.Action -= OnPokeInteractableUnselected;
        }
        
        // Unsubscribe from RayInteractor events
        if (rayInteractor != null)
        {
            rayInteractor.WhenInteractableSelected.Action -= OnRayInteractableSelected;
            rayInteractor.WhenInteractableUnselected.Action -= OnRayInteractableUnselected;
        }
    }

    void InitializeComponents()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] InitializeComponents() called");
        // First, try to find SyntheticHand and get its HandVisual child
        // SyntheticHand is on "[BB] Synthetic Left Hand" or "[BB] Synthetic Right Hand"
        string syntheticHandName = isLeftHand ? "[BB] Synthetic Left Hand" : "[BB] Synthetic Right Hand";
        GameObject syntheticHandObj = GameObject.Find(syntheticHandName);
        
        if (syntheticHandObj != null)
        {
            // Find HandVisual component in children (should be on OVRLeftHandVisual/OVRRightHandVisual)
            string handVisualName = isLeftHand ? "OVRLeftHandVisual" : "OVRRightHandVisual";
            Transform handVisualTransform = syntheticHandObj.transform.Find(handVisualName);
            
            if (handVisualTransform != null)
            {
                Component[] allComponents = handVisualTransform.GetComponents<Component>();
                foreach (var comp in allComponents)
                {
                    if (comp != null && comp.GetType().Name.Contains("HandVisual"))
                    {
                        // Verify this HandVisual is for the correct hand by checking its Hand property
                        bool isCorrectHand = VerifyHandVisualHand(comp as MonoBehaviour);
                        if (isCorrectHand || handVisual == null) // Use first match if we can't verify, or verified correct one
                        {
                            handVisual = comp as MonoBehaviour;
                            if (showDebugLogs)
                            {
                                Debug.Log($"[BHapticsFingertipHaptics] Found HandVisual on {handVisualTransform.name} via SyntheticHand (verified: {isCorrectHand})");
                            }
                            if (isCorrectHand)
                            {
                                break; // Found correct hand, stop searching
                            }
                        }
                    }
                }
            }
        }
        
        // Auto-detect HandVisual if not found via SyntheticHand (fallback method)
        if (handVisual == null)
        {
            // Find HandVisual component (typically on OVRLeftHandVisual/OVRRightHandVisual)
            string handVisualName = isLeftHand ? "OVRLeftHandVisual" : "OVRRightHandVisual";
            GameObject handVisualObj = GameObject.Find(handVisualName);
            
            if (handVisualObj != null)
            {
                Component[] allComponents = handVisualObj.GetComponents<Component>();
                foreach (var comp in allComponents)
                {
                    if (comp != null && comp.GetType().Name.Contains("HandVisual"))
                    {
                        // Verify this HandVisual is for the correct hand
                        bool isCorrectHand = VerifyHandVisualHand(comp as MonoBehaviour);
                        if (isCorrectHand || handVisual == null)
                        {
                            handVisual = comp as MonoBehaviour;
                            if (showDebugLogs)
                            {
                                Debug.Log($"[BHapticsFingertipHaptics] Found HandVisual on {handVisualObj.name} (verified: {isCorrectHand})");
                            }
                            if (isCorrectHand)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            
            // If not found by name, search all objects
            if (handVisual == null)
            {
                MonoBehaviour[] allMonoBehaviours = FindObjectsOfType<MonoBehaviour>();
                foreach (var mb in allMonoBehaviours)
                {
                    if (mb != null && mb.GetType().Name.Contains("HandVisual"))
                    {
                        // Check if it's for the correct hand by checking the Hand property or GameObject name
                        string objName = mb.gameObject.name;
                        bool isCorrectHand = (isLeftHand && objName.Contains("Left")) || 
                                           (!isLeftHand && objName.Contains("Right"));
                        
                        if (isCorrectHand)
                        {
                            // Double-check by verifying Hand property
                            bool verified = VerifyHandVisualHand(mb);
                            if (verified || handVisual == null)
                            {
                                handVisual = mb;
                                if (showDebugLogs)
                                {
                                    Debug.Log($"[BHapticsFingertipHaptics] Found HandVisual on {mb.gameObject.name} (verified: {verified})");
                                }
                                if (verified)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
        
        if (handVisual == null)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] No HandVisual found! Will try to find fingertip transforms by name or use manual assignments.");
            }
        }

        // Get bHaptics glove instance if not assigned
        // Note: bHaptics SDK typically uses a singleton, but we'll try to find it for this hand
        if (bHapticsGlove == null)
        {
            // Try to get the singleton instance first
            bHapticsGlove = BhapticsPhysicsGlove.Instance;
            
            // If singleton is null, try to find any BhapticsPhysicsGlove in the scene
            if (bHapticsGlove == null)
            {
                BhapticsPhysicsGlove[] allGloves = FindObjectsOfType<BhapticsPhysicsGlove>();
                if (allGloves != null && allGloves.Length > 0)
                {
                    // Use the first one found (typically singleton pattern)
                    bHapticsGlove = allGloves[0];
                    if (showDebugLogs)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Found BhapticsPhysicsGlove via FindObjectsOfType for {(isLeftHand ? "Left" : "Right")} hand");
                    }
                }
            }
            else if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Found BhapticsPhysicsGlove singleton instance for {(isLeftHand ? "Left" : "Right")} hand");
            }
            
            if (bHapticsGlove == null)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] No BhapticsPhysicsGlove instance found for {(isLeftHand ? "Left" : "Right")} hand. Haptic feedback will be disabled. Please assign bHapticsGlove in the Inspector.");
            }
        }
        else if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] Using assigned BhapticsPhysicsGlove for {(isLeftHand ? "Left" : "Right")} hand");
        }

        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] Initialized for {(isLeftHand ? "Left" : "Right")} hand");
        }
    }

    void FindPokeInteractor()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] FindPokeInteractor() called");
        // Auto-detect PokeInteractor if not assigned
        if (pokeInteractor == null)
        {
            // Try to find by name first (typical Meta XR SDK Building Blocks naming)
            string handName = isLeftHand ? "Left" : "Right";
            GameObject handObj = GameObject.Find($"{handName}Hand");
            
            if (handObj != null)
            {
                // Look for HandPokeInteractor as a child
                Transform pokeInteractorTransform = handObj.transform.Find("HandPokeInteractor");
                if (pokeInteractorTransform != null)
                {
                    pokeInteractor = pokeInteractorTransform.GetComponent<PokeInteractor>();
                }
            }

            // If not found, search all PokeInteractors and match by hand
            if (pokeInteractor == null)
            {
                PokeInteractor[] allPokeInteractors = FindObjectsOfType<PokeInteractor>();
                foreach (var pi in allPokeInteractors)
                {
                    if (pi == null) continue;
                    
                    // Check if this PokeInteractor is for the correct hand
                    string objName = pi.gameObject.name;
                    Transform parent = pi.transform.parent;
                    bool isCorrectHand = false;
                    
                    // Check GameObject name and parent hierarchy
                    if ((isLeftHand && (objName.Contains("Left") || objName.Contains("l_")) && !objName.Contains("Right") && !objName.Contains("r_")) ||
                        (!isLeftHand && (objName.Contains("Right") || objName.Contains("r_")) && !objName.Contains("Left") && !objName.Contains("l_")))
                    {
                        isCorrectHand = true;
                    }
                    else
                    {
                        // Check parent hierarchy
                        while (parent != null && !isCorrectHand)
                        {
                            string parentName = parent.name;
                            if ((isLeftHand && parentName.Contains("Left") && !parentName.Contains("Right")) ||
                                (!isLeftHand && parentName.Contains("Right") && !parentName.Contains("Left")))
                            {
                                isCorrectHand = true;
                                break;
                            }
                            parent = parent.parent;
                        }
                    }
                    
                    if (isCorrectHand)
                    {
                        pokeInteractor = pi;
                        if (showDebugLogs)
                        {
                            Debug.Log($"[BHapticsFingertipHaptics] Found PokeInteractor for {(isLeftHand ? "Left" : "Right")} hand: {pi.gameObject.name}");
                        }
                        break;
                    }
                }
            }

            // If still not found, use first available as fallback
            if (pokeInteractor == null)
            {
                PokeInteractor[] allPokeInteractors = FindObjectsOfType<PokeInteractor>();
                if (allPokeInteractors.Length > 0)
                {
                    // If there are two, try to pick the right one based on isLeftHand
                    if (allPokeInteractors.Length >= 2 && isLeftHand)
                    {
                        // Try to find left one
                        foreach (var pi in allPokeInteractors)
                        {
                            if (pi != null && (pi.gameObject.name.Contains("Left") || pi.transform.parent?.name.Contains("Left") == true))
                            {
                                pokeInteractor = pi;
                                break;
                            }
                        }
                    }
                    
                    if (pokeInteractor == null)
                    {
                        pokeInteractor = allPokeInteractors[isLeftHand ? 0 : Mathf.Min(1, allPokeInteractors.Length - 1)];
                    }
                    
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Using fallback PokeInteractor: {pokeInteractor.gameObject.name}");
                    }
                }
            }
        }

        // Subscribe to PokeInteractor events for button press detection
        if (pokeInteractor != null)
        {
            pokeInteractor.WhenInteractableSet.Action += OnPokeInteractableSet;
            pokeInteractor.WhenInteractableUnset.Action += OnPokeInteractableUnset;
            pokeInteractor.WhenInteractableSelected.Action += OnPokeInteractableSelected;
            pokeInteractor.WhenInteractableUnselected.Action += OnPokeInteractableUnselected;
            
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Subscribed to PokeInteractor events for {(isLeftHand ? "Left" : "Right")} hand");
            }
        }
        else
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] No PokeInteractor found for {(isLeftHand ? "Left" : "Right")} hand. Button press detection will not work.");
            }
        }
    }

    void FindRayInteractor()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] FindRayInteractor() called");
        // Auto-detect RayInteractor if not assigned
        if (rayInteractor == null)
        {
            // Try to find by name first (typical Meta XR SDK Building Blocks naming)
            string handName = isLeftHand ? "Left" : "Right";
            GameObject handObj = GameObject.Find($"{handName}Hand");
            
            if (handObj != null)
            {
                // Look for HandRayInteractor as a child
                Transform rayInteractorTransform = handObj.transform.Find("HandRayInteractor");
                if (rayInteractorTransform == null)
                {
                    // Try alternative naming
                    rayInteractorTransform = handObj.transform.Find($"{handName}HandRayInteractor");
                }
                if (rayInteractorTransform != null)
                {
                    rayInteractor = rayInteractorTransform.GetComponent<RayInteractor>();
                }
            }

            // If not found, search all RayInteractors and match by hand
            if (rayInteractor == null)
            {
                RayInteractor[] allRayInteractors = FindObjectsOfType<RayInteractor>();
                foreach (var ri in allRayInteractors)
                {
                    if (ri == null) continue;
                    
                    // Check if this RayInteractor is for the correct hand
                    string objName = ri.gameObject.name;
                    Transform parent = ri.transform.parent;
                    bool isCorrectHand = false;
                    
                    // Check GameObject name and parent hierarchy
                    if ((isLeftHand && (objName.Contains("Left") || objName.Contains("l_")) && !objName.Contains("Right") && !objName.Contains("r_")) ||
                        (!isLeftHand && (objName.Contains("Right") || objName.Contains("r_")) && !objName.Contains("Left") && !objName.Contains("l_")))
                    {
                        isCorrectHand = true;
                    }
                    else
                    {
                        // Check parent hierarchy
                        while (parent != null && !isCorrectHand)
                        {
                            string parentName = parent.name;
                            if ((isLeftHand && parentName.Contains("Left") && !parentName.Contains("Right")) ||
                                (!isLeftHand && parentName.Contains("Right") && !parentName.Contains("Left")))
                            {
                                isCorrectHand = true;
                                break;
                            }
                            parent = parent.parent;
                        }
                    }
                    
                    if (isCorrectHand)
                    {
                        rayInteractor = ri;
                        if (showDebugLogs)
                        {
                            Debug.Log($"[BHapticsFingertipHaptics] Found RayInteractor for {(isLeftHand ? "Left" : "Right")} hand: {ri.gameObject.name}");
                        }
                        break;
                    }
                }
            }

            // If still not found, use first available as fallback
            if (rayInteractor == null)
            {
                RayInteractor[] allRayInteractors = FindObjectsOfType<RayInteractor>();
                if (allRayInteractors.Length > 0)
                {
                    // If there are two, try to pick the right one based on isLeftHand
                    if (allRayInteractors.Length >= 2 && isLeftHand)
                    {
                        // Try to find left one
                        foreach (var ri in allRayInteractors)
                        {
                            if (ri != null && (ri.gameObject.name.Contains("Left") || ri.transform.parent?.name.Contains("Left") == true))
                            {
                                rayInteractor = ri;
                                break;
                            }
                        }
                    }
                    
                    if (rayInteractor == null)
                    {
                        rayInteractor = allRayInteractors[isLeftHand ? 0 : Mathf.Min(1, allRayInteractors.Length - 1)];
                    }
                    
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Using fallback RayInteractor: {rayInteractor.gameObject.name}");
                    }
                }
            }
        }

        // Subscribe to RayInteractor events for pinch interaction detection
        if (rayInteractor != null)
        {
            rayInteractor.WhenInteractableSelected.Action += OnRayInteractableSelected;
            rayInteractor.WhenInteractableUnselected.Action += OnRayInteractableUnselected;
            
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Subscribed to RayInteractor events for {(isLeftHand ? "Left" : "Right")} hand");
            }
        }
        else
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] No RayInteractor found for {(isLeftHand ? "Left" : "Right")} hand. Pinch interaction haptics will not work.");
            }
        }
    }

    void WaitForSkeletonInitialization()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] WaitForSkeletonInitialization() called");
        // Start coroutine to wait for skeleton initialization
        StartCoroutine(InitializeFingertipsWhenReady());
    }

    System.Collections.IEnumerator InitializeFingertipsWhenReady()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] InitializeFingertipsWhenReady() called");
        // Wait a few frames for HandVisual to initialize
        yield return new WaitForSeconds(0.2f);
        yield return null;

        // Try to get joint transforms multiple times in case they're not ready yet
        int attempts = 0;
        Transform[] jointTransforms = GetJointTransforms();
        
        while (jointTransforms == null && attempts < 20)
        {
            yield return new WaitForSeconds(0.1f);
            jointTransforms = GetJointTransforms();
            attempts++;
        }

        SetupFingertipColliders(jointTransforms);

        // Update Inspector fields to show what was found
        UpdateInspectorFields();

        // Verify collider setup
        int collidersSetUp = 0;
        for (int i = 0; i < fingertipColliders.Length; i++)
        {
            if (fingertipColliders[i] != null && fingertipColliders[i].enabled)
            {
                collidersSetUp++;
            }
        }

        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] Fingertip colliders set up for {(isLeftHand ? "Left" : "Right")} hand: {collidersSetUp}/{fingertipColliders.Length} colliders active");
            
            // Detailed logging for each fingertip
            for (int i = 0; i < fingertipTransforms.Length; i++)
            {
                if (fingertipTransforms[i] != null)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[i]}: Transform={fingertipTransforms[i].name}, Collider={(fingertipColliders[i] != null ? "OK" : "NULL")}, Enabled={(fingertipColliders[i] != null && fingertipColliders[i].enabled ? "YES" : "NO")}");
                }
                else
                {
                    Debug.LogWarning($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[i]}: Transform is NULL!");
                }
            }
        }
        
        if (collidersSetUp == 0)
        {
            Debug.LogError($"[BHapticsFingertipHaptics] WARNING: No fingertip colliders were set up for {(isLeftHand ? "Left" : "Right")} hand! Check HandVisual reference. handVisual={(handVisual != null ? handVisual.name : "NULL")}");
        }
    }

    /// <summary>
    /// Updates the Inspector fields to show the found transforms (for debugging/visibility).
    /// </summary>
    void UpdateInspectorFields()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] UpdateInspectorFields() called");
        if (fingertipTransforms[0] != null && thumbTip == null) thumbTip = fingertipTransforms[0];
        if (fingertipTransforms[1] != null && indexTip == null) indexTip = fingertipTransforms[1];
        if (fingertipTransforms[2] != null && middleTip == null) middleTip = fingertipTransforms[2];
        if (fingertipTransforms[3] != null && ringTip == null) ringTip = fingertipTransforms[3];
        if (fingertipTransforms[4] != null && pinkyTip == null) pinkyTip = fingertipTransforms[4];
    }

    void SetupFingertipColliders(Transform[] jointTransforms = null)
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] SetupFingertipColliders() called");
        // Get joint transforms if not provided
        if (jointTransforms == null)
        {
            jointTransforms = GetJointTransforms(); // Gets joint transforms from HandVisual
        }

        // Try to get fingertip transforms from multiple sources
        Transform[] fingertipSources = new Transform[]
        {
            thumbTip, indexTip, middleTip, ringTip, pinkyTip
        };

        // First, try manual assignments
        bool hasManualAssignments = false;
        for (int i = 0; i < fingertipSources.Length; i++)
        {
            if (fingertipSources[i] != null)
            {
                fingertipTransforms[i] = fingertipSources[i];
                hasManualAssignments = true;
            }
        }

        // If manual assignments exist, use them; otherwise try joint transforms or find by name
        if (!hasManualAssignments)
        {
            if (jointTransforms != null)
            {
                // Use OpenXR/MetaXR fingertip indices (Meta XR SDK Building Blocks uses OpenXR with 26 joints)
                int[] indicesToUse = fingertipJointIndicesOpenXR;
                
                if (showDebugLogs && jointTransforms.Length >= 26)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] Using OpenXR/MetaXR fingertip indices (26 joints detected) for {(isLeftHand ? "Left" : "Right")} hand");
                }
                
                // Extract fingertips from joint transforms array
                for (int i = 0; i < indicesToUse.Length; i++)
                {
                    int jointIndex = indicesToUse[i];
                    if (jointIndex < jointTransforms.Length && jointTransforms[jointIndex] != null)
                    {
                        fingertipTransforms[i] = jointTransforms[jointIndex];
                        if (showDebugLogs)
                        {
                            Debug.Log($"[BHapticsFingertipHaptics] Found {fingerNames[i]} fingertip at joint index {jointIndex}: {jointTransforms[jointIndex].name}");
                        }
                    }
                }
            }
            else
            {
                // Fallback: Try to find fingertip transforms by name in the scene
                FindFingertipsByName();
            }
        }

        // Set up colliders for each found fingertip
        for (int i = 0; i < fingertipTransforms.Length; i++)
        {
            Transform fingertipTransform = fingertipTransforms[i];
            
            if (fingertipTransform != null)
            {
                // CRITICAL: Ensure the GameObject is active so physics triggers work!
                // Inactive GameObjects don't trigger OnTriggerEnter/OnTriggerExit
                if (!fingertipTransform.gameObject.activeInHierarchy)
                {
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[i]} GameObject is inactive! Activating it so triggers will work.");
                    }
                    fingertipTransform.gameObject.SetActive(true);
                }
                
                // Also ensure parent hierarchy is active (walk up the hierarchy)
                Transform parent = fingertipTransform.parent;
                while (parent != null)
                {
                    if (!parent.gameObject.activeSelf)
                    {
                        if (showDebugLogs)
                        {
                            Debug.LogWarning($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[i]} parent {parent.name} is inactive! Activating it.");
                        }
                        parent.gameObject.SetActive(true);
                    }
                    parent = parent.parent;
                }
                
                // Add or get SphereCollider component
                SphereCollider collider = fingertipTransform.GetComponent<SphereCollider>();
                if (collider == null)
                {
                    collider = fingertipTransform.gameObject.AddComponent<SphereCollider>();
                }

                collider.radius = colliderRadius;
                collider.isTrigger = isTrigger;
                collider.center = Vector3.zero; // Center at the fingertip transform

                // Ensure the GameObject has a Rigidbody for trigger detection to work reliably
                // (Triggers need at least one Rigidbody in the collision pair)
                // Note: Our trigger colliders shouldn't interfere with PokeInteractor since PokeInteractor
                // uses its own collision detection system that's separate from Unity's physics
                Rigidbody rb = fingertipTransform.GetComponent<Rigidbody>();
                if (rb == null)
                {
                    rb = fingertipTransform.gameObject.AddComponent<Rigidbody>();
                    rb.isKinematic = true; // Kinematic so it doesn't move from physics
                    rb.useGravity = false;
                }
                
                // Verify Rigidbody is set up correctly
                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[i]} collider setup: radius={collider.radius}, isTrigger={collider.isTrigger}, hasRigidbody={rb != null}, isKinematic={(rb != null ? rb.isKinematic : false)}, layer={fingertipTransform.gameObject.layer}, activeInHierarchy={fingertipTransform.gameObject.activeInHierarchy}");
                }

                // Add FingertipCollider component to handle collision events
                FingertipColliderHandler handler = fingertipTransform.GetComponent<FingertipColliderHandler>();
                if (handler == null)
                {
                    handler = fingertipTransform.gameObject.AddComponent<FingertipColliderHandler>();
                }

                handler.Initialize(this, i, fingerNames[i], isLeftHand);
                
                // Verify handler was initialized correctly
                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[i]} handler initialized: handler={(handler != null ? "OK" : "NULL")}, collider.enabled={collider.enabled}, collider.isTrigger={collider.isTrigger}, gameObject.activeInHierarchy={fingertipTransform.gameObject.activeInHierarchy}");
                }

                fingertipColliders[i] = collider;

                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] Set up {fingerNames[i]} fingertip collider on {fingertipTransform.name} (hand: {(isLeftHand ? "Left" : "Right")}, fingerIndex: {i}, transform: {(fingertipTransform != null ? fingertipTransform.name : "NULL")})");
                }
            }
            else
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Could not find {fingerNames[i]} fingertip transform. Check manual assignments or HandVisual joint transforms.");
            }
        }
    }
    
    /// <summary>
    /// Finds fingertip transforms by searching for them by name in the scene hierarchy.
    /// </summary>
    void FindFingertipsByName()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] FindFingertipsByName() called");
        string handPrefix = isLeftHand ? "LeftHand" : "RightHand";
        string handMarkerPrefix = isLeftHand ? "l_" : "r_";
        string[] tipNames = new string[]
        {
            $"{handPrefix}ThumbTip",
            $"{handPrefix}IndexTip",
            $"{handPrefix}MiddleTip",
            $"{handPrefix}RingTip",
            $"{handPrefix}PinkyTip"
        };
        
        // Also try HandVisual marker names (l_thumb_finger_tip_marker, etc.)
        string[] markerTipNames = new string[]
        {
            $"{handMarkerPrefix}thumb_finger_tip_marker",
            $"{handMarkerPrefix}index_finger_tip_marker",
            $"{handMarkerPrefix}middle_finger_tip_marker",
            $"{handMarkerPrefix}ring_finger_tip_marker",
            $"{handMarkerPrefix}pinky_finger_tip_marker"
        };
        
        // Search in the scene
        Transform[] allTransforms = FindObjectsOfType<Transform>();
        
        // First, try standard LeftHand/RightHand naming
        foreach (var transform in allTransforms)
        {
            if (transform == null) continue;
            
            for (int i = 0; i < tipNames.Length; i++)
            {
                if (transform.name == tipNames[i] && fingertipTransforms[i] == null)
                {
                    fingertipTransforms[i] = transform;
                    if (showDebugLogs)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Found {fingerNames[i]} tip by name: {transform.name}");
                    }
                }
            }
        }
        
        // Try HandVisual marker names (l_thumb_finger_tip_marker, etc.)
        if (fingertipTransforms[0] == null || fingertipTransforms[1] == null)
        {
            foreach (var transform in allTransforms)
            {
                if (transform == null) continue;
                
                for (int i = 0; i < markerTipNames.Length; i++)
                {
                    if (transform.name == markerTipNames[i] && fingertipTransforms[i] == null)
                    {
                        fingertipTransforms[i] = transform;
                        if (showDebugLogs)
                        {
                            Debug.Log($"[BHapticsFingertipHaptics] Found {fingerNames[i]} tip by marker name: {transform.name}");
                        }
                    }
                }
            }
        }
        
        // Also try alternative naming (LeftHandThumbTip format)
        if (fingertipTransforms[0] == null || fingertipTransforms[1] == null)
        {
            string altPrefix = isLeftHand ? "Left" : "Right";
            string[] altTipNames = new string[]
            {
                $"{altPrefix}HandThumbTip",
                $"{altPrefix}HandIndexTip",
                $"{altPrefix}HandMiddleTip",
                $"{altPrefix}HandRingTip",
                $"{altPrefix}HandPinkyTip"
            };
            
            foreach (var transform in allTransforms)
            {
                if (transform == null) continue;
                
                for (int i = 0; i < altTipNames.Length; i++)
                {
                    if (transform.name == altTipNames[i] && fingertipTransforms[i] == null)
                    {
                        fingertipTransforms[i] = transform;
                        if (showDebugLogs)
                        {
                            Debug.Log($"[BHapticsFingertipHaptics] Found {fingerNames[i]} tip by alternative name: {transform.name}");
                        }
                    }
                }
            }
        }
        
        // Try XRHand naming (XRHand_ThumbTip, etc.)
        if (fingertipTransforms[0] == null || fingertipTransforms[1] == null)
        {
            string[] xrTipNames = new string[]
            {
                "XRHand_ThumbTip",
                "XRHand_IndexTip",
                "XRHand_MiddleTip",
                "XRHand_RingTip",
                "XRHand_LittleTip"  // Note: Little = Pinky
            };
            
            // Only use XRHand naming if it's on the correct hand's hierarchy
            foreach (var transform in allTransforms)
            {
                if (transform == null) continue;
                
                // Check if this transform is in the correct hand's hierarchy
                Transform parent = transform.parent;
                bool isInCorrectHand = false;
                while (parent != null)
                {
                    string parentName = parent.name;
                    if ((isLeftHand && parentName.Contains("Left")) || (!isLeftHand && parentName.Contains("Right")))
                    {
                        isInCorrectHand = true;
                        break;
                    }
                    parent = parent.parent;
                }
                
                if (!isInCorrectHand) continue;
                
                for (int i = 0; i < xrTipNames.Length; i++)
                {
                    if (transform.name == xrTipNames[i] && fingertipTransforms[i] == null)
                    {
                        fingertipTransforms[i] = transform;
                        if (showDebugLogs)
                        {
                            Debug.Log($"[BHapticsFingertipHaptics] Found {fingerNames[i]} tip by XRHand name: {transform.name}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets joint transforms from HandVisual using reflection.
    /// </summary>
    Transform[] GetJointTransforms()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] GetJointTransforms() called");
        // Get joint transforms from HandVisual (primary method for Meta XR SDK Building Blocks)
        Transform[] transforms = GetJointTransformsFromHandVisual();
        return transforms;
    }
    
    /// <summary>
    /// Gets joint transforms from HandVisual component (preferred method for Meta XR SDK Building Blocks).
    /// PRIORITIZES OpenXR/MetaXR joints over OVR joints.
    /// Uses the public Joints property which returns IList<Transform>.
    /// </summary>
    Transform[] GetJointTransformsFromHandVisual()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] GetJointTransformsFromHandVisual() called");
        if (handVisual == null)
            return null;

        try
        {
            System.Type handVisualType = handVisual.GetType();
            
            // PRIORITY 1: Try OpenXR/MetaXR joint transforms first (Meta XR SDK Building Blocks uses OpenXR)
            FieldInfo openXRJointTransformsField = handVisualType.GetField("_openXRJointTransforms", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (openXRJointTransformsField != null)
            {
                object jointTransformsObj = openXRJointTransformsField.GetValue(handVisual);
                if (jointTransformsObj is IList<Transform> openXRList && openXRList != null && openXRList.Count > 0)
                {
                    Transform[] transforms = new Transform[openXRList.Count];
                    for (int i = 0; i < openXRList.Count; i++)
                    {
                        transforms[i] = openXRList[i];
                    }
                    
                    if (showDebugLogs)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Found {transforms.Length} OpenXR joint transforms from HandVisual._openXRJointTransforms for {(isLeftHand ? "Left" : "Right")} hand");
                    }
                    return transforms;
                }
            }
            
            // PRIORITY 2: Try the public Joints property (might return OpenXR or OVR joints)
            PropertyInfo jointsProperty = handVisualType.GetProperty("Joints", BindingFlags.Public | BindingFlags.Instance);
            if (jointsProperty != null)
            {
                object jointsObj = jointsProperty.GetValue(handVisual);
                if (jointsObj is IList<Transform> jointsList && jointsList != null && jointsList.Count > 0)
                {
                    Transform[] transforms = new Transform[jointsList.Count];
                    for (int i = 0; i < jointsList.Count; i++)
                    {
                        transforms[i] = jointsList[i];
                    }
                    
                    if (showDebugLogs)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Found {transforms.Length} joint transforms from HandVisual.Joints property for {(isLeftHand ? "Left" : "Right")} hand");
                    }
                    return transforms;
                }
            }
            
            // PRIORITY 3: Fallback to OVR joint transforms
            FieldInfo jointTransformsField = handVisualType.GetField("_jointTransforms", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (jointTransformsField != null)
            {
                object jointTransformsObj = jointTransformsField.GetValue(handVisual);
                if (jointTransformsObj is IList<Transform> jointList && jointList != null && jointList.Count > 0)
                {
                    Transform[] transforms = new Transform[jointList.Count];
                    for (int i = 0; i < jointList.Count; i++)
                    {
                        transforms[i] = jointList[i];
                    }
                    
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Found {transforms.Length} OVR joint transforms from HandVisual._jointTransforms (fallback, not MetaXR) for {(isLeftHand ? "Left" : "Right")} hand");
                    }
                    return transforms;
                }
            }
        }
        catch (System.Exception e)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Error accessing HandVisual joint transforms: {e.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Verifies if a HandVisual component is for the correct hand by checking its Hand property.
    /// </summary>
    bool VerifyHandVisualHand(MonoBehaviour handVisualComp)
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] VerifyHandVisualHand() called");
        if (handVisualComp == null) return false;
        
        try
        {
            System.Type handVisualType = handVisualComp.GetType();
            
            // Try to get the Hand property
            PropertyInfo handProperty = handVisualType.GetProperty("Hand", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (handProperty != null)
            {
                object handValue = handProperty.GetValue(handVisualComp);
                if (handValue != null)
                {
                    // Check if it's a Hand enum or has a HandFlags property
                    string handValueStr = handValue.ToString();
                    
                    // Meta XR SDK uses HandFlags enum with values like HandFlags.Left, HandFlags.Right
                    if (handValueStr.Contains("Left"))
                    {
                        return isLeftHand;
                    }
                    else if (handValueStr.Contains("Right"))
                    {
                        return !isLeftHand;
                    }
                    
                    // Alternative: Try to get HandFlags property from the Hand value
                    PropertyInfo handFlagsProperty = handValue.GetType().GetProperty("HandFlags", BindingFlags.Public | BindingFlags.Instance);
                    if (handFlagsProperty != null)
                    {
                        object handFlags = handFlagsProperty.GetValue(handValue);
                        string handFlagsStr = handFlags != null ? handFlags.ToString() : "";
                        if (handFlagsStr.Contains("Left"))
                        {
                            return isLeftHand;
                        }
                        else if (handFlagsStr.Contains("Right"))
                        {
                            return !isLeftHand;
                        }
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Error verifying HandVisual hand: {e.Message}");
            }
        }
        
        // If we can't verify, return true (assume it's correct if name matched)
        return true;
    }

    /// <summary>
    /// Called by FingertipColliderHandler when a trigger collision occurs (for UI elements).
    /// Tracks which fingers are touching UI for continuous contact haptics.
    /// Also tracks interactables for button press detection.
    /// </summary>
    public void OnFingertipTrigger(int fingerIndex, Collider other)
    {
        // if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] OnFingertipTrigger({fingerIndex}) called");
        if (fingerIndex < 0 || fingerIndex >= fingertipTransforms.Length)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] OnFingertipTrigger called with invalid fingerIndex {fingerIndex} for {(isLeftHand ? "Left" : "Right")} hand");
            }
            return;
        }

        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] OnFingertipTrigger called: {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} (fingerIndex={fingerIndex}) touching {other.name} (layer: {other.gameObject.layer})");
        }

        // ALL FINGERS can trigger surface contact haptics when touching UI
        // Check if this is a UI element
        bool shouldTrigger = ShouldTriggerHaptic(other);
        
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] ShouldTriggerHaptic returned {shouldTrigger} for {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} touching {other.name}");
        }
        
        if (shouldTrigger)
        {
            // Verify bHapticsGlove is available before tracking
            if (bHapticsGlove == null)
            {
                // Try to reacquire it
                bHapticsGlove = BhapticsPhysicsGlove.Instance;
                if (bHapticsGlove == null)
                {
                    BhapticsPhysicsGlove[] allGloves = FindObjectsOfType<BhapticsPhysicsGlove>();
                    if (allGloves != null && allGloves.Length > 0)
                    {
                        bHapticsGlove = allGloves[0];
                    }
                }
                
                if (bHapticsGlove == null && showDebugLogs)
                {
                    Debug.LogWarning($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} touching UI but bHapticsGlove is null. Contact haptics will not work until bHapticsGlove is found.");
                }
            }

            // Track that this finger is touching UI
            if (!fingersTouchingUI.Contains(fingerIndex))
            {
                fingersTouchingUI.Add(fingerIndex);
                
                // Initialize the collider set for this finger if needed
                if (!fingerToUIColliders.ContainsKey(fingerIndex))
                {
                    fingerToUIColliders[fingerIndex] = new HashSet<Collider>();
                }
                
                // Initialize velocity tracking for this finger
                if (fingertipTransforms[fingerIndex] != null)
                {
                    previousFingerPositions[fingerIndex] = fingertipTransforms[fingerIndex].position;
                    previousFingerTimes[fingerIndex] = Time.time;
                    smoothedFingerVelocities[fingerIndex] = 0f;
                }
                
                // Track this hand as active for keyboard interactions
                if (isLeftHand)
                {
                    lastLeftHandInteractionTime = Time.time;
                }
                else
                {
                    lastRightHandInteractionTime = Time.time;
                }
                
                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} (fingerIndex={fingerIndex}) started touching UI: {other.name}. Contact haptics will begin. bHapticsGlove: {(bHapticsGlove != null ? "Found" : "NULL")}, fingertipTransform: {(fingertipTransforms[fingerIndex] != null ? fingertipTransforms[fingerIndex].name : "NULL")}, fingersTouchingUI count: {fingersTouchingUI.Count}");
                }
            }
            
            // Track which UI collider this finger is touching
            fingerToUIColliders[fingerIndex].Add(other);

            // Send an initial small haptic pulse when first touching (only once per touch)
            // BUTTON PRESS HAPTICS TAKE PRIORITY - check cooldown first
            // Only check button press cooldown if a button press has actually happened (lastButtonPressTime > 0)
            if (!hasSentInitialTouchHaptic[fingerIndex])
            {
                bool canSendInitialHaptic = (lastButtonPressTime[fingerIndex] == 0 || 
                                            Time.time - lastButtonPressTime[fingerIndex] >= buttonPressContactCooldown);
                
                if (canSendInitialHaptic)
                {
                    // Send a small initial touch haptic pulse (uses base intensity, no velocity data yet)
                    SendInitialTouchHapticPulse(fingerIndex);
                    hasSentInitialTouchHaptic[fingerIndex] = true;
                    lastContactHapticTime[fingerIndex] = Time.time;
                }
            }

            // Find IInteractable component in the collider or its parents
            // Note: Only index finger can actually press buttons, but all fingers can track interactables for contact haptics
            IInteractable interactable = FindInteractable(other);
            if (interactable != null)
            {
                hoveringInteractables.Add(interactable);
                // Store which finger is touching this interactable (for button press detection, only index finger will be used)
                interactableToFingerIndex[interactable] = fingerIndex;
                
                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} touching interactable UI: {other.name}");
                }
            }
        }
    }

    /// <summary>
    /// Called when a fingertip exits a trigger collider.
    /// </summary>
    public void OnFingertipTriggerExit(int fingerIndex, Collider other)
    {
        // if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] OnFingertipTriggerExit({fingerIndex}) called");
        if (fingerIndex < 0 || fingerIndex >= fingertipTransforms.Length)
            return;

        // Remove this collider from the finger's tracked UI colliders
        if (fingerToUIColliders.ContainsKey(fingerIndex))
        {
            fingerToUIColliders[fingerIndex].Remove(other);
            
            // If this finger is no longer touching any UI, remove it from the touching set
            if (fingerToUIColliders[fingerIndex].Count == 0)
            {
                fingersTouchingUI.Remove(fingerIndex);
                fingerToUIColliders.Remove(fingerIndex);
                
                // Reset velocity tracking for this finger
                smoothedFingerVelocities[fingerIndex] = 0f;
                previousFingerPositions[fingerIndex] = Vector3.zero;
                previousFingerTimes[fingerIndex] = 0f;
                
                // Reset initial touch haptic flag so it can send again on next touch
                hasSentInitialTouchHaptic[fingerIndex] = false;
                
                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} no longer touching UI");
                }
            }
        }

        // Find IInteractable component and remove it from tracking
        IInteractable interactable = FindInteractable(other);
        if (interactable != null)
        {
            hoveringInteractables.Remove(interactable);
            interactableToFingerIndex.Remove(interactable);
            
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} left interactable: {other.name}");
            }
        }
    }

    /// <summary>
    /// Finds IInteractable component in the collider or its parent hierarchy.
    /// </summary>
    IInteractable FindInteractable(Collider collider)
    {
        // if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] FindInteractable() called");
        if (collider == null) return null;
        
        Transform checkTransform = collider.transform;
        while (checkTransform != null)
        {
            IInteractable interactable = checkTransform.GetComponent<IInteractable>();
            if (interactable != null)
            {
                return interactable;
            }
            checkTransform = checkTransform.parent;
        }
        return null;
    }

    #region PokeInteractor Event Handlers

    void OnPokeInteractableSet(IInteractable interactable)
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] OnPokeInteractableSet() called");
        hoveringInteractables.Add(interactable);
        
        // ONLY INDEX FINGER can interact with UI - always use index finger (fingerIndex = 1)
        int fingerIndex = 1; // Index finger only
        
        // Store the finger index for this interactable
        if (fingerIndex >= 0 && fingerIndex < fingertipTransforms.Length && fingertipTransforms[fingerIndex] != null)
        {
            interactableToFingerIndex[interactable] = fingerIndex;
        }
        
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} hand poke hover started on {interactable} (finger: Index only)");
        }
    }

    void OnPokeInteractableUnset(IInteractable interactable)
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] OnPokeInteractableUnset() called");
        hoveringInteractables.Remove(interactable);
        interactableToFingerIndex.Remove(interactable);
        
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} hand poke hover ended on {interactable}");
        }
    }

    void OnPokeInteractableSelected(IInteractable interactable)
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] OnPokeInteractableSelected() called");
        // THIS IS THE ACTUAL BUTTON PRESS - ONLY INDEX FINGER CAN PRESS BUTTONS
        // Always use index finger (fingerIndex = 1) - no fallback to closest finger
        int fingerIndex = 1; // Index finger only
        
        // Verify index finger is valid
        if (fingerIndex >= 0 && fingerIndex < fingertipTransforms.Length && fingertipTransforms[fingerIndex] != null)
        {
            // CRITICAL: Temporarily remove finger from contact haptics tracking to ensure button press isn't overridden
            // This ensures contact haptics stop immediately when button is pressed
            bool wasTouchingUI = fingersTouchingUI.Contains(fingerIndex);
            if (wasTouchingUI)
            {
                fingersTouchingUI.Remove(fingerIndex);
                if (showDebugLogs)
                {
                    Debug.Log($"[BHapticsFingertipHaptics] Temporarily removed {(isLeftHand ? "Left" : "Right")} Index finger from contact haptics tracking to prioritize button press");
                }
            }

            // Track button press time FIRST to block any contact haptics
            lastButtonPressTime[fingerIndex] = Time.time;
            lastContactHapticTime[fingerIndex] = Time.time;
            
            // Track this hand as active for keyboard interactions
            if (isLeftHand)
            {
                lastLeftHandInteractionTime = Time.time;
            }
            else
            {
                lastRightHandInteractionTime = Time.time;
            }

            // Trigger STRONG haptic feedback for UI button press (synced with button press sound)
            // Send multiple pulses to make button press haptics stronger and more noticeable
            StartCoroutine(SendButtonPressHapticPulses(fingerIndex));

            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} Index button PRESSED - STRONG haptic triggered on {interactable} (sending {buttonPressPulseCount} pulses)");
            }

            // Re-add finger to contact haptics after a delay (only if it was touching UI before)
            // This allows contact haptics to resume after button press is complete
            if (wasTouchingUI)
            {
                StartCoroutine(ReAddFingerToContactHaptics(fingerIndex, buttonPressContactCooldown));
            }
        }
        else
        {
            if (showDebugLogs)
            {
                Debug.LogError($"[BHapticsFingertipHaptics] Failed to trigger haptic on button press: {interactable}. Index finger not found.");
            }
        }
    }

    /// <summary>
    /// Sends multiple haptic pulses for button press to make it stronger and more noticeable.
    /// </summary>
    System.Collections.IEnumerator SendButtonPressHapticPulses(int fingerIndex)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] SendButtonPressHapticPulses({fingerIndex}) called");
        if (bHapticsGlove == null || fingerIndex < 0 || fingerIndex >= fingertipTransforms.Length)
            yield break;

        if (fingertipTransforms[fingerIndex] == null)
            yield break;

        // Map finger index to bHaptics finger index (0=Thumb, 1=Index, 2=Middle, 3=Ring, 4=Pinky)
        int bHapticsFingerIndex = fingerIndex;

        // Send multiple pulses with small delays between them
        for (int i = 0; i < buttonPressPulseCount; i++)
        {
            if (bHapticsGlove != null)
            {
                try
                {
                    bHapticsGlove.SendEnterHaptic(isLeftHand, bHapticsFingerIndex);
                    
                    if (showDebugLogs && i == 0)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} BUTTON PRESS haptic pulse {i + 1}/{buttonPressPulseCount} (intensity: {buttonPressHapticIntensity:F1})");
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[BHapticsFingertipHaptics] Error sending button press haptic pulse {i + 1}: {e.Message}");
                }
            }

            // Wait before next pulse (except for the last one)
            if (i < buttonPressPulseCount - 1)
            {
                yield return new WaitForSeconds(buttonPressPulseInterval);
            }
        }
    }

    /// <summary>
    /// Re-adds a finger to contact haptics tracking after button press cooldown.
    /// Only re-adds if the finger is still touching UI.
    /// </summary>
    System.Collections.IEnumerator ReAddFingerToContactHaptics(int fingerIndex, float delay)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] ReAddFingerToContactHaptics({fingerIndex}, {delay}) called");
        yield return new WaitForSeconds(delay);

        // Only re-add if finger is still touching UI (check via colliders)
        if (fingerToUIColliders.ContainsKey(fingerIndex) && 
            fingerToUIColliders[fingerIndex].Count > 0 &&
            !fingersTouchingUI.Contains(fingerIndex))
        {
            fingersTouchingUI.Add(fingerIndex);
            
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Re-added {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} to contact haptics tracking after button press cooldown");
            }
        }
    }

    void OnPokeInteractableUnselected(IInteractable interactable)
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] OnPokeInteractableUnselected() called");
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} hand button RELEASED on {interactable}");
        }
    }

    /// <summary>
    /// Finds which finger index is associated with an interactable.
    /// Checks stored mappings first, then finds closest fingertip.
    /// </summary>
    int FindFingerIndexForInteractable(IInteractable interactable)
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] FindFingerIndexForInteractable() called");
        if (interactable == null) return -1;
        
        // Priority 1: Check if we have it stored from OnFingertipTrigger
        if (interactableToFingerIndex.ContainsKey(interactable))
        {
            int storedFinger = interactableToFingerIndex[interactable];
            // Verify the stored finger is still valid
            if (storedFinger >= 0 && storedFinger < fingertipTransforms.Length && fingertipTransforms[storedFinger] != null)
            {
                return storedFinger;
            }
            else
            {
                // Stored finger is invalid, remove it
                interactableToFingerIndex.Remove(interactable);
            }
        }
        
        // Priority 2: Find closest fingertip to the interactable (with reasonable distance threshold)
        // Try with 5cm threshold first, but if nothing found, try again without threshold
        int closest = FindClosestFingerToInteractable(interactable, maxDistance: 0.05f);
        if (closest >= 0)
        {
            return closest;
        }
        
        // No finger within 5cm, try without distance limit
        return FindClosestFingerToInteractable(interactable, maxDistance: float.MaxValue);
    }

    /// <summary>
    /// Finds the closest fingertip to an interactable within a maximum distance.
    /// Returns -1 if no finger is within range.
    /// </summary>
    int FindClosestFingerToInteractable(IInteractable interactable, float maxDistance = 0.05f)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] FindClosestFingerToInteractable() called (maxDistance={maxDistance})");
        if (interactable == null) return -1;
        
        if (interactable is MonoBehaviour mb && mb != null)
        {
            Vector3 interactablePos = mb.transform.position;
            float minDistance = float.MaxValue;
            int closestFinger = -1;
            
            // Check all fingers (thumb, index, middle, ring, pinky)
            for (int i = 0; i < fingertipTransforms.Length; i++)
            {
                if (fingertipTransforms[i] != null)
                {
                    float distance = Vector3.Distance(fingertipTransforms[i].position, interactablePos);
                    if (distance < minDistance && distance <= maxDistance)
                    {
                        minDistance = distance;
                        closestFinger = i;
                    }
                }
            }
            
            if (closestFinger >= 0 && showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Found closest finger to {interactable}: {fingerNames[closestFinger]} (distance: {minDistance:F3}m)");
            }
            
            return closestFinger;
        }
        
        return -1; // Unknown
    }

    #endregion

    #region RayInteractor Event Handlers (Pinch Interactions)

    void OnRayInteractableSelected(IInteractable interactable)
    {
        // if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] OnRayInteractableSelected() called");
        // THIS IS A PINCH INTERACTION - Send haptics to both thumb and index finger
        // Thumb = fingerIndex 0, Index = fingerIndex 1
        
        // Track this hand as active for keyboard interactions
        if (isLeftHand)
        {
            lastLeftHandInteractionTime = Time.time;
        }
        else
        {
            lastRightHandInteractionTime = Time.time;
        }
        
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} hand PINCH interaction started on {interactable} - sending haptics to thumb and index");
        }

        // Send haptic pulses to both thumb and index finger
        StartCoroutine(SendPinchHapticPulses());
    }

    void OnRayInteractableUnselected(IInteractable interactable)
    {
        // if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] OnRayInteractableUnselected() called");
        if (showDebugLogs)
        {
            Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} hand pinch interaction ended on {interactable}");
        }
    }

    /// <summary>
    /// Sends haptic pulses to thumb and index finger for pinch interactions.
    /// </summary>
    System.Collections.IEnumerator SendPinchHapticPulses()
    {
        // if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] SendPinchHapticPulses() called");
        if (bHapticsGlove == null)
        {
            // Try to reacquire bHapticsGlove instance if it's null
            bHapticsGlove = BhapticsPhysicsGlove.Instance;
            if (bHapticsGlove == null)
            {
                BhapticsPhysicsGlove[] allGloves = FindObjectsOfType<BhapticsPhysicsGlove>();
                if (allGloves != null && allGloves.Length > 0)
                {
                    bHapticsGlove = allGloves[0];
                }
                else
                {
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Cannot send pinch haptic: bHapticsGlove is null for {(isLeftHand ? "Left" : "Right")} hand. No gloves found in scene.");
                    }
                    yield break;
                }
            }
        }

        // Verify thumb and index finger transforms exist
        int thumbIndex = 0;
        int indexIndex = 1;
        
        if (fingertipTransforms[thumbIndex] == null || fingertipTransforms[indexIndex] == null)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Cannot send pinch haptic: thumb or index finger transform is null for {(isLeftHand ? "Left" : "Right")} hand.");
            }
            yield break;
        }

        // Send multiple pulses with small delays between them
        for (int i = 0; i < pinchPulseCount; i++)
        {
            if (bHapticsGlove != null)
            {
                try
                {
                    // Send haptic to thumb (fingerIndex 0)
                    bHapticsGlove.SendEnterHaptic(isLeftHand, thumbIndex);
                    
                    // Send haptic to index finger (fingerIndex 1)
                    bHapticsGlove.SendEnterHaptic(isLeftHand, indexIndex);
                    
                    if (showDebugLogs && i == 0)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} hand PINCH haptic pulse {i + 1}/{pinchPulseCount} sent to thumb and index (intensity: {pinchHapticIntensity:F1})");
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[BHapticsFingertipHaptics] Error sending pinch haptic pulse {i + 1}: {e.Message}");
                }
            }

            // Wait before next pulse (except for the last one)
            if (i < pinchPulseCount - 1)
            {
                yield return new WaitForSeconds(pinchPulseInterval);
            }
        }
    }

    #endregion

    /// <summary>
    /// Checks if a collision should trigger haptic feedback based on filtering settings.
    /// </summary>
    bool ShouldTriggerHaptic(Collider other)
    {
        // if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] ShouldTriggerHaptic() called");
        if (other == null)
            return false;

        // Exclude hand skeleton if enabled
        if (excludeHandSkeleton)
        {
            string objName = other.name;
            Transform parent = other.transform.parent;
            
            // Check the collider's GameObject and all parents
            while (parent != null)
            {
                objName += " " + parent.name;
                if (objName.Contains("Hand") || objName.Contains("StylizedCharacter") || 
                    objName.Contains("Skeleton") || objName.Contains("OVR") && !objName.Contains("UI"))
                {
                    // Allow OVR UI but exclude hand/character objects
                    if (!objName.Contains("UI") && (objName.Contains("Hand") || objName.Contains("StylizedCharacter")))
                    {
                        return false;
                    }
                }
                parent = parent.parent;
            }
            
            // Direct check on the collider's GameObject
            // Only exclude if it's clearly a hand bone (not UI with "Hand" in name)
            string lowerName = other.name.ToLower();
            if ((lowerName.Contains("hand") && !lowerName.Contains("ui")) || 
                lowerName.Contains("stylizedcharacter") ||
                lowerName.Contains("skeleton") || 
                lowerName.Contains("xrhand_") ||
                lowerName.StartsWith("b_l_") || lowerName.StartsWith("b_r_") ||
                (lowerName.Contains("l_thumb") && !lowerName.Contains("ui")) ||
                (lowerName.Contains("l_index") && !lowerName.Contains("ui")) ||
                (lowerName.Contains("r_thumb") && !lowerName.Contains("ui")) ||
                (lowerName.Contains("r_index") && !lowerName.Contains("ui")))
            {
                // Double-check: make sure this isn't a UI element (some UI might have "hand" in name)
                if (!other.GetComponent<Canvas>() && 
                    !other.GetComponent<GraphicRaycaster>() &&
                    !other.GetComponent<Selectable>() &&
                    !other.GetComponent<Graphic>())
                {
                    return false;
                }
            }
        }

        // If only triggering on UI is enabled, check if it's a UI element
        if (onlyTriggerOnUI)
        {
            // Check layer mask
            int otherLayer = other.gameObject.layer;
            if ((allowedLayers.value & (1 << otherLayer)) == 0)
            {
                // Not in allowed layers, but might still be UI if it has UI components
                if (!checkForUIComponents)
                {
                    return false;
                }
            }

            // Check for UI components if enabled
            if (checkForUIComponents)
            {
                // Check if this GameObject or any parent has UI components
                Transform checkTransform = other.transform;
                bool foundUIComponent = false;

                while (checkTransform != null && !foundUIComponent)
                {
                    // Check for common UI components
                    if (checkTransform.GetComponent<Canvas>() != null ||
                        checkTransform.GetComponent<GraphicRaycaster>() != null ||
                        checkTransform.GetComponent<UnityEngine.EventSystems.EventSystem>() != null ||
                        checkTransform.GetComponent<Selectable>() != null ||
                        checkTransform.GetComponent<Graphic>() != null ||
                        checkTransform.GetComponent<CanvasRenderer>() != null)
                    {
                        foundUIComponent = true;
                        break;
                    }

                    checkTransform = checkTransform.parent;
                }

                // Also check layer as fallback
                if (!foundUIComponent && (allowedLayers.value & (1 << otherLayer)) == 0)
                {
                    return false;
                }
            }
            else
            {
                // Only check layer mask
                if ((allowedLayers.value & (1 << otherLayer)) == 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Sends a small initial haptic pulse when a finger first touches a UI surface.
    /// This is a one-time pulse that occurs only on initial contact.
    /// </summary>
    void SendInitialTouchHapticPulse(int fingerIndex)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] SendInitialTouchHapticPulse({fingerIndex}) called");
        if (bHapticsGlove == null)
        {
            // Try to reacquire bHapticsGlove instance if it's null
            bHapticsGlove = BhapticsPhysicsGlove.Instance;
            if (bHapticsGlove == null)
            {
                BhapticsPhysicsGlove[] allGloves = FindObjectsOfType<BhapticsPhysicsGlove>();
                if (allGloves != null && allGloves.Length > 0)
                {
                    bHapticsGlove = allGloves[0];
                }
                else
                {
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Cannot send initial touch haptic: bHapticsGlove is null for {(isLeftHand ? "Left" : "Right")} hand {fingerNames[fingerIndex]}. No gloves found in scene.");
                    }
                    return;
                }
            }
        }

        if (fingerIndex < 0 || fingerIndex >= fingertipTransforms.Length)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Invalid finger index: {fingerIndex} for {(isLeftHand ? "Left" : "Right")} hand");
            }
            return;
        }

        if (fingertipTransforms[fingerIndex] == null)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Fingertip transform is null for finger {fingerIndex} ({fingerNames[fingerIndex]}) on {(isLeftHand ? "Left" : "Right")} hand");
            }
            return;
        }

        // Map finger index to bHaptics finger index (0=Thumb, 1=Index, 2=Middle, 3=Ring, 4=Pinky)
        int bHapticsFingerIndex = fingerIndex;

        // Send a small initial touch haptic pulse (uses base intensity, no velocity scaling)
        try
        {
            if (bHapticsGlove == null)
            {
                if (showDebugLogs)
                {
                    Debug.LogWarning($"[BHapticsFingertipHaptics] bHapticsGlove became null right before sending initial touch haptic to {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]}");
                }
                return;
            }
            
            // Send haptic pulse with base intensity (small pulse for initial touch)
            bHapticsGlove.SendEnterHaptic(isLeftHand, bHapticsFingerIndex);
            
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} initial touch haptic pulse sent (intensity: {contactHapticIntensity:F1}, base intensity)");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[BHapticsFingertipHaptics] Error sending initial touch haptic to {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]}: {e.Message}. isLeftHand={isLeftHand}, fingerIndex={bHapticsFingerIndex}, bHapticsGlove={(bHapticsGlove != null ? "Found" : "NULL")}");
        }
    }

    /// <summary>
    /// Sends a minor contact haptic pulse to a finger that is touching UI.
    /// Called periodically while the finger is in contact with UI and moving.
    /// BUTTON PRESS HAPTICS TAKE PRIORITY - this will not send if a button was just pressed.
    /// </summary>
    void SendContactHapticPulse(int fingerIndex)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] SendContactHapticPulse({fingerIndex}) called");
        if (bHapticsGlove == null)
        {
            // Try to reacquire bHapticsGlove instance if it's null (same logic as ContactHapticPulseLoop)
            bHapticsGlove = BhapticsPhysicsGlove.Instance;
            if (bHapticsGlove == null)
            {
                // Try to find any BhapticsPhysicsGlove in the scene
                BhapticsPhysicsGlove[] allGloves = FindObjectsOfType<BhapticsPhysicsGlove>();
                if (allGloves != null && allGloves.Length > 0)
                {
                    bHapticsGlove = allGloves[0];
                    if (showDebugLogs)
                    {
                        Debug.Log($"[BHapticsFingertipHaptics] Reacquired bHapticsGlove via FindObjectsOfType for {(isLeftHand ? "Left" : "Right")} hand in SendContactHapticPulse");
                    }
                }
                else
                {
                    if (showDebugLogs)
                    {
                        Debug.LogWarning($"[BHapticsFingertipHaptics] Cannot send contact haptic: bHapticsGlove is null for {(isLeftHand ? "Left" : "Right")} hand {fingerNames[fingerIndex]}. No gloves found in scene.");
                    }
                    return;
                }
            }
            else if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Reacquired bHapticsGlove singleton instance for {(isLeftHand ? "Left" : "Right")} hand in SendContactHapticPulse");
            }
        }

        if (fingerIndex < 0 || fingerIndex >= fingertipTransforms.Length)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Invalid finger index: {fingerIndex} for {(isLeftHand ? "Left" : "Right")} hand");
            }
            return;
        }

        if (fingertipTransforms[fingerIndex] == null)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning($"[BHapticsFingertipHaptics] Fingertip transform is null for finger {fingerIndex} ({fingerNames[fingerIndex]}) on {(isLeftHand ? "Left" : "Right")} hand");
            }
            return;
        }

        // PRIORITY CHECK: Don't send contact haptic if a button press just happened on this finger
        // This ensures button press haptics always take priority
        // Check if lastButtonPressTime was initialized (not 0) before checking cooldown
        // If lastButtonPressTime is 0, it means no button press has happened yet, so allow contact haptics
        // Also check if finger is currently in fingersTouchingUI (it gets removed during button press)
        if (!fingersTouchingUI.Contains(fingerIndex))
        {
            // Finger was temporarily removed for button press - don't send contact haptics
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Skipping contact haptic for {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} - finger temporarily removed for button press");
            }
            return;
        }

        if (lastButtonPressTime[fingerIndex] > 0 && 
            Time.time - lastButtonPressTime[fingerIndex] < buttonPressContactCooldown)
        {
            if (showDebugLogs)
            {
                Debug.Log($"[BHapticsFingertipHaptics] Skipping contact haptic for {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} - button press haptic has priority (cooldown: {buttonPressContactCooldown - (Time.time - lastButtonPressTime[fingerIndex]):F2}s remaining)");
            }
            return;
        }

        // Map finger index to bHaptics finger index (0=Thumb, 1=Index, 2=Middle, 3=Ring, 4=Pinky)
        int bHapticsFingerIndex = fingerIndex;

        // Get velocity-based intensity for this finger
        float intensity = GetVelocityBasedIntensity(fingerIndex);
        float normalizedVelocity = enableVelocityBasedHaptics ? GetNormalizedVelocity(fingerIndex) : 0f;

        // Send minor contact haptic pulse (using velocity-based intensity)
        // Note: bHaptics SendEnterHaptic doesn't directly take intensity, but we can send a brief pulse
        // The intensity is controlled by the bHaptics glove settings or SDK
        // For velocity-based feedback, we can send multiple pulses or adjust timing
        try
        {
            // Double-check bHapticsGlove is still valid before sending
            if (bHapticsGlove == null)
            {
                if (showDebugLogs)
                {
                    Debug.LogWarning($"[BHapticsFingertipHaptics] bHapticsGlove became null right before sending contact haptic to {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]}");
                }
                return;
            }
            
            // Send haptic pulse
            // If velocity is high, we could send multiple pulses for stronger effect
            // For now, we'll send a single pulse but the intensity calculation accounts for velocity
            bHapticsGlove.SendEnterHaptic(isLeftHand, bHapticsFingerIndex);
            
            // Optionally send additional pulses at high velocity for stronger feedback
            if (enableVelocityBasedHaptics && normalizedVelocity > 0.7f)
            {
                // Send a second pulse shortly after for high-speed feedback
                StartCoroutine(SendDelayedHapticPulse(bHapticsFingerIndex, 0.02f));
            }
            
            if (showDebugLogs)
            {
                float velocity = enableVelocityBasedHaptics ? smoothedFingerVelocities[fingerIndex] : 0f;
                Debug.Log($"[BHapticsFingertipHaptics] {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]} contact haptic pulse sent (intensity: {intensity:F1}, base: {contactHapticIntensity:F1}, velocity: {velocity:F3} m/s, normalized: {normalizedVelocity:F2}, glove: {(bHapticsGlove != null ? "OK" : "NULL")})");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[BHapticsFingertipHaptics] Error sending contact haptic to {(isLeftHand ? "Left" : "Right")} {fingerNames[fingerIndex]}: {e.Message}. isLeftHand={isLeftHand}, fingerIndex={bHapticsFingerIndex}, bHapticsGlove={(bHapticsGlove != null ? "Found" : "NULL")}");
        }
    }

    /// <summary>
    /// Sends a delayed haptic pulse for high-velocity feedback.
    /// </summary>
    System.Collections.IEnumerator SendDelayedHapticPulse(int bHapticsFingerIndex, float delay)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] SendDelayedHapticPulse({bHapticsFingerIndex}, {delay}) called");
        yield return new WaitForSeconds(delay);
        
        if (bHapticsGlove != null)
        {
            try
            {
                bHapticsGlove.SendEnterHaptic(isLeftHand, bHapticsFingerIndex);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BHapticsFingertipHaptics] Error sending delayed haptic pulse: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Sends a strong button press haptic pulse to a finger that presses a button.
    /// Called when a button is actually pressed (via PokeInteractor).
    /// </summary>
    /// <summary>
    /// DEPRECATED: Use SendButtonPressHapticPulses instead for multiple pulses.
    /// Kept for backwards compatibility.
    /// </summary>
    void SendButtonPressHapticPulse(int fingerIndex)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] SendButtonPressHapticPulse({fingerIndex}) called");
        // This is now handled by SendButtonPressHapticPulses coroutine
        // Keeping this method for backwards compatibility but it's not used anymore
        StartCoroutine(SendButtonPressHapticPulses(fingerIndex));
    }

    /// <summary>
    /// Get the transform for a specific finger (useful for external scripts).
    /// </summary>
    public Transform GetFingertipTransform(int fingerIndex)
    {
        if (trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] GetFingertipTransform({fingerIndex}) called");
        if (fingerIndex >= 0 && fingerIndex < fingertipTransforms.Length)
        {
            return fingertipTransforms[fingerIndex];
        }
        return null;
    }

    /// <summary>
    /// Get all fingertip transforms.
    /// </summary>
    public Transform[] GetAllFingertipTransforms()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] GetAllFingertipTransforms() called");
        return fingertipTransforms;
    }

    #if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (trackFunctionCalls) Debug.Log("[FUNCTION_TRACKER] OnDrawGizmosSelected() called");
        if (fingertipTransforms == null)
            return;

        Gizmos.color = isLeftHand ? Color.cyan : Color.magenta;
        for (int i = 0; i < fingertipTransforms.Length; i++)
        {
            if (fingertipTransforms[i] != null)
            {
                Gizmos.DrawWireSphere(fingertipTransforms[i].position, colliderRadius);
            }
        }
    }
    #endif
}

/// <summary>
/// Component attached to each fingertip to handle collision events.
/// </summary>
public class FingertipColliderHandler : MonoBehaviour
{
    private BHapticsFingertipHaptics parent;
    private int fingerIndex;
    private string fingerName;
    private bool isLeftHand;

    public void Initialize(BHapticsFingertipHaptics parent, int fingerIndex, string fingerName, bool isLeftHand)
    {
        if (parent != null && parent.trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] FingertipColliderHandler.Initialize({fingerIndex}) called");
        this.parent = parent;
        this.fingerIndex = fingerIndex;
        this.fingerName = fingerName;
        this.isLeftHand = isLeftHand;
    }

    void OnTriggerEnter(Collider other)
    {
        // if (parent != null && parent.trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] FingertipColliderHandler.OnTriggerEnter({fingerIndex}) called");
        // Track which interactable is being touched by which finger
        if (parent != null)
        {
            // Debug logging to verify trigger events are firing
            if (parent.showDebugLogs)
            {
                Debug.Log($"[FingertipColliderHandler] {(isLeftHand ? "Left" : "Right")} {fingerName} (fingerIndex={fingerIndex}) OnTriggerEnter called with: {other.name} (layer: {other.gameObject.layer})");
            }
            parent.OnFingertipTrigger(fingerIndex, other);
        }
        else
        {
            Debug.LogWarning($"[FingertipColliderHandler] {(isLeftHand ? "Left" : "Right")} {fingerName} OnTriggerEnter called but parent is NULL!");
        }
    }

    void OnTriggerExit(Collider other)
    {
        // if (parent != null && parent.trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] FingertipColliderHandler.OnTriggerExit({fingerIndex}) called");
        // Clean up when finger leaves the interactable
        if (parent != null)
        {
            parent.OnFingertipTriggerExit(fingerIndex, other);
        }
    }

    void OnTriggerStay(Collider other)
    {
        // if (parent != null && parent.trackFunctionCalls) Debug.Log($"[FUNCTION_TRACKER] FingertipColliderHandler.OnTriggerStay({fingerIndex}) called");
        // Optionally trigger continuous haptics while touching UI
        // Uncomment if you want continuous feedback while touching
        // if (parent != null && Time.frameCount % 10 == 0) // Throttle to every 10 frames
        // {
        //     parent.OnFingertipTrigger(fingerIndex, other);
        // }
    }
}


