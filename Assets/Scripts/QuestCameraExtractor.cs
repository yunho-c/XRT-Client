using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Extracts raw YUV camera frames from ARFoundation's ARCameraManager 
/// and feeds them into the AprilTagTracker utilizing zero-copy pointers.
/// </summary>
public class QuestCameraExtractor : MonoBehaviour
{
    [Tooltip("Drag your AR Camera Manager here")]
    public ARCameraManager cameraManager;

    [Tooltip("Drag your QuestAprilTagTracker component here")]
    public QuestAprilTagTracker tracker;

    [Header("Camera Intrinsics (Approximations if ARFoundation doesn't supply them)")]
    public double defaultFx = 500.0;
    public double defaultFy = 500.0;
    public double defaultCx = 512.0;
    public double defaultCy = 512.0;

    void OnEnable()
    {
        if (cameraManager != null)
        {
            // Subscribe to the camera frame event
            cameraManager.frameReceived += OnCameraFrameReceived;
        }
    }

    void OnDisable()
    {
        if (cameraManager != null)
        {
            // Always unsubscribe to prevent memory leaks
            cameraManager.frameReceived -= OnCameraFrameReceived;
        }
    }

    private unsafe void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        // If tracker is disabled, don't bother acquiring the image
        if (tracker == null || !tracker.isActiveAndEnabled) return;

        // Attempt to grab the latest raw CPU image from the Quest 3 cameras
        if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            return;

        try
        {
            // Quest 3 outputs YUV420. 
            // Plane 0 is 'Y' (Luminance/Grayscale). Plane 1 is 'U', Plane 2 is 'V'.
            // AprilTag ONLY needs Plane 0!
            XRCpuImage.Plane yPlane = image.GetPlane(0);

            // Get the raw memory pointer to the grayscale data
            void* yPointer = yPlane.data.GetUnsafeReadOnlyPtr();

            // Extract the dimensions and stride needed for the AprilTag C struct
            int width = image.width;
            int height = image.height;
            int stride = yPlane.rowStride;

            // Optional: Pull real intrinsics from ARFoundation if supported
            double fx = defaultFx;
            double fy = defaultFy;
            double cx = defaultCx;
            double cy = defaultCy;

            if (cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            {
                fx = intrinsics.focalLength.x;
                fy = intrinsics.focalLength.y;
                cx = intrinsics.principalPoint.x;
                cy = intrinsics.principalPoint.y;
            }

            // Get standard Unity Camera for head position offset
            // We pass the camera's pose so tracked tags can be converted to World Space correctly.
            Camera standardCam = cameraManager.GetComponent<Camera>();
            Pose cameraPose = Pose.identity;
            if (standardCam != null)
            {
                cameraPose = new Pose(standardCam.transform.position, standardCam.transform.rotation);
            }
            else
            {
                cameraPose = new Pose(transform.position, transform.rotation);
            }

            // Feed to the background thread tracker
            tracker.ProcessRawCameraFrame(
                (IntPtr)yPointer, 
                width, 
                height, 
                stride, 
                fx, fy, cx, cy, 
                cameraPose
            );
        }
        finally
        {
            // CRITICAL: You must dispose of the XRCpuImage every single frame.
            // If you forget this, the Quest's memory will fill up and crash the app in seconds.
            image.Dispose();
        }
    }
}
