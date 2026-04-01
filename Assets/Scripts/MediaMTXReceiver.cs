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
    [SerializeField] private string defaultBaseAddress = "192.168.0.101:8889/zed";

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

    void Start()
    {
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

        // Configure ICE servers
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
                    ResetConnectionState($"Connection {state}. Try again.");
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
                    if (stereoMaterial != null)
                    {
                        stereoMaterial.SetTexture("_Left", tex);
                    }
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
                ResetConnectionState($"Connection {state}. Try again.");
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
                    if (stereoMaterial != null)
                    {
                        stereoMaterial.SetTexture("_Right", tex);
                    }
                };
            }
        };

        RTCRtpTransceiverInit initRight = new RTCRtpTransceiverInit();
        initRight.direction = RTCRtpTransceiverDirection.RecvOnly;
        pcRight.AddTransceiver(TrackKind.Video, initRight);

        StartCoroutine(WebRTC.Update());
        
        // Check for auto-start connection
        if (true)
        {
            Debug.Log($"Auto-starting connection to: {savedBaseAddress}");
            UpdateStatusText($"Auto-connecting to: {savedBaseAddress}...");
            StartStream();
        }
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

    // Public function to be called by a dedicated "Connect" button.
    public void StartStream()
    {
        // Guard against repeated clicks while connecting
        if (isConnecting)
        {
            Debug.Log("Connection already in progress, ignoring click.");
            UpdateStatusText("Connection in progress, please wait...");
            return;
        }

        if (pcLeft == null || pcRight == null)
        {
            UpdateStatusText("Error: Peer connections not initialized. Restart component.");
            return;
        }
        
        // Set the guard and disable the button
        isConnecting = true;
        SetConnectToggleInteractable(false);
        
        UpdateStatusText($"Starting stream connection offers for {urlLeft} and {urlRight}...");
        
        // Start the connection using the current (most recently saved) URLs
        StartCoroutine(createOffer(pcLeft, urlLeft));
        StartCoroutine(createOffer(pcRight, urlRight));
    }
    
    // Public method to manually stop the connection
    public void StopStream()
    {
        pcLeft?.Close();
        pcLeft?.Dispose();
        receiveStreamLeft?.Dispose();

        pcRight?.Close();
        pcRight?.Dispose();
        receiveStreamRight?.Dispose();

        // Re-initialize for next start
        Start(); 
        
        UpdateStatusText("Disconnected.");
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

    private IEnumerator createOffer(RTCPeerConnection pc, string url)
    {
        var op = pc.CreateOffer();
        yield return op;
        if (op.IsError) {
            Debug.LogError($"CreateOffer() failed for {url}");
            ResetConnectionState($"Error creating offer for {url}");
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
            ResetConnectionState($"Error setting local description for {url}");
            yield break;
        }

        yield return postOffer(pc, offer, url);
    }

    private IEnumerator postOffer(RTCPeerConnection pc, RTCSessionDescription offer, string url)
    {
        UpdateStatusText($"Sending offer to {url}...");
        var content = new System.Net.Http.StringContent(offer.sdp);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sdp");

        var task = System.Threading.Tasks.Task.Run(async () => {
            using (var client = new System.Net.Http.HttpClient())
            {
                var res = await client.PostAsync(new System.UriBuilder(url).Uri, content);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync();
            }
        });
        yield return new WaitUntil(() => task.IsCompleted);
        
        if (task.Exception != null) {
            Debug.LogError($"PostOffer() failed for {url}: {task.Exception.InnerException?.Message ?? task.Exception.Message}");
            ResetConnectionState($"Connection failed: {task.Exception.InnerException?.Message ?? task.Exception.Message}");
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
            ResetConnectionState($"Error setting remote description for {url}");
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