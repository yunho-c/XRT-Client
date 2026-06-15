using UnityEngine;

/// <summary>
/// Techno-synth fingertip audio — an alternative to vibrotactile haptic gloves.
///
/// Each finger maps to a distinct note across two octaves (left hand lower,
/// right hand higher), mirroring a piano keyboard layout.
///
///   Left  — Palm:G2  Little:C3  Ring:E3  Middle:G3  Index:B3  Thumb:D4
///   Right — Thumb:E4  Index:G4  Middle:B4  Ring:D5  Little:F5  Palm:C5
///
/// SOUND DESIGN — Daft Punk / smooth electronic pad character
///   Three slightly detuned bandlimited sawtooth oscillators (supersaw) pass through
///   a gentle 2nd-order resonant low-pass filter.
///
///   Envelope: linear fade-in → single exponential release to silence.
///   No separate decay/sustain phase — the note simply rises then gracefully fades.
///
///   Filter: opens with the attack, closes slowly after it (can outlast the amplitude
///   fade, keeping the tone warm and bright as it fades — characteristic pad quality).
///
/// TRIGGER BEHAVIOUR
///   One-shot note on any threshold crossing in either direction (press in or release
///   out). Reaching ForceLevel.None (finger fully lifted) is silent.
///   Re-triggering restarts the note from the beginning (overwrite per finger).
///
/// SETUP
///   1. Add to same GameObject as WebRTCHapticReceiver.
///   2. Enable "Use Audio Haptics" on WebRTCHapticReceiver and assign this component.
///   3. Drag avatar fingertip bone transforms into Left/Right arrays (thumb → palm order).
/// </summary>
public class FingertipAudioHaptics : MonoBehaviour
{
    // ── Note frequencies (Hz) ─────────────────────────────────────────────────
    // Slot order: [0]=thumb [1]=index [2]=middle [3]=ring [4]=little [5]=palm
    //
    // Left hand — pinky(low) → thumb(high), mirrors piano left hand.
    private static readonly float[] LEFT_FREQ =
    {
        293.66f,  // [0] Thumb  → D4
        246.94f,  // [1] Index  → B3
        196.00f,  // [2] Middle → G3
        164.81f,  // [3] Ring   → E3
        130.81f,  // [4] Little → C3
         98.00f,  // [5] Palm   → G2
    };

    // Right hand — thumb(low) → pinky(high), mirrors piano right hand.
    private static readonly float[] RIGHT_FREQ =
    {
        329.63f,  // [0] Thumb  → E4
        392.00f,  // [1] Index  → G4
        493.88f,  // [2] Middle → B4
        587.33f,  // [3] Ring   → D5
        698.46f,  // [4] Little → F5
        523.25f,  // [5] Palm   → C5
    };

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Fingertip Transforms — Left Hand  (thumb 0 … palm 5)")]
    [Tooltip("Avatar left-hand fingertip bones: thumb, index, middle, ring, little, palm.")]
    public Transform[] leftFingertipTransforms  = new Transform[6];

    [Header("Fingertip Transforms — Right Hand  (thumb 0 … palm 5)")]
    [Tooltip("Avatar right-hand fingertip bones: thumb, index, middle, ring, little, palm.")]
    public Transform[] rightFingertipTransforms = new Transform[6];

    [Header("Volume")]
    [Tooltip("Master volume for all triggered notes.")]
    [Range(0f, 1f)] public float masterVolume = 0.80f;

    [Header("Octave by Force Level")]
    [Tooltip("Octave offset from the finger's base note when each level is reached.\n" +
             "0 = base note, 1 = one octave up (2× pitch), -1 = one octave down (0.5× pitch).")]
    public int lightOctave   = -1;
    public int mediumOctave  =  0;
    public int highOctave    =  1;
    public int maximumOctave =  2;

    [Header("Amplitude Envelope  (Play Mode restart required after changes)")]
    [Tooltip("Linear fade-in time. Longer = softer entry, shorter = more percussive.")]
    [Range(0.01f, 0.3f)]  public float attackSeconds = 0.08f;
    [Tooltip("Time for the note to fade from peak to silence after the attack.")]
    [Range(0.2f, 4.0f)]   public float releaseDecay  = 1.6f;

    [Header("Filter  (Play Mode restart required after changes)")]
    [Tooltip("Low-pass cutoff at the note start — higher = warmer, more open sound.")]
    [Range(200f, 4000f)]   public float filterMin      = 1200f;
    [Tooltip("Low-pass cutoff at the brightest point of the note.")]
    [Range(1000f, 18000f)] public float filterMax      = 4800f;
    [Tooltip("Time for the filter to open from filterMin to filterMax during the attack.")]
    [Range(0.01f, 0.4f)]   public float filterAttack   = 0.05f;
    [Tooltip("Time for the filter to close back toward filterMin after the attack peak.\n" +
             "Set longer than releaseDecay to keep the tone bright as it fades (pad character).")]
    [Range(0.2f, 6.0f)]    public float filterRelease  = 2.8f;
    [Tooltip("Resonance peak at the cutoff — keep low for clean Daft Punk tone.")]
    [Range(0f, 3f)]        public float resonance      = 0.20f;

    [Header("Oscillator")]
    [Tooltip("Frequency spread between the three supersaw oscillators. " +
             "Lower = tighter unison, higher = chorus-like width.")]
    [Range(0f, 0.025f)]    public float detuneAmount   = 0.005f;

    [Header("Force Thresholds  (keep in sync with WebRTCHapticReceiver)")]
    [Range(0f, 0.2f)] public float sensorFloor   = 0.05f;
    [Range(0f, 1f)]   public float lightCutoff   = 0.10f;
    [Range(0f, 1f)]   public float mediumCutoff  = 0.35f;
    [Range(0f, 1f)]   public float highCutoff    = 0.60f;
    [Range(0f, 1f)]   public float maximumCutoff = 0.85f;
    [Tooltip("Dead zone below each threshold: force must drop this far below a level's entry point before exiting it. " +
             "Prevents rapid re-triggering from sensor noise near a boundary.")]
    [Range(0f, 0.15f)] public float hysteresisAmount = 0.05f;

    [Header("3D Spatialization")]
    public float audioMinDistance = 0.05f;
    public float audioMaxDistance = 2.0f;

    [Header("Input Smoothing")]
    [Tooltip("Exponential moving-average weight on the previous sample (0 = no smoothing, 0.95 = heavy).\n" +
             "Raise this if sensor noise causes spurious note triggers at low force.")]
    [Range(0f, 0.99f)] public float smoothingFactor = 0.80f;

    [Header("Debounce")]
    [Tooltip("Minimum seconds between notes on the same finger. Prevents rapid multi-level firing " +
             "when sensor values cross several thresholds in quick succession. " +
             "Set shorter than the expected time between deliberate level changes.")]
    [Range(0.05f, 2.0f)] public float minRetriggerSeconds = 0.20f;

    [Header("Debug")]
    public bool showDebugLogs = false;

    // ── Constants ─────────────────────────────────────────────────────────────
    private const int   FINGER_COUNT  = 6;
    private const int   SAMPLE_RATE   = 44100;
    private const int   MAX_HARMONICS = 36;
    private const float TWO_PI        = 6.28318530f;

    // Fast sine via lookup table (shared across all instances)
    private const int   SIN_SIZE  = 8192;
    private const float SIN_SCALE = SIN_SIZE / TWO_PI;

    private static readonly float[] _sinTable = BuildSinTable();
    private static float[] BuildSinTable()
    {
        var t = new float[SIN_SIZE];
        for (int i = 0; i < SIN_SIZE; i++)
            t[i] = Mathf.Sin(TWO_PI * i / SIN_SIZE);
        return t;
    }

    private static float FastSin(float phase)
    {
        int idx = (int)(phase * SIN_SCALE) & (SIN_SIZE - 1);
        return idx >= 0 ? _sinTable[idx] : _sinTable[idx + SIN_SIZE];
    }

    // ── Runtime state ─────────────────────────────────────────────────────────
    private AudioSource[] _leftSrc,  _rightSrc;
    private AudioClip[]   _leftClip, _rightClip;

    private enum ForceLevel { None, Light, Medium, High, Maximum }
    private readonly ForceLevel[] _leftLevel  = new ForceLevel[FINGER_COUNT];
    private readonly ForceLevel[] _rightLevel = new ForceLevel[FINGER_COUNT];
    private readonly float[]      _leftSmoothed      = new float[FINGER_COUNT];
    private readonly float[]      _rightSmoothed     = new float[FINGER_COUNT];
    private readonly float[]      _leftLastTrigger   = new float[FINGER_COUNT];
    private readonly float[]      _rightLastTrigger  = new float[FINGER_COUNT];

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void Start()
    {
        _leftClip  = GenerateClips(LEFT_FREQ);
        _rightClip = GenerateClips(RIGHT_FREQ);
        _leftSrc   = BuildSources("L", leftFingertipTransforms,  _leftClip);
        _rightSrc  = BuildSources("R", rightFingertipTransforms, _rightClip);
    }

    void OnDestroy()
    {
        if (_leftClip  != null) foreach (var c in _leftClip)  { if (c) Destroy(c); }
        if (_rightClip != null) foreach (var c in _rightClip) { if (c) Destroy(c); }
    }

    // ── Public API ────────────────────────────────────────────────────────────
    public void SendHapticsForHand(bool isLeft, float[] fingerValues)
    {
        AudioSource[] srcs        = isLeft ? _leftSrc          : _rightSrc;
        ForceLevel[]  levels      = isLeft ? _leftLevel        : _rightLevel;
        float[]       smoothed    = isLeft ? _leftSmoothed     : _rightSmoothed;
        float[]       lastTrigger = isLeft ? _leftLastTrigger  : _rightLastTrigger;

        for (int i = 0; i < FINGER_COUNT; i++)
        {
            if (srcs == null || srcs[i] == null) continue;

            float raw        = Mathf.Clamp01(i < fingerValues.Length ? fingerValues[i] : 0f);
            smoothed[i]      = smoothingFactor * smoothed[i] + (1f - smoothingFactor) * raw;
            ForceLevel level = ClassifyLevel(smoothed[i], levels[i]);

            // Fire on any level change except to None (lifting is silent),
            // gated by per-finger cooldown to prevent rapid multi-level bursts.
            float now = Time.unscaledTime;
            if (level != levels[i] && level != ForceLevel.None &&
                now - lastTrigger[i] >= minRetriggerSeconds)
            {
                srcs[i].volume = masterVolume;
                srcs[i].pitch  = Mathf.Pow(2f, LevelToOctave(level));
                srcs[i].Stop();
                srcs[i].Play();
                lastTrigger[i] = now;

                if (showDebugLogs)
                    Debug.Log($"[AudioHaptics] {(isLeft?"L":"R")}[{i}] {levels[i]}→{level} " +
                              $"smoothed={smoothed[i]:F3} octave={LevelToOctave(level)} pitch={srcs[i].pitch:F2}");
            }

            levels[i] = level;
        }
    }

    // ── Clip generation ───────────────────────────────────────────────────────
    AudioClip[] GenerateClips(float[] freqs)
    {
        float clipDuration = attackSeconds + releaseDecay + 0.1f;
        int   frames       = Mathf.RoundToInt(SAMPLE_RATE * clipDuration);

        // Amplitude: linear attack → single exponential release to silence
        float releaseRate = -Mathf.Log(0.001f) / Mathf.Max(releaseDecay, 0.0001f);

        // Filter envelope constants
        // Opens with the attack (rises quickly to 1.0 by filterAttack)
        float filterOpenRate  = -Mathf.Log(0.01f) / Mathf.Max(filterAttack,  0.0001f);
        // Closes after the attack (slowly falls back toward filterMin)
        float filterCloseRate = -Mathf.Log(0.02f) / Mathf.Max(filterRelease, 0.0001f);

        var clips = new AudioClip[freqs.Length];

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            float f  = freqs[fi];
            float f2 = f * (1f + detuneAmount);
            float f3 = f * (1f - detuneAmount * 0.72f); // asymmetric detune for richer beating

            int maxH1 = Mathf.Min(Mathf.FloorToInt(SAMPLE_RATE * 0.5f / f),  MAX_HARMONICS);
            int maxH2 = Mathf.Min(Mathf.FloorToInt(SAMPLE_RATE * 0.5f / f2), MAX_HARMONICS);
            int maxH3 = Mathf.Min(Mathf.FloorToInt(SAMPLE_RATE * 0.5f / f3), MAX_HARMONICS);

            // Phase accumulators (avoids floating-point drift from large t*n*f products)
            var ph1 = new float[maxH1]; var pi1 = new float[maxH1];
            var ph2 = new float[maxH2]; var pi2 = new float[maxH2];
            var ph3 = new float[maxH3]; var pi3 = new float[maxH3];

            for (int n = 1; n <= maxH1; n++) pi1[n-1] = TWO_PI * n * f  / SAMPLE_RATE;
            for (int n = 1; n <= maxH2; n++) pi2[n-1] = TWO_PI * n * f2 / SAMPLE_RATE;
            for (int n = 1; n <= maxH3; n++) pi3[n-1] = TWO_PI * n * f3 / SAMPLE_RATE;

            var data = new float[frames];

            for (int s = 0; s < frames; s++)
            {
                float t = s / (float)SAMPLE_RATE;

                // ── Amplitude envelope: rise → fall ─────────────────────────
                float amp = t < attackSeconds
                    ? t / attackSeconds                                        // linear attack
                    : Mathf.Exp(-releaseRate * (t - attackSeconds));           // exponential release

                // ── Filter envelope ─────────────────────────────────────────
                // Rises quickly to peak brightness, then slowly closes back.
                // filterRelease > releaseDecay → filter stays warm/bright as amp fades.
                float fOpen  = 1f - Mathf.Exp(-filterOpenRate * t);           // 0→1 during attack
                float fClose = t > attackSeconds
                    ? Mathf.Exp(-filterCloseRate * (t - attackSeconds))        // 1→0 after attack
                    : 1f;
                float cutoffHz = Mathf.Lerp(filterMin, filterMax, Mathf.Clamp01(fOpen * fClose));

                // ── Three oscillators ───────────────────────────────────────
                float osc1 = SynthOsc(ph1, pi1, maxH1, f,  cutoffHz);
                float osc2 = SynthOsc(ph2, pi2, maxH2, f2, cutoffHz);
                float osc3 = SynthOsc(ph3, pi3, maxH3, f3, cutoffHz);

                data[s] = (0.50f * osc1 + 0.30f * osc2 + 0.20f * osc3) * amp;
            }

            // Normalise to [-1, 1]
            float peak = 0f;
            foreach (float v in data)
                if (Mathf.Abs(v) > peak) peak = Mathf.Abs(v);
            if (peak > 0f)
                for (int s = 0; s < frames; s++) data[s] /= peak;

            var clip = AudioClip.Create($"DPSynth_{f:F0}Hz", frames, 1, SAMPLE_RATE, false);
            clip.SetData(data, 0);
            clips[fi] = clip;
        }

        return clips;
    }

    // Bandlimited sawtooth through a gentle 2nd-order resonant LP filter.
    // 2nd-order (12 dB/oct) is softer and less nasal than the previous 3rd-order.
    float SynthOsc(float[] phases, float[] phaseIncs, int maxH, float freq, float cutoffHz)
    {
        float sum = 0f;
        for (int n = 0; n < maxH; n++)
        {
            int   harmNum = n + 1;
            float x       = harmNum * freq / cutoffHz; // 1.0 = at cutoff, >1 = above

            // 2nd-order LP: gentler -12 dB/oct rolloff (softer, less "ringy")
            float lpW  = 1f / (1f + x * x);

            // Subtle resonance peak near the cutoff (triangular, cheap)
            float dist = Mathf.Abs(x - 1f);
            float resW = resonance * Mathf.Max(0f, 1f - dist * 4f);

            sum += FastSin(phases[n]) * Mathf.Clamp01(lpW + resW) / harmNum;

            phases[n] += phaseIncs[n];
            if (phases[n] >= TWO_PI) phases[n] -= TWO_PI;
        }

        return (2f / Mathf.PI) * sum;
    }

    // ── AudioSource setup ─────────────────────────────────────────────────────
    AudioSource[] BuildSources(string side, Transform[] trs, AudioClip[] clips)
    {
        var      srcs = new AudioSource[FINGER_COUNT];
        string[] lbl  = { "Thumb", "Index", "Middle", "Ring", "Little", "Palm" };

        for (int i = 0; i < FINGER_COUNT; i++)
        {
            Transform parent = (trs != null && i < trs.Length && trs[i] != null)
                ? trs[i] : transform;

            var go = new GameObject($"AudioHaptic_{side}_{lbl[i]}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            var src = go.AddComponent<AudioSource>();
            src.clip        = clips[i];
            src.loop        = false;
            src.spatialBlend= 1.0f;
            src.minDistance = audioMinDistance;
            src.maxDistance = audioMaxDistance;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.volume      = 0f;
            src.playOnAwake = false;
            srcs[i] = src;
        }

        return srcs;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    // Stateful classification with hysteresis: upward crossings are immediate,
    // downward crossings require force to drop hysteresisAmount below the entry
    // threshold to avoid rapid re-triggering from sensor noise at boundaries.
    ForceLevel ClassifyLevel(float force, ForceLevel current)
    {
        ForceLevel raw = ClassifyRaw(force);
        if ((int)raw > (int)current) return raw;    // going up: always immediate
        if (raw == current) return current;
        // going down: only leave if below (entry threshold - dead zone)
        if (force < EntryThreshold(current) - hysteresisAmount) return raw;
        return current;                             // in hysteresis zone: stay
    }

    ForceLevel ClassifyRaw(float force)
    {
        if (force < sensorFloor || force < lightCutoff) return ForceLevel.None;
        if (force < mediumCutoff)  return ForceLevel.Light;
        if (force < highCutoff)    return ForceLevel.Medium;
        if (force < maximumCutoff) return ForceLevel.High;
        return ForceLevel.Maximum;
    }

    float EntryThreshold(ForceLevel level)
    {
        if (level == ForceLevel.Light)   return Mathf.Max(lightCutoff, sensorFloor);
        if (level == ForceLevel.Medium)  return mediumCutoff;
        if (level == ForceLevel.High)    return highCutoff;
        if (level == ForceLevel.Maximum) return maximumCutoff;
        return 0f;
    }

    int LevelToOctave(ForceLevel level)
    {
        if (level == ForceLevel.Light)   return lightOctave;
        if (level == ForceLevel.Medium)  return mediumOctave;
        if (level == ForceLevel.High)    return highOctave;
        if (level == ForceLevel.Maximum) return maximumOctave;
        return 0;
    }
}
