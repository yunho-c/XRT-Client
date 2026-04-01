using System.Collections;
using UnityEngine;
using Unity.WebRTC;
using TMPro;
using NativeWebSocket;
using Oculus.Interaction.Body.Input;
using System;
[System.Serializable]
public class SdpData
{
    public string type;
    public string sdp;
}
[System.Serializable]
public class IceCandidateData
{
    public string candidate;
    public string sdpMid;
    public int sdpMLineIndex;
    public string usernameFragment;
}
[System.Serializable]
public class SignalingMsg
{
    public string type;
    public string peerId;
    public string role;
    public string from;
    public string target;
    public SdpData offer;
    public SdpData answer;
    public IceCandidateData candidate;
    public string message;
}
public class WebRTCReader : MonoBehaviour
{
    [Header("UI Elements")]
    [SerializeField] private TMP_InputField videoServerUrl;
    [SerializeField] private TMP_Text statusText;

    [Header("WebRTC Settings")]
    [Tooltip("Default video server URL (used if no PlayerPrefs saved)")]
    public string defaultVideoServerUrl = "ws://localhost:3000/ws";


    [Tooltip("Enable to automatically start the WebRTC connection on start")]
    public bool autoStartConnection = false;

    public string peerId = "UNITY";
    private RTCPeerConnection pc;
    private MediaStream receiveStream;
    private Renderer targetRenderer; //meshRenderer
    private WebSocket ws;
    private string publisherId;

    void Start()
    {
        // Load saved video server URL from PlayerPrefs, fallback to default
        string savedUrl = PlayerPrefs.GetString("videoServerUrl");
        string urlToUse = defaultVideoServerUrl; // Start with default

        if (!string.IsNullOrEmpty(savedUrl))
        {
            try
            {
                System.Uri uri = new System.Uri(savedUrl);
                if (!string.IsNullOrEmpty(uri.Host))
                {
                    urlToUse = savedUrl; // Use saved URL if valid
                }
            }
            catch (System.Exception)
            {
                Debug.LogWarning($"Ignoring invalid videoServerUrl from PlayerPrefs: {savedUrl}");
            }
        }

        // Set the input field to the determined URL
        if (videoServerUrl != null)
        {
            videoServerUrl.text = urlToUse;
        }

        statusText.text = "Ready to connect.";

        if (autoStartConnection)
        {
            targetRenderer = GetComponent<Renderer>();
            StartCoroutine(WebRTC.Update());
            string connectionUrl = FormatWebSocketUrl(urlToUse);
            statusText.text = $"Auto-connecting to: {connectionUrl}";
            StartCoroutine(ConnectToSignalingServer(connectionUrl));
        }
    }
    void Update()
    {
        // Dispatch WebSocket messages on main thread
#if !UNITY_WEBGL || UNITY_EDITOR
        ws?.DispatchMessageQueue();
#endif
    }

    public void StartStream()
    {
        targetRenderer = GetComponent<Renderer>();
        StartCoroutine(WebRTC.Update());
        string url = FormatWebSocketUrl(videoServerUrl.text);

        // Update the input field with the formatted URL
        videoServerUrl.text = url;

        // Save the URL to PlayerPrefs for persistence
        PlayerPrefs.SetString("videoServerUrl", url);
        PlayerPrefs.Save();

        statusText.text = $"Starting video stream from: {url}";
        StartCoroutine(ConnectToSignalingServer(url));
    }

    public IEnumerator ConnectToSignalingServer(String ipAddress)
    {
        Debug.Log($"Connecting to signaling server: {ipAddress}");
        ws = new WebSocket(ipAddress);
        ws.OnOpen += () =>
        {
            Debug.Log("WebSocket connected!");
            statusText.text = $"Connected to {ipAddress}! Registering as viewer...";
            // Register as viewer
            var msg = new SignalingMsg
            {
                type = "register",
                peerId = peerId,
                role = "viewer"
            };
            ws.SendText(JsonUtility.ToJson(msg));
        };
        ws.OnMessage += (bytes) =>
        {
            string json = System.Text.Encoding.UTF8.GetString(bytes);
            Debug.Log($"Received: {json}");
            HandleSignalingMessage(json);
        };
        ws.OnError += (error) =>
        {
            Debug.LogError($"WebSocket error: {error}");
            statusText.text = $"Connection error: {error}";
        };
        ws.OnClose += (code) =>
        {
            Debug.Log($"WebSocket closed: {code}");
            statusText.text = "Disconnected.";
        };
        yield return ws.Connect();
    }
    private void HandleSignalingMessage(string json)
    {
        var msg = JsonUtility.FromJson<SignalingMsg>(json);
        switch (msg.type)
        {
            case "offer":
                Debug.Log($"Received offer from: {msg.from}");
                statusText.text = "Received video offer, setting up connection...";
                publisherId = msg.from;
                if (msg.offer != null && !string.IsNullOrEmpty(msg.offer.sdp))
                {
                    StartCoroutine(HandleOffer(msg.offer.sdp));
                }
                else
                {
                    Debug.LogError("Received offer but SDP is null or empty!");
                    statusText.text = "Error: Invalid video offer received";
                }
                break;
            case "ice-candidate":
                if (pc != null && msg.candidate != null)
                {
                    Debug.Log($"Processing ICE candidate from {msg.from}: {msg.candidate.candidate}");
                    StartCoroutine(AddIceCandidate(msg.candidate));
                }
                break;
            case "error":
                Debug.LogError($"Signaling error: {msg.message}");
                statusText.text = $"Signaling error: {msg.message}";
                break;
            case "peer-disconnected":
                Debug.Log($"Peer disconnected: {msg.peerId}");
                statusText.text = "Video publisher disconnected.";
                break;
        }
    }
    private IEnumerator HandleOffer(string sdp)
    {
        // Create peer connection
        RTCConfiguration config = new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };
        pc = new RTCPeerConnection(ref config);
        receiveStream = new MediaStream();
        // Monitor connection state
        pc.OnConnectionStateChange = state =>
        {
            Debug.Log($"Connection state: {state}");
            if (state == RTCPeerConnectionState.Connected)
            {
                statusText.text = "Video stream connected!";
            }
            else if (state == RTCPeerConnectionState.Connecting)
            {
                statusText.text = "Establishing video connection...";
            }
            else if (state == RTCPeerConnectionState.Disconnected || state == RTCPeerConnectionState.Failed)
            {
                statusText.text = "Video connection lost.";
            }
        };
        // Handle ICE candidates
        pc.OnIceCandidate = candidate =>
        {
            if (candidate != null)
            {
                Debug.Log($"Sending ICE candidate to {publisherId}: {candidate.Candidate}");
                var candidateData = new IceCandidateData
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex ?? 0
                };
                var msg = new SignalingMsg
                {
                    type = "ice-candidate",
                    target = publisherId,
                    candidate = candidateData
                };
                ws.SendText(JsonUtility.ToJson(msg));
            }
        };
        // Handle incoming tracks
        pc.OnTrack = e =>
        {
            receiveStream.AddTrack(e.Track);
        };
        receiveStream.OnAddTrack = e =>
        {
            if (e.Track is VideoStreamTrack videoTrack)
            {
                videoTrack.OnVideoReceived += (tex) =>
                {
                    targetRenderer.material.mainTexture = tex;
                    statusText.text = "Video stream active!";
                };
            }
        };
        // Set remote description (offer)
        RTCSessionDescription offer = new RTCSessionDescription
        {
            type = RTCSdpType.Offer,
            sdp = sdp
        };
        var setRemoteOp = pc.SetRemoteDescription(ref offer);
        yield return setRemoteOp;
        // Create answer
        var answerOp = pc.CreateAnswer();
        yield return answerOp;
        if (answerOp.IsError)
        {
            Debug.LogError("CreateAnswer failed");
            statusText.text = "Error: Failed to create answer";
            yield break;
        }
        // Set local description (answer)
        var answer = answerOp.Desc;
        var setLocalOp = pc.SetLocalDescription(ref answer);
        yield return setLocalOp;
        // Send answer to publisher
        var answerMsg = new SignalingMsg
        {
            type = "answer",
            target = publisherId,
            answer = new SdpData
            {
                type = "answer",
                sdp = answer.sdp
            }
        };
        string answerJson = JsonUtility.ToJson(answerMsg);
        Debug.Log($"Sending answer: {answerJson}");
        ws.SendText(answerJson);
        statusText.text = "Answer sent, waiting for video...";
    }
    private IEnumerator AddIceCandidate(IceCandidateData candidateData)
    {
        RTCIceCandidateInit candidateInit = new RTCIceCandidateInit
        {
            candidate = candidateData.candidate,
            sdpMid = candidateData.sdpMid,
            sdpMLineIndex = candidateData.sdpMLineIndex
        };
        RTCIceCandidate candidate = new RTCIceCandidate(candidateInit);
        Debug.Log($"Adding ICE candidate: {candidateData.candidate}");
        pc.AddIceCandidate(candidate);
        yield return null;
    }

    private string FormatWebSocketUrl(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return defaultVideoServerUrl;
        }

        string url = input.Trim();

        // Quick numeric shortcuts for local dev: "1" -> localhost:3000, "2" -> localhost:3001
        if (url == "1")
        {
            return "ws://localhost:3000/ws";
        }
        if (url == "2")
        {
            return "ws://localhost:3001/ws";
        }

        // If it's already a complete WebSocket URL, return as is
        if (url.StartsWith("ws://") || url.StartsWith("wss://"))
        {
            return url;
        }

        // If it looks like an IP address or hostname without protocol
        // Add ws:// prefix and :3000/ws suffix if needed
        if (!url.Contains("://"))
        {
            url = "ws://" + url;
        }

        // Add port and path if not present
        if (!url.Contains(":3000") && !url.Contains("/ws"))
        {
            // Remove any trailing slash before adding our suffix
            url = url.TrimEnd('/');
            url += ":3000/ws";
        }

        return url;
    }

    void OnDestroy()
    {
        // Save current URL to PlayerPrefs on destroy
        if (videoServerUrl != null && !string.IsNullOrEmpty(videoServerUrl.text))
        {
            PlayerPrefs.SetString("videoServerUrl", videoServerUrl.text);
            PlayerPrefs.Save();
        }

        ws?.Close();
        pc?.Close();
        pc?.Dispose();
        receiveStream?.Dispose();
    }
}
