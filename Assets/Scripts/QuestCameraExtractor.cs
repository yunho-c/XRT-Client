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
        else if (cameraAccess != null)
        {
            // Bind Meta's true fisheye distortion un-projector to the tracker!
            tracker.RayProjector = (uv) => cameraAccess.ViewportPointToRay(uv);
        }

        // Request Passthrough Camera Permission dynamically at startup
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(CameraPermission))
        {
            UnityEngine.Android.Permission.RequestUserPermission(CameraPermission);
        }
#endif
    }

    private RenderTexture tempRenderTexture;

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
                
                // Quest camera textures often can't be read directly (ExternalOES). 
                // We must blit to a temporary RenderTexture first to ensure readback succeeds.
                int targetWidth = tex.width;
                int targetHeight = tex.height;

                if (tempRenderTexture == null || tempRenderTexture.width != targetWidth || tempRenderTexture.height != targetHeight)
                {
                    if (tempRenderTexture != null) tempRenderTexture.Release();
                    tempRenderTexture = new RenderTexture(targetWidth, targetHeight, 0, RenderTextureFormat.R8);
                    tempRenderTexture.Create();
                }
                
                // Scale Y by -1 to flip the ExternalOES/hardware image right-side up
                Graphics.Blit(tex, tempRenderTexture, new Vector2(1, -1), new Vector2(0, 1));

                // Request readback directly as R8 (Grayscale)
                AsyncGPUReadback.Request(tempRenderTexture, 0, TextureFormat.R8, OnReadbackComplete);
            }
        }
    }

    void OnApplicationPause(bool isPaused)
    {
        if (isPaused)
        {
            // If the app pauses (e.g. system keyboard opens), reset the flag so we don't get stuck forever
            // if Unity drops the GPU readback request without firing the callback.
            readbackInProgress = false;
        }
    }

    private unsafe void OnReadbackComplete(AsyncGPUReadbackRequest request)
    {
        readbackInProgress = false;

        if (request.hasError)
        {
            Debug.LogWarning("[QuestCameraExtractor] GPU Readback error. The texture format might not be supported.");
            return;
        }

        // We receive the data directly as a 1-byte grayscale array (R8 format)
        NativeArray<byte> colors = request.GetData<byte>();
        int width = request.width;
        int height = request.height;
        int size = width * height;

        // Allocate or resize grayscale buffer if necessary
        if (grayscaleBuffer == null || grayscaleBuffer.Length != size)
        {
            grayscaleBuffer = new byte[size];
            Debug.Log($"[QuestCameraExtractor] Allocated Grayscale Buffer: {width}x{height}");
        }

        // Fast memory copy instead of iterating pixel by pixel
        colors.CopyTo(grayscaleBuffer);

        // Sanity check: Sample the center pixel to ensure we aren't passing a completely black image to the tracker
        if (Time.frameCount % 120 == 0)
        {
            int centerPixelIndex = (height / 2) * width + (width / 2);
            Debug.Log($"[QuestCameraExtractor] Center pixel brightness: {grayscaleBuffer[centerPixelIndex]} / 255");
        }

        // Extract camera intrinsics
        var intrinsics = cameraAccess.Intrinsics;
        double fx = intrinsics.FocalLength[0];
        double fy = intrinsics.FocalLength[1];
        double cx = intrinsics.PrincipalPoint[0];
        double cy = intrinsics.PrincipalPoint[1];

        // Extrapolate center offset if intrinsics default to 0 due to API latency
        if (fx == 0) fx = 867.0839;
        if (fy == 0) fy = 867.0839;
        if (cx == 0) cx = 642.3784;
        if (cy == 0) cy = 643.5328;

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
