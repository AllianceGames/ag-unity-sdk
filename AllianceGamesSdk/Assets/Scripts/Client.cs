using System;
using AllianceGamesSdk.Client;
using AllianceGamesSdk.Unity.Netcode;
using Cysharp.Threading.Tasks;
using Serilog;
using Serilog.Sinks.Unity3D;
using UnityEngine;
using Chromia;
using Buffer = Chromia.Buffer;
using Unity.Netcode;

class Client : MonoBehaviour
{
    [SerializeField]
    private AllianceGamesNetworkManager networkManager;

    void Start()
    {
        Debug.Log("Start client");
        StartInternal().Forget();
    }

    private async UniTaskVoid StartInternal()
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Unity3D()
            .CreateLogger();
        Log.Logger = logger;

        AllianceGamesClient allianceGamesClient = await networkManager.CreateLocalClient(
            "test",
            "",
            "http://localhost:40940",
            Buffer.From("02897FAC9964FBDF97E6B83ECCBDE4A8D28729E0FB27059487D1B6B29F70B48767"),
            SignatureProvider.Create(),
            logger
        );

        if (allianceGamesClient == null)
        {
            logger.Error("Failed to create AG client");
            return;
        }

        var success = await networkManager.StartClient();
        if (!success)
        {
            logger.Error("Failed to start AG client");
            return;
        }

        var tcs = new UniTaskCompletionSource<NgoPingPongProfiler>();
        NgoPingPongProfiler.OnSpawn += obj => tcs.TrySetResult(obj);
        var profilerObject = await tcs.Task;
        await profilerObject.StartSending();
        networkManager.Shutdown();
    }
}