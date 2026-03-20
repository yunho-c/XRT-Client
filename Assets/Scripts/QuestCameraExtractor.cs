using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Meta.XR;

/// <summary>
/// Extracts raw GPU camera frames from Meta's PassthroughCameraAccess (PCA)
/// via AsyncGPUReadback, computes grayscale bytes natively, and feeds them 
/// into the AprilTagTracker utilizing pointers.
/// </summary>
public class QuestCameraExtractor : MonoBehaviour
{
    [Tooltip("Drag the PassthroughCameraAccess component here (Left or Right camera).")]
    public PassthroughCameraAccess cameraAccess;

    [Tooltip("Drag your QuestAprilTagTracker component here")]
    public QuestAprilTagTracker tracker;

    // Quest native tracking requires checking permission
    private const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

    private bool readbackInProgress = false;
    private byte[] grayscaleBuffer;
    private int debugFrameCounter = 0;

    void Start()
    {
        Debug.Log("[QuestCameraExtractor] Start() called. Is cameraAccess assigned? " + (cameraAccess != null));
        if (cameraAccess == null)
        {
            Debug.LogError("[QuestCameraExtractor] PassthroughCameraAccess is NULL! Please assign it in the Inspector.");
        }
        if (tracker == null)
        {
            Debug.LogError("[QuestCameraExtractor] QuestAprilTagTracker is NULL! Please assign it in the Inspector.");
        }

        // Request Passthrough Camera Permission dynamically at startup
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(CameraPermission))
        {
            UnityEngine.Android.Permission.RequestUserPermission(CameraPermission);
        }
#endif
    }

    void Update()
    {
        // Debug periodic check to verify the state of the subsystem and permissions
        if (debugFrameCounter++ % 120 == 0 && cameraAccess != null)
        {
            bool hasPerm = UnityEngine.Android.Permission.HasUserAuthorizedPermission(CameraPermission);
            bool isPlaying = cameraAccess.IsPlaying;
            Debug.Log($"[QuestCameraExtractor Status] Camera Permission Granted: {hasPerm} | PCA Playing: {isPlaying}");
        }

        if (cameraAccess == null || tracker == null || !tracker.isActiveAndEnabled)
            return;

        // Ensure PCA is running and wait until we aren't currently waiting on the GPU
        if (cameraAccess.IsPlaying && !readbackInProgress && cameraAccess.IsUpdatedThisFrame)
        {
            Texture tex = cameraAccess.GetTexture();
            if (tex != null)
            {
                readbackInProgress = true;
                // Request readback without blocking the main render thread
                AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBA32, OnReadbackComplete);
            }
        }
    }

    private unsafe void OnReadbackComplete(AsyncGPUReadbackRequest request)
    {
        readbackInProgress = false;

        if (request.hasError)
        {
            Debug.LogWarning("[QuestCameraExtractor] GPU Readback error.");
            return;
        }

        // We receive the data as RGBA32
        NativeArray<Color32> colors = request.GetData<Color32>();
        int width = request.width;
        int height = request.height;
        int size = width * height;

        // Allocate or resize grayscale buffer if necessary
        if (grayscaleBuffer == null || grayscaleBuffer.Length != size)
        {
            grayscaleBuffer = new byte[size];
            Debug.Log($"[QuestCameraExtractor] Allocated Grayscale Buffer: {width}x{height}");
        }

        // Fast conversion: Copy the Green channel of each RGBA block into our grayscale buffer
        Color32* colorPtr = (Color32*)colors.GetUnsafeReadOnlyPtr();
        fixed (byte* grayPtr = grayscaleBuffer)
        {
            Color32* src = colorPtr;
            byte* dst = grayPtr;
            for (int i = 0; i < size; i++)
            {
                // Just use the green channel for Y (luminance) as an approximation
                *dst = src->g; 
                src++;
                dst++;
            }
        }

        // Extract camera intrinsics
        var intrinsics = cameraAccess.Intrinsics;
        double fx = intrinsics.FocalLength[0];
        double fy = intrinsics.FocalLength[1];
        double cx = intrinsics.PrincipalPoint[0];
        double cy = intrinsics.PrincipalPoint[1];

        // Extrapolate center offset if intrinsics default to 0 due to API latency
        if (fx == 0) fx = 500.0;
        if (fy == 0) fy = 500.0;
        if (cx == 0) cx = width / 2.0;
        if (cy == 0) cy = height / 2.0;

        // Fetch precise spatial camera pose at the exact capture timestamp
        Pose cameraPose = cameraAccess.GetCameraPose();
        if (cameraPose == default)
        {
            // Fallback if the headset doesn't have tracking lock yet
            cameraPose = new Pose(transform.position, transform.rotation);
        }

        // Pass memory directly to our native tracker zero-copy
        fixed (byte* ptr = grayscaleBuffer)
        {
            tracker.ProcessRawCameraFrame((IntPtr)ptr, width, height, width, fx, fy, cx, cy, cameraPose);
        }
    }
}
