using UnityEngine;

/// <summary>
/// Overlays translucent cylinders on the StylizedCharacter model to visualise
/// Unitree G1 arm motor torques in real time.
///
/// Each cylinder is:
///   • Positioned and oriented to match a specific arm motor (assign via Inspector).
///   • Coloured on a green → red gradient as |torque| approaches the per-joint max.
///   • Semi-transparent (alpha driven by torqueDisplayAlpha).
///
/// Motor order per arm (7 joints):
///   0 shoulder_pitch | 1 shoulder_roll | 2 elbow_pitch | 3 elbow_roll
///   4 wrist_pitch    | 5 wrist_roll    | 6 wrist_yaw
///
/// Typical workflow:
///   1. Add this component to the StylizedCharacter (or any persistent GO).
///   2. Expand "Left Arm Motor Transforms" and drag the 7 bones from the
///      character's hierarchy that best correspond to each joint.
///   3. Optionally fine-tune cylinderScale for each arm.
/// </summary>
[RequireComponent(typeof(MotorStatsReceiver))]
public class TorqueVisualization : MonoBehaviour
{
    [Header("Character Reference")]
    [Tooltip("Root of the StylizedCharacter. Used only for fallback position if motor transforms are null.")]
    public Transform characterRoot;

    [Header("Left Arm Motor Transforms (shoulder_pitch → wrist_yaw)")]
    [Tooltip("Drag the 7 left-arm bone transforms from the StylizedCharacter hierarchy. " +
             "Order: shoulder_pitch, shoulder_roll, elbow_pitch, elbow_roll, wrist_pitch, wrist_roll, wrist_yaw.")]
    public Transform[] leftMotorTransforms  = new Transform[MotorStatsReceiver.ARM_MOTOR_COUNT];

    [Header("Right Arm Motor Transforms (shoulder_pitch → wrist_yaw)")]
    public Transform[] rightMotorTransforms = new Transform[MotorStatsReceiver.ARM_MOTOR_COUNT];

    [Header("Cylinder Appearance")]
    [Tooltip("World-space size of each cylinder (x/z = radius, y = half-height).")]
    public Vector3 cylinderScale = new Vector3(0.04f, 0.06f, 0.04f);

    [Tooltip("Maximum alpha of the cylinders when a motor is at 100 % torque. " +
             "At zero torque the cylinder is fully transparent.")]
    [Range(0f, 1f)]
    public float maxTorqueAlpha = 0.50f;

    [Tooltip("Base grey tint of the cylinder at zero torque (alpha will always be 0 at zero torque).")]
    public Color zeroTorqueColor = new Color(0.6f, 0.6f, 0.6f, 1f);

    [Tooltip("Colour used at maximum torque.")]
    public Color maxTorqueColor = new Color(1f, 0f, 0f, 1f);

    // ── Internals ─────────────────────────────────────────────────────────────
    private MotorStatsReceiver _receiver;
    private GameObject[]  _leftCylinders, _rightCylinders;
    private Material[]    _leftMats,      _rightMats;

    private float[] _leftTorques  = new float[MotorStatsReceiver.ARM_MOTOR_COUNT];
    private float[] _rightTorques = new float[MotorStatsReceiver.ARM_MOTOR_COUNT];

    void Awake()
    {
        _receiver = GetComponent<MotorStatsReceiver>();
    }

    void Start()
    {
        _leftCylinders  = BuildCylinders("Left",  leftMotorTransforms,  out _leftMats);
        _rightCylinders = BuildCylinders("Right", rightMotorTransforms, out _rightMats);
        _receiver.OnMotorStatsUpdated += HandleStats;
    }

    void OnDestroy()
    {
        if (_receiver != null)
            _receiver.OnMotorStatsUpdated -= HandleStats;

        DestroyAll(_leftCylinders);
        DestroyAll(_rightCylinders);
        DestroyMaterials(_leftMats);
        DestroyMaterials(_rightMats);
    }

    // ── Cylinder factory ──────────────────────────────────────────────────────
    GameObject[] BuildCylinders(string side, Transform[] motorTrs, out Material[] mats)
    {
        int n    = MotorStatsReceiver.ARM_MOTOR_COUNT;
        mats     = new Material[n];
        var gos  = new GameObject[n];

        for (int i = 0; i < n; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = $"TorqueCylinder_{side}_{i}";
            Destroy(go.GetComponent<Collider>()); // no physics needed

            // Parent to motor transform (or character root as fallback)
            Transform parent = (motorTrs != null && i < motorTrs.Length && motorTrs[i] != null)
                ? motorTrs[i]
                : (characterRoot != null ? characterRoot : transform);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale    = cylinderScale;

            // Transparent material
            var rend = go.GetComponent<Renderer>();
            var mat  = new Material(GetTransparentShader());
            mat.name = $"TorqueMat_{side}_{i}";
            SetMatColor(mat, zeroTorqueColor, 0f); // start fully transparent
            rend.material = mat;
            mats[i] = mat;
            gos[i]  = go;
        }
        return gos;
    }

    static Shader GetTransparentShader()
    {
        // URP Lit transparent
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null)
            sh = Shader.Find("Standard");
        return sh;
    }

    static void SetMatColor(Material mat, Color baseColor, float alpha)
    {
        Color c = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);

        if (mat.shader.name.Contains("Universal"))
        {
            // URP Lit — transparent surface
            mat.SetFloat("_Surface", 1f);                         // 0=Opaque, 1=Transparent
            mat.SetFloat("_Blend",   0f);                         // Alpha blend
            mat.SetFloat("_ZWrite",  0f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetColor("_BaseColor", c);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = 3000;
        }
        else
        {
            // Standard shader fallback
            mat.SetFloat("_Mode", 3f);    // Transparent
            mat.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite",    0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.color      = c;
            mat.renderQueue= 3000;
        }
    }

    // ── Stats handler ─────────────────────────────────────────────────────────
    void HandleStats(MotorStatsReceiver.ArmStats left, MotorStatsReceiver.ArmStats right)
    {
        System.Array.Copy(left.torques,  _leftTorques,  MotorStatsReceiver.ARM_MOTOR_COUNT);
        System.Array.Copy(right.torques, _rightTorques, MotorStatsReceiver.ARM_MOTOR_COUNT);
    }

    void Update()
    {
        UpdateCylinders(_leftCylinders,  _leftMats,  _leftTorques);
        UpdateCylinders(_rightCylinders, _rightMats, _rightTorques);
    }

    void UpdateCylinders(GameObject[] cylinders, Material[] mats, float[] torques)
    {
        for (int i = 0; i < MotorStatsReceiver.ARM_MOTOR_COUNT; i++)
        {
            if (mats[i] == null) continue;
            float severity = MotorStatsReceiver.TorqueSeverity(torques[i], i);
            // Colour: grey at zero → red at max
            Color col   = Color.Lerp(zeroTorqueColor, maxTorqueColor, severity);
            // Alpha: 0 at zero torque → maxTorqueAlpha at full torque
            float alpha = severity * maxTorqueAlpha;
            SetMatColor(mats[i], col, alpha);
        }
    }

    // ── Cleanup helpers ───────────────────────────────────────────────────────
    static void DestroyAll(GameObject[] gos)
    {
        if (gos == null) return;
        foreach (var g in gos) if (g != null) Destroy(g);
    }

    static void DestroyMaterials(Material[] mats)
    {
        if (mats == null) return;
        foreach (var m in mats) if (m != null) Destroy(m);
    }
}
