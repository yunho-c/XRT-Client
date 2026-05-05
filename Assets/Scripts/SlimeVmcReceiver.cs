using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

public class SlimeVmcReceiver : MonoBehaviour
{
    [Header("Network")]
    [SerializeField] private int listenPort = 39539;
    [SerializeField] private bool acceptAnySourceIp = true;
    [SerializeField] private string allowedSourceIp = "";

    [Header("Runtime")]
    [SerializeField] private float staleTimeoutSeconds = 0.35f;

    public event Action<IReadOnlyDictionary<SlimeTrackerRole, SlimeTrackerSample>> OnTrackerFrame;

    public float LastPacketTime { get; private set; }
    public int PacketCount { get; private set; }
    public int DroppedPacketCount { get; private set; }
    public bool IsStreamHealthy => (GetNowSeconds() - LastPacketTime) <= staleTimeoutSeconds;

    private readonly object _lock = new object();
    private readonly Dictionary<SlimeTrackerRole, SlimeTrackerSample> _latestSamples = new Dictionary<SlimeTrackerRole, SlimeTrackerSample>();
    private readonly Dictionary<SlimeTrackerRole, SlimeTrackerSample> _mainThreadSnapshot = new Dictionary<SlimeTrackerRole, SlimeTrackerSample>();
    private UdpClient _udpClient;
    private Thread _receiveThread;
    private volatile bool _running;
    private static readonly double TickToSeconds = 1.0 / Stopwatch.Frequency;

    private void OnEnable()
    {
        StartReceiver();
    }

    private void OnDisable()
    {
        StopReceiver();
    }

    private void Update()
    {
        float now = GetNowSeconds();
        lock (_lock)
        {
            _mainThreadSnapshot.Clear();
            foreach (KeyValuePair<SlimeTrackerRole, SlimeTrackerSample> kvp in _latestSamples)
            {
                SlimeTrackerSample sample = kvp.Value;
                if ((now - sample.timestamp) <= staleTimeoutSeconds)
                {
                    _mainThreadSnapshot[kvp.Key] = sample;
                }
            }
        }

        if (_mainThreadSnapshot.Count > 0)
        {
            OnTrackerFrame?.Invoke(_mainThreadSnapshot);
        }
    }

    public string GetDebugStatus()
    {
        return $"SlimeVMC port={listenPort}, packets={PacketCount}, dropped={DroppedPacketCount}, healthy={IsStreamHealthy}";
    }

    private void StartReceiver()
    {
        if (_running)
        {
            return;
        }

        try
        {
            _udpClient = new UdpClient(listenPort);
            _running = true;
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Start();
            Debug.Log($"[SlimeVmcReceiver] Listening on UDP:{listenPort}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SlimeVmcReceiver] Failed to start receiver: {ex.Message}");
            _running = false;
        }
    }

    private void StopReceiver()
    {
        _running = false;

        try
        {
            _udpClient?.Close();
            _udpClient = null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SlimeVmcReceiver] Close error: {ex.Message}");
        }

        if (_receiveThread != null && _receiveThread.IsAlive)
        {
            _receiveThread.Join(100);
        }

        _receiveThread = null;
    }

    private void ReceiveLoop()
    {
        while (_running)
        {
            try
            {
                IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _udpClient.Receive(ref remoteEndpoint);
                if (!acceptAnySourceIp && !string.IsNullOrWhiteSpace(allowedSourceIp) && remoteEndpoint.Address.ToString() != allowedSourceIp)
                {
                    DroppedPacketCount++;
                    continue;
                }

                PacketCount++;
                LastPacketTime = GetNowSeconds();
                ParseOscPacket(data);
            }
            catch (SocketException)
            {
                // Socket closed while shutting down.
            }
            catch (ObjectDisposedException)
            {
                // Socket closed while shutting down.
            }
            catch (Exception ex)
            {
                DroppedPacketCount++;
                Debug.LogWarning($"[SlimeVmcReceiver] Packet parse error: {ex.Message}");
            }
        }
    }

    private void ParseOscPacket(byte[] data)
    {
        if (data == null || data.Length < 4)
        {
            return;
        }

        if (StartsWithBundle(data))
        {
            ParseOscBundle(data);
            return;
        }

        ParseOscMessage(data, 0, data.Length);
    }

    private bool StartsWithBundle(byte[] data)
    {
        return data.Length > 7
               && data[0] == (byte)'#'
               && data[1] == (byte)'b'
               && data[2] == (byte)'u'
               && data[3] == (byte)'n'
               && data[4] == (byte)'d'
               && data[5] == (byte)'l'
               && data[6] == (byte)'e';
    }

    private void ParseOscBundle(byte[] data)
    {
        int offset = 16; // "#bundle\0" + timetag(8)
        while (offset + 4 <= data.Length)
        {
            int elementSize = ReadInt32BigEndian(data, offset);
            offset += 4;
            if (elementSize <= 0 || offset + elementSize > data.Length)
            {
                return;
            }

            ParseOscMessage(data, offset, elementSize);
            offset += elementSize;
        }
    }

    private void ParseOscMessage(byte[] data, int start, int length)
    {
        int index = start;
        string address = ReadOscString(data, ref index, start + length);
        if (string.IsNullOrEmpty(address))
        {
            return;
        }

        string typeTag = ReadOscString(data, ref index, start + length);
        if (string.IsNullOrEmpty(typeTag) || typeTag[0] != ',')
        {
            return;
        }

        if (address != "/VMC/Ext/Tra/Pos")
        {
            return;
        }

        // Expected signature: ,sfffffff
        if (typeTag.Length < 9)
        {
            return;
        }

        string trackerName = ReadOscString(data, ref index, start + length);
        if (string.IsNullOrEmpty(trackerName))
        {
            return;
        }

        Vector3 position = new Vector3(
            ReadFloat32BigEndian(data, ref index),
            ReadFloat32BigEndian(data, ref index),
            ReadFloat32BigEndian(data, ref index));

        Quaternion rotation = new Quaternion(
            ReadFloat32BigEndian(data, ref index),
            ReadFloat32BigEndian(data, ref index),
            ReadFloat32BigEndian(data, ref index),
            ReadFloat32BigEndian(data, ref index));

        SlimeTrackerRole role = InferRole(trackerName);
        if (role == SlimeTrackerRole.Unknown)
        {
            return;
        }

        SlimeTrackerSample sample = new SlimeTrackerSample
        {
            role = role,
            position = position,
            rotation = rotation,
            timestamp = GetNowSeconds()
        };

        lock (_lock)
        {
            _latestSamples[role] = sample;
        }
    }

    private SlimeTrackerRole InferRole(string trackerName)
    {
        string n = trackerName.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

        if (n.Contains("leftshoulder"))
        {
            return SlimeTrackerRole.LeftShoulder;
        }
        if (n.Contains("rightshoulder"))
        {
            return SlimeTrackerRole.RightShoulder;
        }
        if (n.Contains("leftupperarm") || n.Contains("leftarmupper") || n.Contains("leftbicep"))
        {
            return SlimeTrackerRole.LeftUpperArm;
        }
        if (n.Contains("rightupperarm") || n.Contains("rightarmupper") || n.Contains("rightbicep"))
        {
            return SlimeTrackerRole.RightUpperArm;
        }
        if (n.Contains("leftlowerarm") || n.Contains("leftarmlower") || n.Contains("leftforearm"))
        {
            return SlimeTrackerRole.LeftLowerArm;
        }
        if (n.Contains("rightlowerarm") || n.Contains("rightarmlower") || n.Contains("rightforearm"))
        {
            return SlimeTrackerRole.RightLowerArm;
        }
        if (n.Contains("hip") && !n.Contains("lefthip") && !n.Contains("righthip"))
        {
            return SlimeTrackerRole.Hip;
        }
        if (n.Contains("waist"))
        {
            return SlimeTrackerRole.Waist;
        }
        if (n.Contains("chest") || n.Contains("spine"))
        {
            return SlimeTrackerRole.Chest;
        }
        if (n.Contains("leftthigh") || n.Contains("thighleft") || n.Contains("leftupleg"))
        {
            return SlimeTrackerRole.LeftThigh;
        }
        if (n.Contains("rightthigh") || n.Contains("thighright") || n.Contains("rightupleg"))
        {
            return SlimeTrackerRole.RightThigh;
        }
        if (n.Contains("leftankle") || n.Contains("ankleleft"))
        {
            return SlimeTrackerRole.LeftAnkle;
        }
        if (n.Contains("rightankle") || n.Contains("ankleright"))
        {
            return SlimeTrackerRole.RightAnkle;
        }
        if (n.Contains("leftfoot") && !n.Contains("toes"))
        {
            return SlimeTrackerRole.LeftFoot;
        }
        if (n.Contains("rightfoot") && !n.Contains("toes"))
        {
            return SlimeTrackerRole.RightFoot;
        }
        if (n.Contains("head"))
        {
            return SlimeTrackerRole.Head;
        }

        return SlimeTrackerRole.Unknown;
    }

    private static string ReadOscString(byte[] data, ref int index, int endExclusive)
    {
        if (index >= endExclusive)
        {
            return string.Empty;
        }

        int start = index;
        while (index < endExclusive && data[index] != 0)
        {
            index++;
        }

        if (index >= endExclusive)
        {
            return string.Empty;
        }

        string s = Encoding.ASCII.GetString(data, start, index - start);
        index++; // skip null terminator
        while ((index % 4) != 0 && index < endExclusive)
        {
            index++;
        }

        return s;
    }

    private static int ReadInt32BigEndian(byte[] data, int offset)
    {
        return (data[offset] << 24)
               | (data[offset + 1] << 16)
               | (data[offset + 2] << 8)
               | data[offset + 3];
    }

    private static float ReadFloat32BigEndian(byte[] data, ref int index)
    {
        byte b0 = data[index];
        byte b1 = data[index + 1];
        byte b2 = data[index + 2];
        byte b3 = data[index + 3];
        index += 4;
        byte[] bytes = BitConverter.IsLittleEndian
            ? new[] { b3, b2, b1, b0 }
            : new[] { b0, b1, b2, b3 };
        return BitConverter.ToSingle(bytes, 0);
    }

    private static float GetNowSeconds()
    {
        return (float)(Stopwatch.GetTimestamp() * TickToSeconds);
    }
}
