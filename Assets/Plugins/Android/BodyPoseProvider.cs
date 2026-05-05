using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Assertions;
using Meta.XR.Movement.Retargeting;
using static Meta.XR.Movement.MSDKUtility;

public class BodyPoseProvider : MonoBehaviour
{
    #region Data Structures
    /// <summary>
    /// Represents the captured state of a single bone at a specific moment.
    /// </summary>
    [Serializable]
    public struct BoneData
    {
        public OVRSkeleton.BoneId id;
        public Vector3 position;
        public Quaternion rotation;
    }

    /// <summary>
    /// Represents a complete body pose, including a timestamp and a list of all bone data.
    /// This object is reused to prevent garbage collection.
    /// </summary>
    [Serializable]
    public class PoseData
    {
        public float timestamp;
        public List<BoneData> bones = new List<BoneData>();
    }
    #endregion

    #region Public Fields
    // [Tooltip("The MetaSourceDataProvider component that provides the raw tracking data.")]
    // public MetaSourceDataProvider sourceDataProvider;
    [Tooltip("Switch to enable bone position representation as red balls.")]
    public bool enableBallRep = true;
    [Tooltip("In Unity Editor, call native body query each frame. Disable to avoid editor-native crashes while testing non-device flows.")]
    public bool enableNativeBodyQueryInEditor = false;
    [Tooltip("When true, emits the last known/default pose if Meta body tracking is invalid.")]
    public bool emitPoseWhenInvalid = true;
    [Tooltip("Seconds between repeated invalid-pose warnings.")]
    public float invalidPoseWarningInterval = 2.0f;
    #endregion

    #region Public Properties
    /// <summary>
    /// Holds the most recently captured pose data.
    /// </summary>
    public PoseData CurrentPoseData { get; private set; }
    #endregion

    #region Private Fields
    private ISourceDataProvider sourceDataProvider;
    private bool _isInitialized = false;
    private List<GameObject> ballRepObjects = new List<GameObject>();
    private float _nextInvalidPoseWarningTime = 0f;
    #endregion

    #region Events
    /// <summary>
    /// Event that is invoked every Update with the latest tracking data.
    /// Other scripts can subscribe to this to receive pose updates.
    /// </summary>
    public event Action<PoseData> OnPoseUpdated;
    #endregion

    #region Unity Methods
    void Awake()
    {
        Debug.Log("BodyPoseProvider: Awake()");
        sourceDataProvider = gameObject.GetComponent<ISourceDataProvider>();
        Assert.IsNotNull(sourceDataProvider, "BodyPoseProvider: ISourceDataProvider not found on this GameObject.");
    }

    void Start()
    {
        Debug.Log("BodyPoseProvider: Start()");
        if (sourceDataProvider == null)
        {
            Debug.LogError("BodyPoseProvider: sourceDataProvider is null. Disabling script.");
            this.enabled = false;
            return;
        }
    }

    void Update()
    {
        // 1. Handle initialization if not done yet.
        if (!_isInitialized)
        {
            InitializePoseData();
            if (!_isInitialized) { return; } // Exit if initialization fails
        }

        // Editor-safe mode: avoid native Meta body polling in Editor play mode.
        // This keeps downstream systems alive with fallback pose frames.
#if UNITY_EDITOR
        if (!enableNativeBodyQueryInEditor)
        {
            if (emitPoseWhenInvalid && CurrentPoseData != null)
            {
                CurrentPoseData.timestamp = Time.time;
                OnPoseUpdated?.Invoke(CurrentPoseData);
            }
            return;
        }
#endif

        // 2. FETCH the skeleton pose first. This is the most important change.
        var skeletonPose = sourceDataProvider.GetSkeletonPose();

        // 3. NOW check for validity. The flag has been updated by the call above.
        if (!sourceDataProvider.IsPoseValid())
        {
            if (Time.time >= _nextInvalidPoseWarningTime)
            {
                Debug.LogWarning("BodyPoseProvider: Body pose is invalid. Waiting for valid data.");
                _nextInvalidPoseWarningTime = Time.time + Mathf.Max(0.25f, invalidPoseWarningInterval);
            }

            if (emitPoseWhenInvalid && CurrentPoseData != null)
            {
                CurrentPoseData.timestamp = Time.time;
                OnPoseUpdated?.Invoke(CurrentPoseData);
            }
            return; // It's now safe to return, as you've already attempted the fetch.
        }

        // 4. If valid, update your internal data structure.
        // The UpdatePoseData method needs to be modified to accept the fetched pose.
        UpdatePoseData(skeletonPose);

        // 5. Invoke the event with the fresh data.
        OnPoseUpdated?.Invoke(CurrentPoseData);
    }

    #endregion

    #region Private Methods
    private void InitializePoseData()
    {
        Debug.Log("BodyPoseProvider: Initializing PoseData");
        CurrentPoseData = new PoseData();

        // Determine the skeleton type from the source data provider
        // Assuming MetaSourceDataProvider is the concrete type
        MetaSourceDataProvider metaSource = sourceDataProvider as MetaSourceDataProvider;
        if (metaSource == null)
        {
            Debug.LogError("BodyPoseProvider: Source data provider is not MetaSourceDataProvider. Cannot determine skeleton type.");
            return;
        }

        // Use the provided skeleton type to get the correct bone range
        OVRSkeleton.BoneId startBoneId;
        OVRSkeleton.BoneId endBoneId;

        // This logic needs to be robust. We'll assume Body or FullBody for now.
        // You might need to refine this based on the actual OVRPlugin.BodyJointSet values.
        if (metaSource.ProvidedSkeletonType == OVRPlugin.BodyJointSet.FullBody)
        {
            startBoneId = OVRSkeleton.BoneId.FullBody_Start;
            endBoneId = OVRSkeleton.BoneId.FullBody_End;
            Debug.Log("BodyPoseProvider: Detected FullBody skeleton type.");
        }
        else // Default to Body (UpperBody) if not FullBody
        {
            startBoneId = OVRSkeleton.BoneId.Body_Start;
            endBoneId = OVRSkeleton.BoneId.Body_End;
            Debug.Log("BodyPoseProvider: Detected Body (UpperBody) skeleton type.");
        }

        // For ball representation, hide the mesh of the body if it exists
        if (enableBallRep)
        { 
            Renderer[] bodyRenderer = GetComponentsInChildren<Renderer>();
            foreach (Renderer renderer in bodyRenderer)
            {
                if (renderer != null)
                {
                    renderer.enabled = false; // Disable the mesh renderer
                }
                else
                {
                    Debug.LogError("BodyPoseProvider: Renderer not found on body object. Cannot disable mesh.");
                }
            }
        }

        // Populate bones list with all possible bone IDs for the detected skeleton type
        for (int i = (int)startBoneId; i < (int)endBoneId; i++)
        {
            CurrentPoseData.bones.Add(new BoneData { id = (OVRSkeleton.BoneId)i });
            if (enableBallRep)
            {
                GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Renderer ballRenderer = ball.GetComponent<Renderer>();
                if (ballRenderer != null)
                {
                    ballRenderer.material.color = Color.red; // Set color for visibility
                }
                else
                {
                    Debug.LogWarning("BodyPoseProvider: Renderer not found on the ball object. Using default material.");
                }
                ball.transform.localScale = Vector3.one * 0.02f; // Adjust size as needed
                ball.name = $"Bone_{((OVRSkeleton.BoneId)i).ToString()}";
                ballRepObjects.Add(ball);
            }
        }

        if (CurrentPoseData.bones.Count > 0)
        {
            _isInitialized = true;
            Debug.Log($"BodyPoseProvider: PoseData initialized with {CurrentPoseData.bones.Count} bones.");
        }
        else
        {
            Debug.LogError("BodyPoseProvider: Failed to initialize PoseData. No bones added.");
        }
    }

    private void UpdatePoseData(NativeArray<NativeTransform> skeletonPose)
    {
        CurrentPoseData.timestamp = Time.time;

        if (skeletonPose.IsCreated && skeletonPose.Length == CurrentPoseData.bones.Count)
        {
            for (int i = 0; i < skeletonPose.Length; i++)
            {
                // This is safe because BoneData is a struct, but it's inefficient.
                // A more optimal way would be to work with an array of structs directly.
                BoneData boneData = CurrentPoseData.bones[i];
                boneData.position = skeletonPose[i].Position;
                boneData.rotation = skeletonPose[i].Orientation;
                CurrentPoseData.bones[i] = boneData;
                if (enableBallRep && i < ballRepObjects.Count)
                {
                    ballRepObjects[i].transform.position = boneData.position;
                    ballRepObjects[i].transform.rotation = boneData.rotation;
                }
            }
        }
        else if (skeletonPose.IsCreated && skeletonPose.Length != CurrentPoseData.bones.Count)
        {
            Debug.LogWarning($"BodyPoseProvider: Mismatch in bone count. Expected {CurrentPoseData.bones.Count}, got {skeletonPose.Length}. Re-initializing.");
            _isInitialized = false; // Trigger re-initialization on the next frame.
        }
    }
    #endregion
}