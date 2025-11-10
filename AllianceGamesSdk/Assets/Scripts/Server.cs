using System;
using AllianceGamesSdk.Server;
using AllianceGamesSdk.Unity.Netcode;
using Chromia;
using Cysharp.Threading.Tasks;
using Serilog;
using Serilog.Sinks.Unity3D;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Profiling;
using Buffer = Chromia.Buffer;

class Server : MonoBehaviour
{
    private const string PRIV_KEY = "854D8402085EC5F737B1BE63FFD980981EED2A0DA5FAC6B4468CB1F176BA0321";

    [SerializeField]
    private AllianceGamesNetworkManager networkManager;
    [SerializeField]
    private NetworkObject profilerObject;

    private readonly UniTaskCompletionSource disconnectTcs = new();

    void Start()
    {
        StartInternal().Forget();
    }

    private async UniTaskVoid StartInternal()
    {

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Unity3D()
            .CreateLogger();
        Log.Logger = logger;

        var signatureProvider = SignatureProvider.Create(Buffer.From(PRIV_KEY));
        var config = new LocalTestNodeConfig(
            new Node(signatureProvider.PubKey, "http://localhost:40940"),
            signatureProvider,
            logger
        );

        var server = await networkManager.CreateServer(config);
        if (server == null)
        {
            logger.Error("Failed to create AG server");
            return;
        }

        server.OnClientConnect += address => logger.Information($"Client with pubkey {address.Parse()} connected.");
        server.OnClientDisconnect += _ => disconnectTcs.TrySetResult();


        await networkManager.StartServer(async () => await Do());
    }

    private async UniTask<string> Do()
    {
        Log.Logger.Information("Waiting for server");
        await UniTask.WaitForSeconds(2);
        Log.Logger.Information("Running server");
        Profiler.enabled = true;           // Start capturing
        try
        {
            var spawnedObj = Instantiate(profilerObject);
            spawnedObj.Spawn();
        }
        catch (Exception e)
        {
            Log.Logger.Error(e, "spawn");
        }
        await disconnectTcs.Task;
        Profiler.enabled = false;          // Stop capturing
        Log.Logger.Information("Shutting down server");
        return "";
    }
}