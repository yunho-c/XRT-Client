using System;
using UnityEngine;
using Unity.WebRTC;

/// <summary>
/// Receives Unitree G1 arm motor temperature and torque data over the 'motor_stats'
/// WebRTC data channel (server-initiated, JSON). Broadcasts a C# event each time a
/// valid packet arrives so other components can react without polling.
///
/// JSON format (from xr_robot_teleop_client.send_motor_stats):
/// {
///   "type": "motor_stats",
///   "timestamp": 1234567890.0,
///   "left_arm_temps":   [t0..t6],   // °C, motors 15-21
///   "right_arm_temps":  [t0..t6],   // °C, motors 22-28
///   "left_arm_torques": [tau0..tau6],// Nm
///   "right_arm_torques":[tau0..tau6] // Nm
/// }
/// </summary>
public class MotorStatsReceiver : MonoBehaviour
{
    [Tooltip("Auto-detected if null.")]
    public WebRTCController webRTCController;

    [Header("Debug")]
    public bool showDebugLogs = false;

    // ── Unitree G1 limits ────────────────────────────────────────────────────
    // Source: Unitree G1 technical specification.
    // 80 °C is the motor protection (hard cutoff) temperature.
    public const float MOTOR_MAX_TEMP_C   = 80f;
    public const float OVERHEAT_WARN_FRAC = 0.90f; // vignette appears
    public const float OVERHEAT_FLASH_FRAC= 0.95f; // vignette begins flashing

    // Per-joint peak torque (Nm) for the 7 arm DOF:
    //   [shoulder_pitch, shoulder_roll, elbow_pitch, elbow_roll,
    //    wrist_pitch, wrist_roll, wrist_yaw]
    public static readonly float[] ARM_MAX_TORQUE_NM = { 25f, 25f, 25f, 10f, 5f, 5f, 5f };

    public const int ARM_MOTOR_COUNT = 7;

    // ── Data ─────────────────────────────────────────────────────────────────
    [Serializable]
    public class ArmStats
    {
        public float[] temps   = new float[ARM_MOTOR_COUNT];
        public float[] torques = new float[ARM_MOTOR_COUNT];
    }

    public ArmStats LeftArm  { get; private set; } = new ArmStats();
    public ArmStats RightArm { get; private set; } = new ArmStats();

    public event Action<ArmStats, ArmStats> OnMotorStatsUpdated;

    // ── Internal ─────────────────────────────────────────────────────────────
    private RTCDataChannel _channel;
    private bool _channelRegistered;

    [Serializable]
    private class MotorStatsMessage
    {
        public string type;
        public double timestamp;
        public float[] left_arm_temps;
        public float[] right_arm_temps;
        public float[] left_arm_torques;
        public float[] right_arm_torques;
    }

    void Start()
    {
        if (webRTCController == null)
            webRTCController = FindObjectOfType<WebRTCController>();
    }

    /// <summary>Called by WebRTCController when the server-initiated 'motor_stats' channel opens.</summary>
    public void OnMotorStatsChannelReceived(RTCDataChannel channel)
    {
        if (_channelRegistered) return;
        _channel = channel;
        _channelRegistered = true;

        channel.OnMessage = bytes =>
        {
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(bytes);
                var msg = JsonUtility.FromJson<MotorStatsMessage>(json);
                if (msg == null || msg.type != "motor_stats") return;

                CopyArray(msg.left_arm_temps,   LeftArm.temps);
                CopyArray(msg.right_arm_temps,  RightArm.temps);
                CopyArray(msg.left_arm_torques, LeftArm.torques);
                CopyArray(msg.right_arm_torques,RightArm.torques);

                // Fire on main thread via UnityMainThreadDispatcher
                var left  = LeftArm;
                var right = RightArm;
                UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    OnMotorStatsUpdated?.Invoke(left, right));

                if (showDebugLogs)
                    Debug.Log($"[MotorStatsReceiver] ts={msg.timestamp:F2} " +
                              $"L_maxT={MaxOf(LeftArm.temps):F1}°C R_maxT={MaxOf(RightArm.temps):F1}°C");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MotorStatsReceiver] Parse error: {e.Message}");
            }
        };

        if (showDebugLogs)
            Debug.Log("[MotorStatsReceiver] motor_stats channel connected.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    static void CopyArray(float[] src, float[] dst)
    {
        if (src == null) return;
        int n = Mathf.Min(src.Length, dst.Length);
        Array.Copy(src, dst, n);
    }

    public static float MaxOf(float[] arr)
    {
        float m = float.MinValue;
        foreach (float v in arr) if (v > m) m = v;
        return m;
    }

    /// <summary>
    /// Normalised overheat severity [0,1] for a temperature value.
    /// Returns 0 below 90 % of max, 1 at 100 % (80 °C).
    /// </summary>
    public static float OverheatSeverity(float tempC)
    {
        float warnTemp = OVERHEAT_WARN_FRAC  * MOTOR_MAX_TEMP_C; // 72 °C
        float maxTemp  = MOTOR_MAX_TEMP_C;                        // 80 °C
        return Mathf.Clamp01((tempC - warnTemp) / (maxTemp - warnTemp));
    }

    /// <summary>Normalised torque load [0,1] for one motor index.</summary>
    public static float TorqueSeverity(float torqueNm, int motorIndex)
    {
        int idx = Mathf.Clamp(motorIndex, 0, ARM_MAX_TORQUE_NM.Length - 1);
        float maxTau = ARM_MAX_TORQUE_NM[idx];
        return Mathf.Clamp01(Mathf.Abs(torqueNm) / maxTau);
    }
}
