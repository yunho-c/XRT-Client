using UnityEngine;

/// <summary>
/// Glass-armonica fingertip audio — an alternative to vibrotactile haptic gloves.
///
/// Each finger maps to a distinct note tuned in perfect-5th steps (~7 semitones),
/// giving a 2.3-octave span per hand with unmistakably different tones per finger.
///
///   Left  (low) — Palm:G2  Little:D3  Ring:A3  Middle:E4  Index:B4  Thumb:F#5
///   Right (high) — Palm:C#4  Thumb:G#4  Index:D#5  Middle:A#5  Ring:F6  Little:C7
///
/// SOUND DESIGN — Smooth, round, Quest-startup-like character
///   Nearly pure sine (fundamental + very subtle octave harmonic ≈ 0.02).
///   Envelope: smooth sine-rise → cosine-fall (a half-sine bell shape). Both
///   slopes are zero at the peak — no kink, no click. Quick and clean (~0.17 s).
///   Post-attack vibrato fades in gradually (~0.10 s) so the onset stays pure.
///   No sharp overtones, no exponential tail.
///
/// INTENSITY → PITCH + VOLUME (directional)
///   Pressing harder (upward): pitch rises per level (0, +1, +1, +2 octaves).
///   Releasing (downward): plays the REVERSED audio clip at a lower octave.
///     The reversed envelope swells from silence to peak then cuts — clearly
///     distinct from the forward ping and reinforces the "going down" feel.
///   Volume also scales with level for additional differentiation.
///
/// PALM HAPTICS
///   Palm (slot [5]) is fully supported. Assign palm bone transforms if desired;
///   otherwise the AudioSource falls back to this transform (still spatialized).
///
/// SPATIAL AUDIO MODES (useHeadSpatialAudio toggle)
///   OFF — current behaviour: each finger/palm sound is located AT the hand.
///   ON  — head-spatial: sounds are spread in an arc around the player's head —
///         thumbs almost in front, pinkies almost behind, palms low around shoulder
///         level, left hand on the left and right hand on the right. The arc follows
///         the player's head position + yaw (so the vertical drop stays world-down).
///   headHandSpatialBlend (0..1, used when ON) mixes the two: 0 = fully around the
///   head, 1 = fully at the hands; in between the sounds stay spaced around the head
///   but their position is pulled toward the real hand location.
///
/// SETUP
///   1. Add to same GameObject as WebRTCHapticReceiver.
///   2. Enable "Use Audio Haptics" and assign this component on WebRTCHapticReceiver.
///   3. Optionally assign fingertip + palm bone transforms (thumb[0] … palm[5]).
/// </summary>
public class FingertipAudioHaptics : MonoBehaviour
{
    // ── Note frequencies (Hz) — perfect-5th steps ────────────────────────────
    // Slot order: [0]=thumb [1]=index [2]=middle [3]=ring [4]=little [5]=palm

    // Left — P5 steps from D3 (little=low → thumb=high). Palm=G2 (P5 below D3).
    private static readonly float[] LEFT_FREQ =
    {
        739.99f,  // [0] Thumb  → F#5
        493.88f,  // [1] Index  → B4
        329.63f,  // [2] Middle → E4
        220.00f,  // [3] Ring   → A3
        146.83f,  // [4] Little → D3
         98.00f,  // [5] Palm   → G2  (deep body contact)
    };

    // Right — P5 steps from G#4 (thumb=low → little=high). Palm=C#4 (P5 below G#4).
    private static readonly float[] RIGHT_FREQ =
    {
        415.30f,  // [0] Thumb  → G#4
        622.25f,  // [1] Index  → D#5
        932.33f,  // [2] Middle → A#5
       1396.91f,  // [3] Ring   → F6
       2093.00f,  // [4] Little → C7  (bright crystal — glass armonica soprano)
        277.18f,  // [5] Palm   → C#4 (body contact, below finger range)
    };

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Fingertip Transforms — Left Hand  (thumb 0 … palm 5)")]
    [Tooltip("Avatar left-hand bones: thumb, index, middle, ring, little, palm. " +
             "Leave null to fall back to this transform (still spatialized).")]
    public Transform[] leftFingertipTransforms  = new Transform[6];

    [Header("Fingertip Transforms — Right Hand  (thumb 0 … palm 5)")]
    [Tooltip("Avatar right-hand bones: thumb, index, middle, ring, little, palm.")]
    public Transform[] rightFingertipTransforms = new Transform[6];

    [Header("Volume")]
    [Range(0f, 1f)] public float masterVolume = 0.80f;

    [Header("Volume by Force Level")]
    [Tooltip("Note loudness (fraction of masterVolume) at each level.")]
    [Range(0f, 1f)] public float lightVolume   = 0.35f;
    [Range(0f, 1f)] public float mediumVolume  = 0.60f;
    [Range(0f, 1f)] public float highVolume    = 0.82f;
    [Range(0f, 1f)] public float maximumVolume = 1.00f;

    [Header("Octave by Intensity — Pressing (upward level transitions)")]
    [Tooltip("Pitch offset in octaves when pressing INTO each level. " +
             "Positive = higher pitch (1.0 = one octave up, AudioSource.pitch × 2).")]
    [Range(-3f, 4f)] public float lightPressOctave   =  0f;
    [Range(-3f, 4f)] public float mediumPressOctave  =  1f;
    [Range(-3f, 4f)] public float highPressOctave    =  1f;
    [Range(-3f, 4f)] public float maximumPressOctave =  2f;

    [Header("Octave by Intensity — Releasing (downward, plays reversed clip)")]
    [Tooltip("Pitch offset in octaves when releasing INTO each level. " +
             "The audio plays REVERSED (swell-to-peak, cut-off). " +
             "Negative = lower than base note — reinforces the 'going down' feel.")]
    [Range(-4f, 3f)] public float lightRelOctave   = -1f;
    [Range(-4f, 3f)] public float mediumRelOctave  = -1f;
    [Range(-4f, 3f)] public float highRelOctave    =  0f;
    [Range(-4f, 3f)] public float maximumRelOctave =  0f;

    [Header("Amplitude Envelope  (Play Mode restart required after changes)")]
    [Tooltip("Sine-rise time to peak. Longer = softer/rounder onset.")]
    [Range(0.005f, 0.10f)] public float attackSeconds = 0.025f;
    [Tooltip("Cosine-fall time from peak to silence. Keep short for a snappy notification ping.")]
    [Range(0.02f, 0.22f)]  public float releaseDecay  = 0.10f;
    [Tooltip("Hard ceiling on total note length (attack + release). Notes never exceed this, " +
             "regardless of the values above — keeps every note a short notification tone.")]
    [Range(0.05f, 0.25f)]  public float maxNoteDuration = 0.25f;

    [Header("Glass Armonica Timbre  (Play Mode restart required after changes)")]
    [Tooltip("Vibrato oscillation rate (Hz).")]
    [Range(1f, 12f)]      public float vibratoRate   = 5.5f;
    [Tooltip("Vibrato depth as a fraction of fundamental frequency (~0.003 ≈ 5 cents).")]
    [Range(0f, 0.02f)]    public float vibratoDepth  = 0.003f;
    [Tooltip("Time after attack peak for vibrato to reach full depth (organic fade-in).")]
    [Range(0f, 0.4f)]     public float vibratoFadeIn = 0.10f;
    [Tooltip("2nd harmonic amplitude (octave above fundamental). Keep low for purity.")]
    [Range(0f, 0.20f)]    public float harmonic2Gain = 0.02f;

    [Header("Force Thresholds  (keep in sync with WebRTCHapticReceiver)")]
    [Range(0f, 0.2f)] public float sensorFloor   = 0.05f;
    [Range(0f, 1f)]   public float lightCutoff   = 0.10f;
    [Range(0f, 1f)]   public float mediumCutoff  = 0.35f;
    [Range(0f, 1f)]   public float highCutoff    = 0.60f;
    [Range(0f, 1f)]   public float maximumCutoff = 0.85f;
    [Tooltip("Hysteresis dead zone below each threshold to suppress noise at boundaries.")]
    [Range(0f, 0.15f)] public float hysteresisAmount = 0.05f;

    [Header("3D Spatialization (direction only — distance never changes volume)")]
    [Tooltip("Distance settings are kept for reference but do NOT attenuate volume: a flat " +
             "custom rolloff curve holds gain at 1.0 at every distance, so only force level " +
             "controls loudness. Direction (panning) is still fully spatialized.")]
    public float audioMinDistance = 0.05f;
    public float audioMaxDistance = 3.0f;

    [Header("Spatial Audio Mode")]
    [Tooltip("OFF = current implementation: each finger/palm sound is located AT the hand.\n" +
             "ON = new head-spatial mode: sounds are spread in an arc around the player's head — " +
             "thumbs almost in front, pinkies almost behind, palms low near the feet — with the " +
             "left hand's sounds on the left and the right hand's on the right.")]
    public bool useHeadSpatialAudio = false;

    [Tooltip("HEAD / HAND SPATIAL BLEND (only used when head-spatial mode is ON).\n" +
             "0 = sounds sit fully in the spread arc around the head.\n" +
             "1 = sounds sit fully at the player's actual hands (same as the current mode).\n" +
             "In between, the sounds stay spaced out around the head but their position is pulled " +
             "toward the real hand location, so head layout and hand tracking are mixed.")]
    [Range(0f, 1f)] public float headHandSpatialBlend = 0.0f;

    [Header("Head Reference (head-spatial mode)")]
    [Tooltip("Player head transform the spatial arc is built around. Auto-detects the main camera " +
             "(CenterEyeAnchor) if left null.")]
    public Transform headTransform;

    [Header("Head-Spatial Layout  (when head-spatial mode is ON)")]
    [Tooltip("Radius of the finger-sound arc around the head (metres).")]
    [Range(0.2f, 1.5f)] public float headSpreadRadius = 0.6f;
    [Tooltip("Azimuth of the THUMB sound from straight ahead (degrees). Small = near the front.")]
    [Range(0f, 90f)]    public float thumbAzimuthDeg = 35f;
    [Tooltip("Azimuth of the PINKY (little) sound from straight ahead (degrees). " +
             "Near 180 = almost directly behind the player.")]
    [Range(90f, 180f)]  public float pinkyAzimuthDeg = 160f;
    [Tooltip("Vertical offset of the finger arc relative to head height (metres, + = up).")]
    [Range(-0.5f, 0.5f)] public float fingerHeightOffset = 0.0f;
    [Tooltip("How far BELOW the head the PALM sounds sit (metres). ~0.35 ≈ shoulder level.")]
    [Range(0f, 2.0f)]   public float palmDropDistance = 0.35f;
    [Tooltip("Sideways offset of the palm sounds from head centre (metres). Sign follows the hand.")]
    [Range(0f, 0.8f)]   public float palmSideOffset = 0.3f;
    [Tooltip("Forward offset of the palm sounds (metres, + = in front of the player).")]
    [Range(-0.5f, 0.5f)] public float palmForwardOffset = 0.15f;

    [Header("Input Smoothing")]
    [Tooltip("Exponential moving-average weight on the prior sample (0 = none, 0.95 = heavy). " +
             "Together with the hysteresis dead zone this suppresses noise-triggered re-fires, " +
             "so every genuine level change can fire immediately and overwrite the previous note.")]
    [Range(0f, 0.99f)] public float smoothingFactor = 0.80f;

    [Header("Debug")]
    public bool showDebugLogs = false;

    // ── Constants ─────────────────────────────────────────────────────────────
    private const int   FINGER_COUNT = 6;
    private const int   SAMPLE_RATE  = 44100;
    private const float TWO_PI       = 6.28318530f;

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
        return _sinTable[idx >= 0 ? idx : idx + SIN_SIZE];
    }

    // ── Runtime state ─────────────────────────────────────────────────────────
    private AudioSource[] _leftSrc,  _rightSrc;

    // Forward clips for press (upward) transitions.
    private AudioClip[] _leftClipFwd,  _rightClipFwd;
    // Reversed clips for release (downward) transitions — sample data flipped.
    private AudioClip[] _leftClipRev,  _rightClipRev;

    private enum ForceLevel { None, Light, Medium, High, Maximum }
    private readonly ForceLevel[] _leftLevel     = new ForceLevel[FINGER_COUNT];
    private readonly ForceLevel[] _rightLevel    = new ForceLevel[FINGER_COUNT];
    private readonly float[]      _leftSmoothed  = new float[FINGER_COUNT];
    private readonly float[]      _rightSmoothed = new float[FINGER_COUNT];

    private Transform _cachedHead; // resolved head transform (CenterEyeAnchor / main camera)

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void Start()
    {
        _leftClipFwd  = GenerateClips(LEFT_FREQ);
        _leftClipRev  = ReverseClips(_leftClipFwd);
        _rightClipFwd = GenerateClips(RIGHT_FREQ);
        _rightClipRev = ReverseClips(_rightClipFwd);

        // AudioSources are owned by this component (positioned each frame in LateUpdate),
        // so the same sources serve both hand-located and head-spatial modes.
        _leftSrc  = BuildSources("L", _leftClipFwd);
        _rightSrc = BuildSources("R", _rightClipFwd);

        // Place them once so the very first note isn't emitted from the origin.
        UpdateSourcePositions();
    }

    // Keep each finger/palm AudioSource at the correct world position for the active mode.
    // LateUpdate so it runs after hand/head tracking has written this frame's transforms.
    void LateUpdate()
    {
        UpdateSourcePositions();
    }

    void OnDestroy()
    {
        DestroyClips(_leftClipFwd);  DestroyClips(_leftClipRev);
        DestroyClips(_rightClipFwd); DestroyClips(_rightClipRev);
    }

    // ── Public API ────────────────────────────────────────────────────────────
    public void SendHapticsForHand(bool isLeft, float[] fingerValues)
    {
        AudioSource[] srcs     = isLeft ? _leftSrc      : _rightSrc;
        ForceLevel[]  levels   = isLeft ? _leftLevel    : _rightLevel;
        float[]       smoothed = isLeft ? _leftSmoothed : _rightSmoothed;
        AudioClip[]   fwdClips = isLeft ? _leftClipFwd  : _rightClipFwd;
        AudioClip[]   revClips = isLeft ? _leftClipRev  : _rightClipRev;

        for (int i = 0; i < FINGER_COUNT; i++)
        {
            if (srcs == null || srcs[i] == null) continue;

            float raw   = Mathf.Clamp01(i < fingerValues.Length ? fingerValues[i] : 0f);
            smoothed[i] = smoothingFactor * smoothed[i] + (1f - smoothingFactor) * raw;
            ForceLevel lv = ClassifyLevel(smoothed[i], levels[i]);

            // Any genuine level change fires immediately and overwrites the previous note —
            // in BOTH directions. A fast press never sticks on the lower note (the higher one
            // takes over the instant its threshold is crossed); a fast release never sticks on
            // the higher reversed note (the lower one takes over the instant force drops past
            // it). Stop() before Play() on the shared per-finger AudioSource guarantees the
            // overwrite. Hysteresis + input smoothing suppress noise chatter, so no debounce
            // lockout is needed in either direction.
            if (lv != levels[i] && lv != ForceLevel.None)
            {
                bool isUpward = (int)lv > (int)levels[i];

                // Downward transitions use the reversed clip — swell-to-peak then cut-off.
                srcs[i].clip   = isUpward ? fwdClips[i] : revClips[i];
                srcs[i].volume = masterVolume * LevelToVolumeFraction(lv);
                srcs[i].pitch  = Mathf.Pow(2f,
                    isUpward ? LevelToPressOctave(lv) : LevelToReleaseOctave(lv));
                srcs[i].Stop();
                srcs[i].Play();

                if (showDebugLogs)
                    Debug.Log($"[AudioHaptics] {(isLeft ? "L" : "R")}[{i}] " +
                              $"{levels[i]}→{lv} {(isUpward ? "▲fwd" : "▼rev")} " +
                              $"pitch={srcs[i].pitch:F2} vol={srcs[i].volume:F2}");
            }

            levels[i] = lv;
        }
    }

    // ── Clip generation ───────────────────────────────────────────────────────
    // Envelope: sine-rise (0→peak) then cosine-fall (peak→0).
    // Both slopes are zero at the peak — no kink, no click.
    // Vibrato fades in gradually after the peak so the onset stays pure.
    // Nearly pure sine (fundamental + very subtle octave harmonic).
    AudioClip[] GenerateClips(float[] freqs)
    {
        // Hard-cap total note length. If attack+release exceeds the ceiling, scale both
        // down proportionally so the bell shape is preserved but the note stays short.
        float attack  = attackSeconds;
        float release = releaseDecay;
        float sum     = attack + release;
        if (sum > maxNoteDuration && sum > 0f)
        {
            float k = maxNoteDuration / sum;
            attack  *= k;
            release *= k;
        }
        float totalDur     = attack + release;
        int   frames       = Mathf.RoundToInt(SAMPLE_RATE * totalDur);
        float vibratoInc   = TWO_PI * vibratoRate / SAMPLE_RATE;
        float vibFadeRate  = Mathf.Max(vibratoFadeIn, 0.001f);

        var clips = new AudioClip[freqs.Length];

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            float f    = freqs[fi];
            float inc1 = TWO_PI * f       / SAMPLE_RATE; // fundamental
            float inc2 = TWO_PI * f * 2f  / SAMPLE_RATE; // octave harmonic

            float ph1 = 0f, ph2 = 0f, phV = 0f;
            var   data = new float[frames];

            for (int s = 0; s < frames; s++)
            {
                float t = s / (float)SAMPLE_RATE;

                // ── Bell envelope (sine-rise / cosine-fall) ─────────────────
                float amp;
                if (t < attack)
                {
                    // Sine rise: smooth 0 → 1, zero slope at both ends.
                    amp = Mathf.Sin(Mathf.PI * 0.5f * t / attack);
                }
                else if (t < totalDur)
                {
                    // Cosine fall: smooth 1 → 0, zero slope at both ends.
                    float u = (t - attack) / release;
                    amp = Mathf.Cos(Mathf.PI * 0.5f * u);
                }
                else
                {
                    amp = 0f;
                }

                // ── Vibrato — gradual fade-in after attack peak ─────────────
                float postAttack = Mathf.Max(0f, t - attack);
                float vibEnv     = Mathf.Clamp01(postAttack / vibFadeRate);
                float vibMod     = 1f + vibratoDepth * vibEnv * FastSin(phV);
                phV += vibratoInc;
                if (phV >= TWO_PI) phV -= TWO_PI;

                // ── Oscillator — nearly pure sine ───────────────────────────
                data[s] = (FastSin(ph1) + harmonic2Gain * FastSin(ph2)) * amp;

                ph1 += inc1 * vibMod; if (ph1 >= TWO_PI) ph1 -= TWO_PI;
                ph2 += inc2 * vibMod; if (ph2 >= TWO_PI) ph2 -= TWO_PI;
            }

            // Normalise to [-1, 1]
            float peak = 0f;
            foreach (float v in data)
                if (Mathf.Abs(v) > peak) peak = Mathf.Abs(v);
            if (peak > 0f)
                for (int s = 0; s < frames; s++) data[s] /= peak;

            var clip = AudioClip.Create($"GlassArmonica_{f:F0}Hz", frames, 1, SAMPLE_RATE, false);
            clip.SetData(data, 0);
            clips[fi] = clip;
        }

        return clips;
    }

    // Build reversed clips by flipping the sample array of each forward clip.
    // Reversed envelope: the cosine-fall appears first (gradual swell), the
    // sine-rise appears last (short sharp peak then cut) — a clearly distinct
    // "downward" character compared to the forward ping.
    AudioClip[] ReverseClips(AudioClip[] fwd)
    {
        var rev = new AudioClip[fwd.Length];
        for (int i = 0; i < fwd.Length; i++)
        {
            if (fwd[i] == null) continue;
            int     n    = fwd[i].samples;
            var     data = new float[n];
            fwd[i].GetData(data, 0);
            System.Array.Reverse(data);
            var clip = AudioClip.Create(fwd[i].name + "_Rev", n, 1, SAMPLE_RATE, false);
            clip.SetData(data, 0);
            rev[i] = clip;
        }
        return rev;
    }

    // ── AudioSource setup ─────────────────────────────────────────────────────
    // Sources are parented to this component (NOT the hand bones). Their world position is
    // driven each frame by UpdateSourcePositions() so one set of sources can be hand-located,
    // head-spatial, or any blend of the two.
    AudioSource[] BuildSources(string side, AudioClip[] clips)
    {
        var      srcs = new AudioSource[FINGER_COUNT];
        string[] lbl  = { "Thumb", "Index", "Middle", "Ring", "Little", "Palm" };

        for (int i = 0; i < FINGER_COUNT; i++)
        {
            var go = new GameObject($"AudioHaptic_{side}_{lbl[i]}");
            go.transform.SetParent(transform, false);

            var src = go.AddComponent<AudioSource>();
            src.clip         = clips[i];
            src.loop         = false;
            src.spatialBlend = 1.0f;   // full 3D so direction / panning is preserved
            src.dopplerLevel = 0f;     // no pitch shift from hand or head motion
            src.minDistance  = audioMinDistance;
            src.maxDistance  = audioMaxDistance;
            // Direction-only spatialization: a FLAT rolloff curve (constant 1.0) means the
            // distance to the source never attenuates the sound — extending your hand keeps the
            // volume fixed. Loudness comes solely from force level (set per trigger), while 3D
            // panning still localizes each finger/palm by direction.
            src.rolloffMode  = AudioRolloffMode.Custom;
            src.SetCustomCurve(AudioSourceCurveType.CustomRolloff,
                               AnimationCurve.Constant(0f, 1f, 1f));
            src.volume       = 0f;
            src.playOnAwake  = false;
            srcs[i] = src;
        }

        return srcs;
    }

    // ── Spatial positioning ─────────────────────────────────────────────────────
    void UpdateSourcePositions()
    {
        // effectiveBlend: 1 = fully at hands (current mode). When head-spatial is on, the
        // blend slider mixes from the head arc (0) toward the hands (1).
        float blend = useHeadSpatialAudio ? Mathf.Clamp01(headHandSpatialBlend) : 1f;
        PositionHand(true,  leftFingertipTransforms,  _leftSrc,  blend);
        PositionHand(false, rightFingertipTransforms, _rightSrc, blend);
    }

    void PositionHand(bool isLeft, Transform[] trs, AudioSource[] srcs, float blend)
    {
        if (srcs == null) return;

        // Only resolve the head frame when the head layout actually contributes (blend < 1).
        bool       useHead = blend < 0.999f;
        Vector3    headPos = Vector3.zero;
        Quaternion headYaw = Quaternion.identity;
        if (useHead)
        {
            Transform head = ResolveHead();
            if (head != null)
            {
                headPos = head.position;
                // Yaw only: keeps "down toward feet" world-vertical and front/back tied to
                // the direction the player faces, without tilting when they look up/down.
                headYaw = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
            }
            else
            {
                useHead = false; // no head available — fall back to hand positions
            }
        }

        for (int i = 0; i < FINGER_COUNT; i++)
        {
            if (srcs[i] == null) continue;

            Transform handTr = (trs != null && i < trs.Length && trs[i] != null) ? trs[i] : null;
            Vector3   handPos = handTr != null ? handTr.position : transform.position;

            Vector3 pos;
            if (!useHead)
            {
                pos = handPos; // current behaviour: sound at the hand
            }
            else
            {
                Vector3 spreadWorld = headPos + headYaw * GetHeadSpreadLocalOffset(i, isLeft);
                pos = Vector3.Lerp(spreadWorld, handPos, blend);
            }

            srcs[i].transform.position = pos;
        }
    }

    // Local offset (head-yaw space: x=right, y=up, z=forward) for the head-spatial arc.
    //   Fingers 0..4 (thumb..pinky) sweep from near-front to near-behind along an arc.
    //   Palm (5) sits lower than the arc (~shoulder level), offset to the hand's side.
    Vector3 GetHeadSpreadLocalOffset(int finger, bool isLeft)
    {
        float side = isLeft ? -1f : 1f; // left hand on the left, right hand on the right

        if (finger == 5) // palm — dropped below the head, ~shoulder level
            return new Vector3(side * palmSideOffset, -palmDropDistance, palmForwardOffset);

        // frac: 0 = thumb (front), 1 = little/pinky (behind)
        float frac = finger / 4f;
        float az   = Mathf.Lerp(thumbAzimuthDeg, pinkyAzimuthDeg, frac) * Mathf.Deg2Rad;

        float x = Mathf.Sin(az) * headSpreadRadius * side;
        float z = Mathf.Cos(az) * headSpreadRadius; // +front (thumb) → −back (pinky)
        float y = fingerHeightOffset;
        return new Vector3(x, y, z);
    }

    Transform ResolveHead()
    {
        if (headTransform != null) return headTransform;
        if (_cachedHead != null)   return _cachedHead;
        if (Camera.main != null)   _cachedHead = Camera.main.transform;
        return _cachedHead;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    void DestroyClips(AudioClip[] clips)
    {
        if (clips == null) return;
        foreach (var c in clips) { if (c) Destroy(c); }
    }

    ForceLevel ClassifyLevel(float force, ForceLevel current)
    {
        ForceLevel raw = ClassifyRaw(force);
        if ((int)raw > (int)current) return raw;
        if (raw == current) return current;
        if (force < EntryThreshold(current) - hysteresisAmount) return raw;
        return current;
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

    float LevelToVolumeFraction(ForceLevel level)
    {
        if (level == ForceLevel.Light)   return lightVolume;
        if (level == ForceLevel.Medium)  return mediumVolume;
        if (level == ForceLevel.High)    return highVolume;
        if (level == ForceLevel.Maximum) return maximumVolume;
        return 0f;
    }

    float LevelToPressOctave(ForceLevel level)
    {
        if (level == ForceLevel.Light)   return lightPressOctave;
        if (level == ForceLevel.Medium)  return mediumPressOctave;
        if (level == ForceLevel.High)    return highPressOctave;
        if (level == ForceLevel.Maximum) return maximumPressOctave;
        return 0f;
    }

    float LevelToReleaseOctave(ForceLevel level)
    {
        if (level == ForceLevel.Light)   return lightRelOctave;
        if (level == ForceLevel.Medium)  return mediumRelOctave;
        if (level == ForceLevel.High)    return highRelOctave;
        if (level == ForceLevel.Maximum) return maximumRelOctave;
        return 0f;
    }
}
