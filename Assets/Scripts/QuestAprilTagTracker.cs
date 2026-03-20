using UnityEngine;
using AprilTag;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

public class QuestAprilTagTracker : MonoBehaviour
{
    [Header("Tracking Settings")]
    public float tagSizeMeters = 0.05f; // Size of the tag in meters

    [Header("Visualization")]
    [Tooltip("If checked, draws an RGB coordinate axis at the detected AprilTag using Debug.DrawRay.")]
    public bool drawAxesGizmo = true;

    private IntPtr detector;
    private IntPtr family;
    private bool isExecutingTask = false;

    // A structure to hold the detection result queued back to the main thread
    public struct TagResult
    {
        public int id;
        public Vector3 position;
        public Quaternion rotation;
    }

    private ConcurrentQueue<TagResult[]> mainThreadQueue = new ConcurrentQueue<TagResult[]>();

    void Start()
    {
#if (UNITY_ANDROID && !UNITY_EDITOR) || UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Initialize Detector and Tag Family only on actual device
        detector = AprilTagNative.apriltag_detector_create();
        family = AprilTagNative.tag36h11_create(); // Commonly used tag family
        AprilTagNative.apriltag_detector_add_family(detector, family);
#else
        Debug.LogWarning("[QuestAprilTagTracker] AprilTag native library not supported on this platform.");
#endif
    }

    void Update()
    {
        // We dequeue results built by the background thread here
        while (mainThreadQueue.TryDequeue(out TagResult[] detections))
        {
            foreach (var result in detections)
            {
                // Optional: Draw lightweight RGB axes in the Scene/Game view
                if (drawAxesGizmo)
                {
                    // Draw X (Red), Y (Green), Z (Blue) - length is a fraction of tag size
                    float axisLength = tagSizeMeters * 1.5f;
                    Debug.DrawRay(result.position, result.rotation * Vector3.right * axisLength, Color.red, 0.1f);
                    Debug.DrawRay(result.position, result.rotation * Vector3.up * axisLength, Color.green, 0.1f);
                    Debug.DrawRay(result.position, result.rotation * Vector3.forward * axisLength, Color.blue, 0.1f);
                }

                // Call the Event if you had one, or wire it up:
                Debug.Log($"[AprilTag] Detected Tag ID {result.id} at Position {result.position} / Rotation {result.rotation.eulerAngles}");
            }
        }
    }

    private byte[] rawImageBuffer;
    private GCHandle pinnedBuffer;

    /// <summary>
    /// Call this from your Meta XR camera frame callback (OVRManager or Passthrough layer).
    /// </summary>
    public void ProcessRawCameraFrame(IntPtr yChannelBuffer, int width, int height, int stride, double fx, double fy, double cx, double cy, Pose cameraHeadPose)
    {
        // Don't process if the script/GameObject is disabled in the Unity Inspector
        if (!this.isActiveAndEnabled) return;

#if (UNITY_ANDROID && !UNITY_EDITOR) || UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Skip if the background thread is still busy processing a prior frame
        if (isExecutingTask) return;

        isExecutingTask = true;

        // Copy raw memory to managed buffer on the main thread so we don't hold the camera frame hostage.
        int bufferSize = stride * height;
        if (rawImageBuffer == null || rawImageBuffer.Length != bufferSize)
        {
            if (pinnedBuffer.IsAllocated)
                pinnedBuffer.Free();
            rawImageBuffer = new byte[bufferSize];
            pinnedBuffer = GCHandle.Alloc(rawImageBuffer, GCHandleType.Pinned);
        }
        Marshal.Copy(yChannelBuffer, rawImageBuffer, 0, bufferSize);

        // Spin up a background task
        Task.Run(() =>
        {
            try
            {
                // Setup the image struct wrapper on the stack (zero allocation, zero copy!)
                image_u8_t image = new image_u8_t
                {
                    width = width,
                    height = height,
                    stride = stride,
                    buf = pinnedBuffer.AddrOfPinnedObject() // Use our safely copied and pinned memory
                };

                // Execute detection blocking thread
                IntPtr zarrayPtr = AprilTagNative.apriltag_detector_detect(detector, ref image);
                
                // Extract sizes
                IntPtr[] detPointers = AprilTagNative.GetDetectionPointers(zarrayPtr);
                apriltag_detection_t[] detStructs = AprilTagNative.GetDetections(zarrayPtr);
                TagResult[] results = new TagResult[detPointers.Length];

                for (int i = 0; i < detPointers.Length; i++)
                {
                    // Struct for fetching pose
                    apriltag_detection_info_t info = new apriltag_detection_info_t
                    {
                        det = detPointers[i],
                        tagsize = tagSizeMeters,
                        fx = fx,
                        fy = fy,
                        cx = cx,
                        cy = cy
                    };

                    apriltag_pose_t pose;
                    double err = AprilTagNative.estimate_tag_pose(ref info, out pose);

                    // Reconstruct transform from matd_t structures:
                    // OpenCV space is: X right, Y down, Z forward
                    // Unity space is: X right, Y up, Z forward (Left handed)
                    
                    Vector3 position = Vector3.zero;
                    Quaternion rotation = Quaternion.identity;

                    if (pose.t != IntPtr.Zero && pose.R != IntPtr.Zero)
                    {
                        matd_t tMat = Marshal.PtrToStructure<matd_t>(pose.t);
                        matd_t RMat = Marshal.PtrToStructure<matd_t>(pose.R);

                        double[] tData = new double[3];
                        Marshal.Copy(tMat.data, tData, 0, 3);
                        
                        double[] RData = new double[9];
                        Marshal.Copy(RMat.data, RData, 0, 9);

                        // Convert coordinates directly (OpenCV to Unity Space)
                        position = new Vector3((float)tData[0], -(float)tData[1], (float)tData[2]);
                        
                        Matrix4x4 unityRotMat = new Matrix4x4();
                        unityRotMat.m00 = (float)RData[0]; unityRotMat.m01 = (float)RData[1]; unityRotMat.m02 = (float)RData[2]; unityRotMat.m03 = 0;
                        unityRotMat.m10 = -(float)RData[3]; unityRotMat.m11 = -(float)RData[4]; unityRotMat.m12 = -(float)RData[5]; unityRotMat.m13 = 0;
                        unityRotMat.m20 = (float)RData[6]; unityRotMat.m21 = (float)RData[7]; unityRotMat.m22 = (float)RData[8]; unityRotMat.m23 = 0;
                        unityRotMat.m30 = 0; unityRotMat.m31 = 0; unityRotMat.m32 = 0; unityRotMat.m33 = 1;

                        rotation = unityRotMat.rotation;
                        
                        // Apply headset/camera transform so it's in world space
                        position = cameraHeadPose.position + (cameraHeadPose.rotation * position);
                        rotation = cameraHeadPose.rotation * rotation;
                    }

                    results[i] = new TagResult
                    {
                        id = detStructs[i].id,
                        position = position,
                        rotation = rotation
                    };

                    // Free memory allocated by estimate_tag_pose
                    if (pose.R != IntPtr.Zero) AprilTagNative.matd_destroy(pose.R);
                    if (pose.t != IntPtr.Zero) AprilTagNative.matd_destroy(pose.t);
                }

                // Important: Free the returned array memory!
                AprilTagNative.apriltag_detections_destroy(zarrayPtr);

                // Queue to main thread
                mainThreadQueue.Enqueue(results);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AprilTag] Background detection error: {e.Message}");
            }
            finally
            {
                isExecutingTask = false;
            }
        });
#endif
    }

    void OnDestroy()
    {
        if (pinnedBuffer.IsAllocated)
            pinnedBuffer.Free();

#if (UNITY_ANDROID && !UNITY_EDITOR) || UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Clean up the detector to prevent memory leaks when stopping the app
        if (detector != IntPtr.Zero)
        {
            AprilTagNative.apriltag_detector_remove_family(detector, family);
            AprilTagNative.apriltag_detector_destroy(detector);
            AprilTagNative.tag36h11_destroy(family);
            detector = IntPtr.Zero;
            family = IntPtr.Zero;
        }
#endif
    }
}
