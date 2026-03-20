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
    [Tooltip("If checked, draws an RGB coordinate axis at the detected AprilTag.")]
    public bool drawAxesGizmo = true;

    [Tooltip("Uses Meta's distortion-corrected projection. Uncheck if the 3D poses appear broken or completely detached from the tag.")]
    public bool useMetaProjection = true;

    private IntPtr detector;
    private IntPtr family;
    private bool isExecutingTask = false;

    // A structure to hold the detection result queued back to the main thread
    public struct TagResult
    {
        public int id;
        public Vector3 position;
        public Quaternion rotation;
        // Raw OpenCV Data and Camera Frame Info for advanced reprojection
        public Vector3 rawLocalPosition;
        public Vector2 centerPixel;
        public int frameWidth;
        public int frameHeight;
        public Pose cameraPose;
    }

    private ConcurrentQueue<TagResult[]> mainThreadQueue = new ConcurrentQueue<TagResult[]>();
    
    // Optional callback for advanced projection (like Meta Passthrough fisheye fix)
    public Func<Vector2, Ray> RayProjector;
    
    // Dictionary to hold spawned 3D visualizers for each detected tag
    private System.Collections.Generic.Dictionary<int, GameObject> tagVisualizers = new System.Collections.Generic.Dictionary<int, GameObject>();

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
                // Draw 3D axes that are actually visible *inside VR*
                if (drawAxesGizmo)
                {
                    if (!tagVisualizers.TryGetValue(result.id, out GameObject visualizer))
                    {
                        visualizer = new GameObject($"AprilTag_Visualizer_{result.id}");
                        
                        float len = tagSizeMeters * 2.0f; // Make axes big enough to clearly see

                        // Helper to create reliable visible Unlit lines
                        GameObject CreateLine(string name, Color color, Vector3 endPos)
                        {
                            var go = new GameObject(name);
                            go.transform.SetParent(visualizer.transform, false);
                            var lr = go.AddComponent<LineRenderer>();
                            lr.material = new Material(Shader.Find("Sprites/Default")); // Always renders correctly, ignores lighting
                            lr.startColor = color; lr.endColor = color;
                            lr.startWidth = 0.005f; lr.endWidth = 0.005f; // 5mm thick
                            lr.useWorldSpace = false;
                            lr.positionCount = 2;
                            lr.SetPosition(0, Vector3.zero);
                            lr.SetPosition(1, endPos);
                            return go;
                        }

                        CreateLine("X_Red", Color.red, new Vector3(len, 0, 0));
                        CreateLine("Y_Green", Color.green, new Vector3(0, len, 0));
                        CreateLine("Z_Blue", Color.blue, new Vector3(0, 0, len));

                        // Central white sphere so we never lose the origin point!
                        var origin = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        origin.transform.SetParent(visualizer.transform, false);
                        origin.transform.localScale = Vector3.one * 0.015f; // 1.5 cm sphere
                        var mat = new Material(Shader.Find("Sprites/Default"));
                        mat.color = Color.white;
                        origin.GetComponent<Renderer>().material = mat;
                        Destroy(origin.GetComponent<Collider>());

                        tagVisualizers[result.id] = visualizer;
                    }

                    // Use hardware ray projection for absolute stereo alignment if available
                    if (useMetaProjection && RayProjector != null && result.frameWidth > 0 && result.frameHeight > 0)
                    {
                        // Because we physically flipped the image (scaleY = -1 in Graphics.Blit), OpenCV Y=0 (top) 
                        // maps to the original hardware image's bottom. Therefore, OpenCV Y / height equals
                        // exactly Meta's standard Viewport V! (No 1.0f - V inversion needed)
                        Vector2 uv = new Vector2(
                            result.centerPixel.x / result.frameWidth,
                            result.centerPixel.y / result.frameHeight 
                        );

                        // Project a mathematically perfect ray through Meta's fisheye un-distortion mesh
                        Ray worldRay = RayProjector(uv);
                        
                        // Z is standard depth computed by OpenCV
                        float distance = result.rawLocalPosition.magnitude * 1.0f; // Scale this if depth is consistently wrong
                        
                        // Override position to EXACTLY align with Meta's physical depth
                        visualizer.transform.position = worldRay.origin + (worldRay.direction.normalized * distance);
                        visualizer.transform.rotation = result.rotation; 
                    }
                    else
                    {
                        // Update Position and Rotation based purely on standard camera offset math
                        visualizer.transform.position = result.position;
                        visualizer.transform.rotation = result.rotation;
                    }
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
                        // To convert OpenCv (X-right, Y-down, Z-in) to Unity (X-right, Y-up, Z-in):
                        // We negate the Y displacement and the second row + second column of the rotation matrix.
                        position = new Vector3((float)tData[0], -(float)tData[1], (float)tData[2]);
                        
                        Matrix4x4 unityRotMat = new Matrix4x4();
                        unityRotMat.m00 = (float)RData[0];  unityRotMat.m01 = -(float)RData[1]; unityRotMat.m02 = (float)RData[2]; unityRotMat.m03 = 0;
                        unityRotMat.m10 = -(float)RData[3]; unityRotMat.m11 = (float)RData[4];  unityRotMat.m12 = -(float)RData[5]; unityRotMat.m13 = 0;
                        unityRotMat.m20 = (float)RData[6];  unityRotMat.m21 = -(float)RData[7]; unityRotMat.m22 = (float)RData[8]; unityRotMat.m23 = 0;
                        unityRotMat.m30 = 0; unityRotMat.m31 = 0; unityRotMat.m32 = 0; unityRotMat.m33 = 1;

                        rotation = unityRotMat.rotation;
                        
                        // Apply headset/camera transform so it's in world space
                        position = cameraHeadPose.position + (cameraHeadPose.rotation * position);
                        rotation = cameraHeadPose.rotation * rotation;
                    }

                    unsafe {
                        results[i] = new TagResult
                        {
                            id = detStructs[i].id,
                            position = position,
                            rotation = rotation,
                            rawLocalPosition = pose.t != IntPtr.Zero ? new Vector3((float)Marshal.PtrToStructure<matd_t>(pose.t).data, 0, 0) : Vector3.zero, // Filled properly below
                            centerPixel = new Vector2((float)detStructs[i].c[0], (float)detStructs[i].c[1]),
                            frameWidth = width,
                            frameHeight = height,
                            cameraPose = cameraHeadPose
                        };

                        if (pose.t != IntPtr.Zero)
                        {
                            double[] tData = new double[3];
                            matd_t tMat = Marshal.PtrToStructure<matd_t>(pose.t);
                            Marshal.Copy(tMat.data, tData, 0, 3);
                            results[i].rawLocalPosition = new Vector3((float)tData[0], -(float)tData[1], (float)tData[2]);
                        }
                    }

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
