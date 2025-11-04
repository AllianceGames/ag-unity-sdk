using System.Collections;
using UnityEngine;
using Unity.Netcode;
using Cysharp.Threading.Tasks;
using AllianceGamesSdk.Common.Profiler;
using System;
using Serilog;

public class NgoPingPongProfiler : NetworkBehaviour
{
    public static event Action<NgoPingPongProfiler> OnSpawn;

    [Header("Ping settings")]
    [Tooltip("How often to send a ping from the client (seconds).")]
    public float intervalSeconds = 0.25f;

    [Tooltip("How many pings to send. Set 0 for infinite until disabled.")]
    public int maxPings = 10;

    [Tooltip("Optional payload bytes to include to test fragmentation/throughput.")]
    public int payloadBytes = 0;

    [Tooltip("Automatically start the ping loop when client spawns.")]
    public bool autoStartOnClient = true;

    // Profiler markers
    private static readonly TimingReporter MarkSendClient = new("Unity/NgoPingPongProfiler/Send(Client)");
    private static readonly TimingReporter MarkRecvServer = new("Unity/NgoPingPongProfiler/Recv(Server)");
    private static readonly TimingReporter MarkSendServer = new("Unity/NgoPingPongProfiler/Send(ServerEcho)");
    private static readonly TimingReporter MarkRecvClient = new("Unity/NgoPingPongProfiler/Recv(ClientEcho)");

    // Simple running stats
    private int _sentCount;
    private int _recvCount;
    private double _sumRttMs, _sumCsMs, _sumScMs;
    private double _minRtt = double.MaxValue, _maxRtt = 0.0;
    private double _minCs = double.MaxValue, _maxCs = 0.0;
    private double _minSc = double.MaxValue, _maxSc = 0.0;

    private byte[] _payload; // optional payload buffer

    private Coroutine _loop;
    private UniTaskCompletionSource sendCompletion = new();

    void Awake()
    {
        Application.targetFrameRate = 300;
        QualitySettings.vSyncCount = 0;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        OnSpawn?.Invoke(this);
    }

    public async UniTask StartSending()
    {
        _loop = StartCoroutine(PingLoop());
        await sendCompletion.Task;
    }

    public void Stop()
    {
        StopCoroutine(_loop);
        _loop = null;
        Debug.Log("Ping loop stopped.");
    }

    private IEnumerator PingLoop()
    {
        Debug.Log($"[PingPong] Starting ping loop. interval={intervalSeconds}s, payload={payloadBytes}B, maxPings={(maxPings == 0 ? "∞" : maxPings)}");
        _sentCount = _recvCount = 0;
        _sumRttMs = _sumCsMs = _sumScMs = 0;
        _minRtt = _minCs = _minSc = double.MaxValue;
        _maxRtt = _maxCs = _maxSc = 0.0;

        while (maxPings == 0 || _sentCount < maxPings)
        {
            SendPing();
            _sentCount++;
            yield return new WaitForSeconds(intervalSeconds);
        }

        Debug.Log("[PingPong] Completed sending pings.");
        _loop = null;
        sendCompletion.TrySetResult();
    }

    private void SendPing()
    {
        if (!IsClient) return;

        try
        {
            using (MarkSendClient.Auto())
            {
                // Use server-synced time on the client for skew-free measurements
                double tClientSendOnServerClock = NetworkManager.ServerTime.Time;

                // Optional: put a tiny header then payload length bytes
                var payload = _payload; // null or preallocated
                PingServerRpc(tClientSendOnServerClock, payload);
            }
        }
        catch (Exception e)
        {
            Log.Logger.Error(e, "SendPing");
        }
    }

    // -------- Server side --------

    [ServerRpc(RequireOwnership = false)]
    private void PingServerRpc(double tClientSendOnServerClock, byte[] payload)
    {
        using (MarkRecvServer.Auto())
        {
            double tServerRecv = NetworkManager.ServerTime.Time;

            // Compute one-way client->server on the server clock
            double oneWayCsMs = (tServerRecv - tClientSendOnServerClock) * 1000.0;

            // Immediately echo with the server's send timestamp
            using (MarkSendServer.Auto())
            {
                double tServerSend = NetworkManager.ServerTime.Time;
                PongClientRpc(tClientSendOnServerClock, tServerRecv, tServerSend);
                // Note: We don't bounce the payload back to keep the echo minimal.
            }

            // Optional: server-side log for C->S
            // Debug.Log($"[PingPong][Server] C→S one-way ≈ {oneWayCsMs:0.0} ms");
        }
    }

    // -------- Client side echo receive --------

    [ClientRpc]
    private void PongClientRpc(double tClientSendOnServerClock, double tServerRecv, double tServerSend)
    {
        using (MarkRecvClient.Auto())
        {
            double tClientRecvOnServerClock = NetworkManager.ServerTime.Time;

            // On client we can compute:
            //   S→C one-way: from server send to client receive (both on server clock)
            double oneWayScMs = (tClientRecvOnServerClock - tServerSend) * 1000.0;

            //   C→S one-way: from client send to server recv (pure server clock domain)
            double oneWayCsMs = (tServerRecv - tClientSendOnServerClock) * 1000.0;

            //   RTT: from client send to client recv (server clock domain)
            double rttMs = (tClientRecvOnServerClock - tClientSendOnServerClock) * 1000.0;

            _recvCount++;
            _sumRttMs += rttMs; _sumCsMs += oneWayCsMs; _sumScMs += oneWayScMs;
            _minRtt = Mathf.Min((float)_minRtt, (float)rttMs);
            _maxRtt = Mathf.Max((float)_maxRtt, (float)rttMs);
            _minCs = Mathf.Min((float)_minCs, (float)oneWayCsMs);
            _maxCs = Mathf.Max((float)_maxCs, (float)oneWayCsMs);
            _minSc = Mathf.Min((float)_minSc, (float)oneWayScMs);
            _maxSc = Mathf.Max((float)_maxSc, (float)oneWayScMs);

            // Per-ping concise log
            Debug.Log($"[PingPong] C→S {oneWayCsMs:0.0} ms | S→C {oneWayScMs:0.0} ms | RTT {rttMs:0.0} ms  (n={_recvCount})");

            // Every 20 pings, print a small summary
            if (_recvCount % 20 == 0)
            {
                double n = _recvCount;
                Debug.Log(
                    $"[PingPong][Summary last {Mathf.Min(_recvCount, 20)} shown / total {n}] " +
                    $"C→S avg {(_sumCsMs / n):0.0} ms (min {_minCs:0.0}, max {_maxCs:0.0}) | " +
                    $"S→C avg {(_sumScMs / n):0.0} ms (min {_minSc:0.0}, max {_maxSc:0.0}) | " +
                    $"RTT avg {(_sumRttMs / n):0.0} ms (min {_minRtt:0.0}, max {_maxRtt:0.0})");
            }
        }
    }
}
