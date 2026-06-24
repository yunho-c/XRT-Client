using System.Collections;
using UnityEngine;
using Unity.WebRTC;
using TMPro;

public class MediaMTXReceiver : MonoBehaviour
{
    [Header("WebRTC Settings")]
    [Tooltip("Enable to automatically start the WebRTC connection on start")]
    public bool autoStartConnection = false;

    // Base address for user input (e.g., "localhost:8889/stream" or "192.168.0.101:8889/zed")
    [SerializeField] private string defaultBaseAddress = "192.168.0.104:8889/zed";

    [Header("UI Elements")]
    // Reference to the InputField (to load the saved URL)
    [SerializeField] private TMP_InputField ipAddressInputField;
    // Reference to the Text component (to show status)
    [SerializeField] private TMP_Text statusText;
    // Reference to the connect toggle to disable during connection
    [SerializeField] private UnityEngine.UI.Toggle connectToggle;

    // Single material with both texture slots
    [SerializeField] private Material stereoMaterial;

    // GameObject to hide/show the stereo display
    [SerializeField] private GameObject stereoDisplayObject;

    // Default state for video stream visibility
    [Tooltip("Default state for video stream visibility (overridden by PlayerPrefs)")]
    public bool videoStreamVisible = true;

    [Header("Connection")]
    [Tooltip("Seconds to wait for the WHEP offer POST before giving up (so a bad address/server can't hang forever).")]
    public float connectTimeoutSeconds = 10f;

    private string urlLeft;
    private string urlRight;

    private RTCPeerConnection pcLeft;
    private RTCPeerConnection pcRight;
    private MediaStream receiveStreamLeft;
    private MediaStream receiveStreamRight;

    // References to the actual video tracks
    private VideoStreamTrack _videoTrackLeft;
    private VideoStreamTrack _videoTrackRight;

    // Guard against repeated connection attempts
    private bool isConnecting = false;

    // Tracked connect coroutines so we can cancel them without killing WebRTC.Update().
    private Coroutine _coLeft;
    private Coroutine _coRight;

    // Cached decoder textures bound to the stereo material, set ONLY from OnVideoReceived
    // (the authoritative "a real frame arrived" signal) and cleared on disconnect. The
    // display MeshRenderer is gated on BOTH being present: while there is no live video the
    // renderer is disabled so the camera passthrough shows through (the camera clears to
    // transparent over an OVRPassthroughLayer), instead of the blend shader painting a
    // released/blank RenderTexture as a white semi-transparent overlay or an opaque black void.
    private Texture _matTexLeft;
    private Texture _matTexRight;
    private MeshRenderer _meshRenderer;

    void Start()
    {
        _meshRenderer = GetComponent<MeshRenderer>();
        // Nothing to show until a frame arrives — keep passthrough until then.
        UpdateDisplayVisibility();

        // 1. Load saved stream visibility state and apply it
        bool savedVideoVisible = PlayerPrefs.GetInt("stereoStreamVisible", videoStreamVisible ? 1 : 0) == 1;
        ToggleVideoStream(savedVideoVisible);

        // 2. Load and set the server URL (sets the internal urlLeft/urlRight)
        string savedBaseAddress = PlayerPrefs.GetString("stereoBaseUrl", defaultBaseAddress);
        SetBaseStreamUrl(savedBaseAddress);

        // 3. Initialize Input Field and Status Text
        if (ipAddressInputField != null)
        {
            ipAddressInputField.text = savedBaseAddress;
        }
        if (statusText != null)
        {
            statusText.text = "Ready to connect.";
        }

        // Reset connection state
        isConnecting = false;
        SetConnectToggleInteractable(true);

        InitializePeerConnections();

        StartCoroutine(WebRTC.Update());

        // Auto-connect on first activation (preserves existing behavior).
        if (true)
        {
            Debug.Log($"Auto-starting connection to: {savedBaseAddress}");
            UpdateStatusText($"Auto-connecting to: {savedBaseAddress}...");
            StartStream();
        }
    }

    // Creates fresh peer connections (disposing any previous ones first). Does NOT start a
    // connection — call StartStream() for that. Safe to call repeatedly for clean retries.
    private void InitializePeerConnections()
    {
        if (_coLeft  != null) { StopCoroutine(_coLeft);  _coLeft  = null; }
        if (_coRight != null) { StopCoroutine(_coRight); _coRight = null; }

        pcLeft?.Close();  pcLeft?.Dispose();  pcLeft = null;
        pcRight?.Close(); pcRight?.Dispose(); pcRight = null;
        receiveStreamLeft?.Dispose();  receiveStreamLeft = null;
        receiveStreamRight?.Dispose(); receiveStreamRight = null;
        _videoTrackLeft = null;
        _videoTrackRight = null;

        // Drop references to the old (now disposed) decoder textures and hide the display
        // (show passthrough) until fresh frames arrive on reconnect — so a torn-down or
        // failed stream never lingers as a white overlay / black void on the shared material.
        _matTexLeft = null;
        _matTexRight = null;
        UpdateDisplayVisibility();

        RTCConfiguration config = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer {urls = new[] {"stun:stun.l.google.com:19302"}}
            }
        };

        // Initialize left eye stream
        pcLeft = new RTCPeerConnection(ref config);
        receiveStreamLeft = new MediaStream();

        pcLeft.OnIceConnectionChange = state =>
        {
            Debug.Log($"Left ICE Connection State: {state}");
            UpdateStatusText($"Left ICE State: {state}");
        };

        pcLeft.OnConnectionStateChange = state =>
        {
            Debug.Log($"Left Connection State: {state}");
            if (state == RTCPeerConnectionState.Connected)
            {
                UpdateStatusText("Left Peer connected!");
            }
            else if (state == RTCPeerConnectionState.Failed ||
                     state == RTCPeerConnectionState.Disconnected ||
                     state == RTCPeerConnectionState.Closed)
            {
                // Only reset if right is also not connected
                if (pcRight == null ||
                    pcRight.ConnectionState == RTCPeerConnectionState.Failed ||
                    pcRight.ConnectionState == RTCPeerConnectionState.Disconnected ||
                    pcRight.ConnectionState == RTCPeerConnectionState.Closed)
                {
                    ResetConnectionState($"Connection {state}. Press to retry.");
                }
            }
        };

        pcLeft.OnTrack = e =>
        {
            receiveStreamLeft.AddTrack(e.Track);
        };

        receiveStreamLeft.OnAddTrack = e =>
        {
            if (e.Track is VideoStreamTrack videoTrack)
            {
                _videoTrackLeft = videoTrack;
                _videoTrackLeft.Enabled = videoStreamVisible;

                videoTrack.OnVideoReceived += (tex) =>
                {
                    _matTexLeft = tex;
                    if (stereoMaterial != null) stereoMaterial.SetTexture("_Left", tex);
                    UpdateDisplayVisibility();
                };
            }
        };

        RTCRtpTransceiverInit initLeft = new RTCRtpTransceiverInit();
        initLeft.direction = RTCRtpTransceiverDirection.RecvOnly;
        pcLeft.AddTransceiver(TrackKind.Video, initLeft);

        // Initialize right eye stream
        pcRight = new RTCPeerConnection(ref config);
        receiveStreamRight = new MediaStream();

        pcRight.OnIceConnectionChange = state =>
        {
            Debug.Log($"Right ICE Connection State: {state}");
            UpdateStatusText($"Right ICE State: {state}");
        };

        pcRight.OnConnectionStateChange = state =>
        {
            Debug.Log($"Right Connection State: {state}");
            if (state == RTCPeerConnectionState.Connected)
            {
                // Both streams connected - reset the guard and re-enable button
                if (pcLeft != null && pcLeft.ConnectionState == RTCPeerConnectionState.Connected)
                {
                    ResetConnectionState("Streaming active.");
                }
                else
                {
                    UpdateStatusText("Right Peer connected! Waiting for left...");
                }
            }
            else if (state == RTCPeerConnectionState.Failed ||
                     state == RTCPeerConnectionState.Disconnected ||
                     state == RTCPeerConnectionState.Closed)
            {
                ResetConnectionState($"Connection {state}. Press to retry.");
            }
        };

        pcRight.OnTrack = e =>
        {
            receiveStreamRight.AddTrack(e.Track);
        };

        receiveStreamRight.OnAddTrack = e =>
        {
            if (e.Track is VideoStreamTrack videoTrack)
            {
                _videoTrackRight = videoTrack;
                _videoTrackRight.Enabled = videoStreamVisible;

                videoTrack.OnVideoReceived += (tex) =>
                {
                    _matTexRight = tex;
                    if (stereoMaterial != null) stereoMaterial.SetTexture("_Right", tex);
                    UpdateDisplayVisibility();
                };
            }
        };

        RTCRtpTransceiverInit initRight = new RTCRtpTransceiverInit();
        initRight.direction = RTCRtpTransceiverDirection.RecvOnly;
        pcRight.AddTransceiver(TrackKind.Video, initRight);
    }

    // Helper method to safely update the status text
    private void UpdateStatusText(string message)
    {
        if (statusText != null)
        {
            statusText.text = message;
        }
        Debug.Log($"Status: {message}");
    }

    // Helper method to safely set toggle interactable state
    private void SetConnectToggleInteractable(bool interactable)
    {
        if (connectToggle != null)
        {
            connectToggle.interactable = interactable;
        }
    }

    // Helper method to reset connection state and re-enable UI
    private void ResetConnectionState(string statusMessage)
    {
        isConnecting = false;
        SetConnectToggleInteractable(true);
        UpdateStatusText(statusMessage);
    }

    private bool IsConnected()
    {
        return pcLeft != null && pcRight != null &&
               pcLeft.ConnectionState == RTCPeerConnectionState.Connected &&
               pcRight.ConnectionState == RTCPeerConnectionState.Connected;
    }

    /// <summary>
    /// True while a stream connection is established or in progress. Used by the study
    /// manager's Streaming Feed button to reflect the real state at connect time (Unity
    /// auto-starts the stream on launch).
    /// </summary>
    public bool IsStreamingActive => isConnecting || IsConnected();

    /// <summary>
    /// Single entry point for the Streaming Connection button: connect when idle, or cancel the
    /// in-progress / established connection when pressed again (so a snagged attempt can be retried
    /// without reloading the scene).
    /// </summary>
    /// <returns>The new intended state: true if a connection was started, false if cancelled.</returns>
    public bool ToggleConnection()
    {
        if (isConnecting || IsConnected())
        {
            CancelConnection("Streaming connection cancelled — press to retry.");
            return false;
        }
        StartStream();
        return true;
    }

    // Public function to be called by a dedicated "Connect" button.
    public void StartStream()
    {
        // Already mid-connect: do nothing (cancel is handled by ToggleConnection).
        if (isConnecting)
        {
            Debug.Log("Connection already in progress.");
            return;
        }

        // Ensure we have fresh peer connections (recreate if missing or in a dead state).
        if (pcLeft == null || pcRight == null ||
            pcLeft.ConnectionState  == RTCPeerConnectionState.Closed || pcLeft.ConnectionState  == RTCPeerConnectionState.Failed ||
            pcRight.ConnectionState == RTCPeerConnectionState.Closed || pcRight.ConnectionState == RTCPeerConnectionState.Failed)
        {
            InitializePeerConnections();
        }

        // NOTE: intentionally do NOT disable the connect toggle here — it must stay pressable so
        // the user can press again to cancel a snagged connection.
        isConnecting = true;

        UpdateStatusText($"Connecting to {urlLeft} / {urlRight}...");

        _coLeft  = StartCoroutine(createOffer(pcLeft,  urlLeft));
        _coRight = StartCoroutine(createOffer(pcRight, urlRight));
    }

    /// <summary>
    /// Cancel any in-progress / established connection and re-create fresh peer connections so the
    /// next StartStream() is a clean retry. Keeps the GameObject active and WebRTC.Update running.
    /// </summary>
    public void CancelConnection(string reason = "Disconnected.")
    {
        isConnecting = false;
        SetConnectToggleInteractable(true);
        InitializePeerConnections();   // stops tracked coroutines + disposes + recreates
        UpdateStatusText(reason);
    }

    // Public method to manually stop the connection.
    public void StopStream()
    {
        CancelConnection("Disconnected.");
    }

    // Public method to be called from a UI InputField's On End Edit (String) event
    public void SetBaseStreamUrl(string baseAddressAndPort)
    {
        if (string.IsNullOrEmpty(baseAddressAndPort)) return;

        // 1. Save the new base address
        PlayerPrefs.SetString("stereoBaseUrl", baseAddressAndPort);
        PlayerPrefs.Save();

        // 2. Construct the final URLs with the "backwards" logic
        urlLeft = $"http://{baseAddressAndPort}/right/whep";
        urlRight = $"http://{baseAddressAndPort}/left/whep";

        UpdateStatusText($"URL set: {baseAddressAndPort}");
    }

    public void ToggleVideoStream(bool isOn)
    {
        videoStreamVisible = isOn;

        if (_videoTrackLeft != null)
        {
            _videoTrackLeft.Enabled = isOn;
        }
        if (_videoTrackRight != null)
        {
            _videoTrackRight.Enabled = isOn;
        }

        if (stereoDisplayObject != null)
        {
            stereoDisplayObject.SetActive(isOn);
        }

        PlayerPrefs.SetInt("stereoStreamVisible", isOn ? 1 : 0);
        PlayerPrefs.Save();
    }

    // Show the display only when BOTH eyes have a live decoder texture; otherwise disable
    // the MeshRenderer so the transparent-clearing camera reveals passthrough (instead of a
    // white overlay from a blank/released RenderTexture, or an opaque black void). Once a
    // texture is bound here it keeps updating in place as WebRTC pumps new frames, so no
    // per-frame re-binding is needed.
    private void UpdateDisplayVisibility()
    {
        if (_meshRenderer != null)
        {
            _meshRenderer.enabled = (_matTexLeft != null && _matTexRight != null);
        }
    }

    private IEnumerator createOffer(RTCPeerConnection pc, string url)
    {
        var op = pc.CreateOffer();
        yield return op;
        if (op.IsError) {
            Debug.LogError($"CreateOffer() failed for {url}");
            CancelConnection($"Error creating offer for {url}. Press to retry.");
            yield break;
        }

        yield return setLocalDescription(pc, op.Desc, url);
    }

    private IEnumerator setLocalDescription(RTCPeerConnection pc, RTCSessionDescription offer, string url)
    {
        var op = pc.SetLocalDescription(ref offer);
        yield return op;
        if (op.IsError) {
            Debug.LogError($"SetLocalDescription() failed for {url}");
            CancelConnection($"Error setting local description for {url}. Press to retry.");
            yield break;
        }

        yield return postOffer(pc, offer, url);
    }

    private IEnumerator postOffer(RTCPeerConnection pc, RTCSessionDescription offer, string url)
    {
        UpdateStatusText($"Sending offer to {url}...");
        var content = new System.Net.Http.StringContent(offer.sdp);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sdp");

        float timeout = Mathf.Max(1f, connectTimeoutSeconds);
        var task = System.Threading.Tasks.Task.Run(async () => {
            using (var client = new System.Net.Http.HttpClient())
            {
                // Bounded timeout so an unreachable server can't hang the connection forever.
                client.Timeout = System.TimeSpan.FromSeconds(timeout);
                var res = await client.PostAsync(new System.UriBuilder(url).Uri, content);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync();
            }
        });
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Exception != null) {
            Debug.LogError($"PostOffer() failed for {url}: {task.Exception.InnerException?.Message ?? task.Exception.Message}");
            CancelConnection($"Connection failed: {task.Exception.InnerException?.Message ?? task.Exception.Message}. Press to retry.");
            yield break;
        }

        UpdateStatusText($"Received answer from {url}. Setting remote description...");
        yield return setRemoteDescription(pc, task.Result, url);
    }

    private IEnumerator setRemoteDescription(RTCPeerConnection pc, string answer, string url)
    {
        RTCSessionDescription desc = new RTCSessionDescription();
        desc.type = RTCSdpType.Answer;
        desc.sdp = answer;
        var op = pc.SetRemoteDescription(ref desc);
        yield return op;
        if (op.IsError) {
            Debug.LogError($"SetRemoteDescription() failed for {url}");
            CancelConnection($"Error setting remote description for {url}. Press to retry.");
            yield break;
        }

        UpdateStatusText("WebRTC negotiation complete. Waiting for stream...");
        yield break;
    }

    void OnDestroy()
    {
        // Stop all running coroutines
        StopAllCoroutines();

        // Save the current visibility state
        PlayerPrefs.SetInt("stereoStreamVisible", videoStreamVisible ? 1 : 0);

        // Save the latest successful base address
        if (!string.IsNullOrEmpty(urlLeft))
        {
             // Extract the base address part from one of the final URLs
             string baseAddress = urlLeft.Replace("http://", "").Replace("/right/whep", "");
             PlayerPrefs.SetString("stereoBaseUrl", baseAddress);
        }
        PlayerPrefs.Save();

        pcLeft?.Close();
        pcLeft?.Dispose();
        receiveStreamLeft?.Dispose();

        pcRight?.Close();
        pcRight?.Dispose();
        receiveStreamRight?.Dispose();
    }
}
