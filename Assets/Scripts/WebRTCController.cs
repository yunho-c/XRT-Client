using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.WebRTC;
using UnityEngine.Networking;
using TMPro;
using UnityEngine.UI;


[System.Serializable]
public class OrientationState
{
  public float yaw;
  public float pitch;
  public float roll;
  public float fov_x = 90.0f;
}


public class WebRTCController : MonoBehaviour
{
  [Header("Signaling Server")]
  [Tooltip("Default URL of signaling server (overriden by PlayerPrefs)")]
  public string serverUrl = "http://localhost:8080/offer";

  [Header("VR Camera")]
  [Tooltip("The VR camera to track")]
  public Camera vrCamera;

  [Header("Body Tracking")]
  // #if UNITY_ANDROID
  [Tooltip("The BodyPoseProvider to get body pose data from")]
  public BodyPoseProvider bodyPoseProvider;
  [Tooltip("The AprilTag tracker to get tag pose data from")]
  public QuestAprilTagTracker aprilTagTracker;
  // #endif

  [Header("UI Elements")]
  [SerializeField] private TMP_Text statusText;
  [SerializeField] private RenderTexture videoRenderTexture;
  [SerializeField] private Material videoMaterial;
  [SerializeField] private GameObject videoDisplayObject;
  [SerializeField] private TMP_InputField ipAddressInputField;

  [Header("WebRTC Settings")]
  [Tooltip("Enable to automatically start the WebRTC connection on start")]
  public bool autoStartConnection = false;
  [Tooltip("Enable to receive video stream")]
  public bool receiveVideo = true;
  [Tooltip("Default state for video stream visibility (overridden by PlayerPrefs)")]
  public bool videoStreamVisible = true;
  private const ulong HIGH_WATER_MARK = 1 * 1024 * 1024; // 1 MB

  private RTCPeerConnection pc;
  private RTCDataChannel cameraChannel;
  private RTCDataChannel bodyPoseChannel;
  private RTCDataChannel aprilTagChannel;
  private VideoStreamTrack videoTrack;
  private Coroutine _sendBodyPoseCoroutine;

  // Use a single volatile variable to store the latest pose data.
  // This avoids queuing and accumulating latency.
  private volatile byte[] _latestBodyPoseData = null;
  private readonly object _bodyPoseDataLock = new object();


  void Start()
  {
    string savedUrl = PlayerPrefs.GetString("serverUrl");
    if (!string.IsNullOrEmpty(savedUrl))
    {
      try
      {
        System.Uri uri = new System.Uri(savedUrl);
        if (!string.IsNullOrEmpty(uri.Host))
        {
          serverUrl = savedUrl;
        }
      }
      catch (System.Exception)
      {
        Debug.LogWarning($"Ignoring invalid serverUrl from PlayerPrefs: {savedUrl}");
      }
    }

    // Load video stream visibility setting
    bool savedVideoVisible = PlayerPrefs.GetInt("videoStreamVisible", videoStreamVisible ? 1 : 0) == 1;
    ToggleVideoStream(savedVideoVisible);

    statusText.text = "Ready to connect.";

    if (ipAddressInputField != null)
    {
      if (!string.IsNullOrEmpty(serverUrl))
      {
        // Extract IP address from the server URL
        try
        {
          System.Uri uri = new System.Uri(serverUrl);
          ipAddressInputField.text = uri.Host;
        }
        catch (System.Exception e)
        {
          Debug.LogError("Error parsing server URL: " + e.Message);
        }
      }
    }

    if (autoStartConnection)
    {
      StartConnection();
    }
  }

  void OnEnable()
  {
    // #if UNITY_ANDROID
    if (bodyPoseProvider != null)
    {
      bodyPoseProvider.OnPoseUpdated += OnBodyPoseUpdated;
    }
    if (aprilTagTracker != null)
    {
      aprilTagTracker.OnTagsDetected += OnAprilTagsDetected;
    }
    // #endif
  }

  void OnDisable()
  {
    // #if UNITY_ANDROID
    if (bodyPoseProvider != null)
    {
      bodyPoseProvider.OnPoseUpdated -= OnBodyPoseUpdated;
    }
    if (aprilTagTracker != null)
    {
      aprilTagTracker.OnTagsDetected -= OnAprilTagsDetected;
    }
    if (_sendBodyPoseCoroutine != null)
    {
      StopCoroutine(_sendBodyPoseCoroutine);
      _sendBodyPoseCoroutine = null;
    }
    // #endif
  }

  void Update()
  {
    if (cameraChannel != null && cameraChannel.ReadyState == RTCDataChannelState.Open)
    {
      SendOrientation();
    }
    // if (videoTrack != null && videoTrack.Enabled)
    // {
    //   // NOTE: WebRTC.Update() invokes texture update for video tracks
    //   // Debug.Log("Updated texture");
    //   WebRTC.Update();
    // }
  }

  public void SetServerIp(string ipAddress)
  {
    serverUrl = "http://" + ipAddress + ":8080/offer";
    PlayerPrefs.SetString("serverUrl", serverUrl);
    PlayerPrefs.Save();
    statusText.text = $"Server URL set to: {serverUrl}";
    Debug.Log("Server URL set to: " + serverUrl);
  }

  public void StartConnection()
  {
    if (pc != null && (pc.ConnectionState == RTCPeerConnectionState.Connected || pc.ConnectionState == RTCPeerConnectionState.Connecting))
    {
      Debug.LogWarning("WebRTC connection is already active or connecting.");
      return;
    }
    statusText.text = "Starting WebRTC...";
    StartCoroutine(StartWebRTC());
  }

  public void StopConnection()
  {
    if (cameraChannel != null)
    {
      cameraChannel.Close();
      cameraChannel = null;
    }
    if (bodyPoseChannel != null)
    {
      bodyPoseChannel.Close();
      bodyPoseChannel = null;
    }
    if (aprilTagChannel != null)
    {
      aprilTagChannel.Close();
      aprilTagChannel = null;
    }
    if (videoTrack != null)
    {
      videoTrack.Dispose();
      videoTrack = null;
    }
    if (_sendBodyPoseCoroutine != null)
    {
      StopCoroutine(_sendBodyPoseCoroutine);
      _sendBodyPoseCoroutine = null;
    }
    if (pc != null)
    {
      pc.Close();
      pc = null;
    }
    statusText.text = "Disconnected.";
    Debug.Log("WebRTC connection closed.");
  }

  public void ToggleConnection(bool isOn)
  {
    if (isOn)
    {
      StartConnection();
    }
    else
    {
      StopConnection();
    }
  }

public void ToggleVideoStream(bool isOn)
  {
    if (isOn)
    {
      if (videoTrack != null)
      {
        videoTrack.Enabled = true;
      }
      if (videoDisplayObject != null)
      {
        videoDisplayObject.SetActive(true);
      }
    }
    else
    {
      if (videoTrack != null)
      {
        videoTrack.Enabled = false;
      }
      if (videoDisplayObject != null)
      {
        videoDisplayObject.SetActive(false);
      }
    }

    // Save the video stream visibility setting
    PlayerPrefs.SetInt("videoStreamVisible", isOn ? 1 : 0);
    PlayerPrefs.Save();
  }

  // #if UNITY_ANDROID
  private void OnBodyPoseUpdated(BodyPoseProvider.PoseData poseData)
  {
    // This method is called from the body tracking thread.
    // We serialize the data and store it in a volatile variable.
    // The sending coroutine on the main thread will pick it up.
    if (poseData.bones != null && poseData.bones.Count > 0)
    {
      byte[] binaryData = SerializePoseData(poseData);
      lock (_bodyPoseDataLock)
      {
        _latestBodyPoseData = binaryData;
      }
    }
  }

  private void OnAprilTagsDetected(QuestAprilTagTracker.TagResult[] tags)
  {
      if (aprilTagChannel != null && aprilTagChannel.ReadyState == RTCDataChannelState.Open)
      {
          if (tags == null || tags.Length == 0)
          {
              // Option A: Could send an empty payload to indicate no tags, 
              // or Option B: just drop the frame. We will send empty sequence so the receiver knows the tag is lost.
              byte[] emptyData = SerializeAprilTagData(new QuestAprilTagTracker.TagResult[0]);
              if (aprilTagChannel.BufferedAmount < HIGH_WATER_MARK)
              {
                  aprilTagChannel.Send(emptyData);
              }
              return;
          }

          byte[] binaryData = SerializeAprilTagData(tags);
          if (binaryData != null && aprilTagChannel.BufferedAmount < HIGH_WATER_MARK)
          {
              aprilTagChannel.Send(binaryData);
          }
      }
  }

  // #endif

  private IEnumerator StartWebRTC()
  {
    CreatePeerConnection();

    // Add video transceiver if video is enabled
    if (receiveVideo)
    {
      var videoTransceiver = pc.AddTransceiver(TrackKind.Video);
      videoTransceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;
    }

    // Create data channel
    cameraChannel = pc.CreateDataChannel("camera");
    SetupDataChannelEvents(cameraChannel);

    // Create pose data channel: Unreliable and Unordered
    // This is critical for low-latency real-time data.
    var bodyPoseChannelOptions = new RTCDataChannelInit()
    {
      ordered = false,
      maxRetransmits = 0
    };
    bodyPoseChannel = pc.CreateDataChannel("body_pose", bodyPoseChannelOptions);
    SetupBodyPoseDataChannel(bodyPoseChannel);

    aprilTagChannel = pc.CreateDataChannel("apriltag_pose", bodyPoseChannelOptions);
    SetupDataChannelEvents(aprilTagChannel);

    // Create offer
    var offer = pc.CreateOffer();
    yield return offer;

    if (offer.IsError)
    {
      Debug.LogError("Error creating offer: " + offer.Error.message);
      yield break;
    }

    var desc = offer.Desc;
    var localDescOp = pc.SetLocalDescription(ref desc);
    yield return localDescOp;

    if (localDescOp.IsError)
    {
      Debug.LogError("Error setting local description: " + localDescOp.Error.message);
      yield break;
    }

    // Send offer to server
    statusText.text = $"Sending offer to {serverUrl}...";
    Debug.Log($"[WebRTCController] Initiating WebRTC signaling to URL: {serverUrl}");
    SignalingMessage offerMessage = new SignalingMessage { type = "offer", sdp = desc.sdp };
    string jsonOffer = JsonUtility.ToJson(offerMessage);

    using (UnityWebRequest www = new UnityWebRequest(serverUrl, "POST"))
    {
      byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonOffer);
      www.uploadHandler = new UploadHandlerRaw(bodyRaw);
      www.downloadHandler = new DownloadHandlerBuffer();
      www.SetRequestHeader("Content-Type", "application/json");

      yield return www.SendWebRequest();

      if (www.result != UnityWebRequest.Result.Success)
      {
        Debug.LogError("Error sending offer: " + www.error);
        statusText.text = $"Error sending offer: {www.error}";
        yield break;
      }

      statusText.text = "Offer sent, waiting for answer...";
      string jsonAnswer = www.downloadHandler.text;
      SignalingMessage answerMessage = JsonUtility.FromJson<SignalingMessage>(jsonAnswer);
      StartCoroutine(OnGotAnswer(answerMessage.sdp));
    }
  }

  private void CreatePeerConnection()
  {
    var configuration = GetSelectedSdpSemantics();
    pc = new RTCPeerConnection(ref configuration);
    Debug.Log("Peer Connection created.");

    pc.OnConnectionStateChange = state =>
    {
      Debug.Log("Connection state changed to: " + state);
      if (state == RTCPeerConnectionState.Connected)
      {
        UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
              statusText.text = "Peers connected!";
            });
      }
    };

    pc.OnDataChannel = channel =>
    {
      Debug.Log($"Data Channel received: {channel.Label}!");
      if (channel.Label == "camera")
      {
        cameraChannel = channel;
      }
      else if (channel.Label == "body_pose")
      {
        bodyPoseChannel = channel;
        SetupBodyPoseDataChannel(channel);

      }
      else if (channel.Label == "apriltag_pose")
      {
        aprilTagChannel = channel;
        SetupDataChannelEvents(channel);
      }
      else if (channel.Label == "haptics")
      {
        // Notify WebRTCHapticReceiver if it exists
        WebRTCHapticReceiver hapticReceiver = FindObjectOfType<WebRTCHapticReceiver>();
        if (hapticReceiver != null)
        {
          hapticReceiver.OnHapticsChannelReceived(channel);
        }
        else
        {
          SetupDataChannelEvents(channel);
        }
      }
      else
      {
        SetupDataChannelEvents(channel);
      }
    };

    // The client receives the video stream
    pc.OnTrack = (RTCTrackEvent e) =>
    {
      if (e.Track.Kind == TrackKind.Video)
      {
        Debug.Log(e.Track);
        Debug.Log("Video channel created.");
        if (e.Track is VideoStreamTrack track)
        {
          videoTrack = track;
          videoTrack.OnVideoReceived += (texture) =>
              {
                Debug.Log("Received first video frame (and set texture).");
                videoMaterial.mainTexture = texture;
                StartCoroutine(WebRTC.Update());
              };
        }
      }
    };
  }

  private IEnumerator OnGotAnswer(string sdp)
  {
    var remoteDesc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
    var remoteDescOp = pc.SetRemoteDescription(ref remoteDesc);
    yield return remoteDescOp;

    if (remoteDescOp.IsError)
    {
      Debug.LogError("Error setting remote description on answer: " + remoteDescOp.Error.message);
    }
  }

  private void SetupDataChannelEvents(RTCDataChannel channel)
  {
    channel.OnOpen = () =>
    {
      Debug.Log($"{channel.Label} Channel is open!");
      UnityMainThreadDispatcher.Instance().Enqueue(() =>
          {
            statusText.text = $"{channel.Label} channel open.";
          });
    };

    channel.OnClose = () =>
    {
      Debug.Log($"{channel.Label} Channel is closed!");
      UnityMainThreadDispatcher.Instance().Enqueue(() =>
          {
            statusText.text = $"{channel.Label} channel closed.";
          });
    };

    channel.OnMessage = bytes =>
    {
      // Handle incoming messages if needed
      Debug.Log($"Received message on {channel.Label} channel: {System.Text.Encoding.UTF8.GetString(bytes)}");
    };
  }

  private void SetupBodyPoseDataChannel(RTCDataChannel channel)
  {
    SetupDataChannelEvents(channel);

    channel.OnOpen = () =>
    {
      Debug.Log($"{channel.Label} Channel is open!");
      UnityMainThreadDispatcher.Instance().Enqueue(() =>
          {
            statusText.text = $"{channel.Label} channel open.";
          });
      if (_sendBodyPoseCoroutine == null)
      {
        _sendBodyPoseCoroutine = StartCoroutine(SendBodyPoseCoroutine());
      }
    };
  }

  private IEnumerator SendBodyPoseCoroutine()
  {
    // Send at a fixed rate (e.g., 90 Hz) to control the data flow.
    var wait = new WaitForSeconds(1.0f / 90.0f);

    while (true)
    {
      byte[] dataToSend = null;
      lock (_bodyPoseDataLock)
      {
        // Check if there is new data since the last send.
        if (_latestBodyPoseData != null)
        {
          dataToSend = _latestBodyPoseData;
          _latestBodyPoseData = null; // Consume the data to avoid re-sending.
        }
      }

      // Only send if there's new data and the network buffer is not congested.
      // This prevents building up a queue and causing latency.
      if (dataToSend != null && bodyPoseChannel.BufferedAmount < HIGH_WATER_MARK)
      {
        bodyPoseChannel.Send(dataToSend);
      }
      // If the buffer is full or there's no new data, we effectively "drop" the frame,
      // prioritizing low latency and sending the most recent data in the next cycle.

      yield return wait;
    }
  }

  private byte[] SerializePoseData(BodyPoseProvider.PoseData poseData)
  {
    using (var memoryStream = new MemoryStream())
    {
      using (var writer = new BinaryWriter(memoryStream))
      {
        writer.Write(poseData.bones.Count);
        foreach (var bone in poseData.bones)
        {
          writer.Write((int)bone.id);

          writer.Write(bone.position.x);
          writer.Write(bone.position.y);
          writer.Write(bone.position.z);

          writer.Write(bone.rotation.x);
          writer.Write(bone.rotation.y);
          writer.Write(bone.rotation.z);
          writer.Write(bone.rotation.w);
        }
      }
      return memoryStream.ToArray();
    }
  }

  private byte[] SerializeAprilTagData(QuestAprilTagTracker.TagResult[] tags)
  {
    using (var memoryStream = new MemoryStream())
    {
      using (var writer = new BinaryWriter(memoryStream))
      {
        writer.Write(tags.Length);
        foreach (var tag in tags)
        {
          writer.Write((int)tag.id);

          writer.Write(tag.position.x);
          writer.Write(tag.position.y);
          writer.Write(tag.position.z);

          writer.Write(tag.rotation.x);
          writer.Write(tag.rotation.y);
          writer.Write(tag.rotation.z);
          writer.Write(tag.rotation.w);
        }
      }
      return memoryStream.ToArray();
    }
  }

  private void SendOrientation()
  {
    if (vrCamera != null)
    {
      OrientationState state = new OrientationState
      {
        yaw = vrCamera.transform.eulerAngles.y,
        pitch = -vrCamera.transform.eulerAngles.x, // Invert pitch for correct mapping
        roll = vrCamera.transform.eulerAngles.z
      };
      string jsonState = JsonUtility.ToJson(state);
      cameraChannel.Send(jsonState);
    }
  }

  private void OnApplicationQuit()
  {
    PlayerPrefs.SetString("serverUrl", serverUrl);
    PlayerPrefs.Save();
    StopConnection();
  }

  private static RTCConfiguration GetSelectedSdpSemantics()
  {
    return new RTCConfiguration
    {
      iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
    };
  }
}
