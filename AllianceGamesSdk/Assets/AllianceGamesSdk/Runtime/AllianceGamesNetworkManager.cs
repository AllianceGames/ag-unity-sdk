using AllianceGamesSdk.Client;
using AllianceGamesSdk.Server;
using AllianceGamesSdk.Transport.Unity.Netcode;
using Chromia;
using Cysharp.Threading.Tasks;
using Serilog;
using System;
using Unity.Netcode;
using Buffer = Chromia.Buffer;

namespace AllianceGamesSdk.Unity.Netcode
{
    public class AllianceGamesNetworkManager : NetworkManager
    {
        public event Action OnShutdown;

        internal AllianceGamesNetworkTransport transport => NetworkConfig.NetworkTransport as AllianceGamesNetworkTransport;

        private void Awake()
        {
            // TODO interface for AG transports which defines the behavior needed for 
            // this network manager. For now we are using the WebSocketTransport 
            //base.NetworkConfig.NetworkTransport = new WebSocketTransport();
        }

        public async UniTask<AllianceGamesClient> CreateClient(
            string connectionAddress,
            Buffer coordinatorPubkey,
            string sessionId,
            SignatureProvider signatureProvider,
            ILogger logger = null
        )
        {
            return await transport.CreateClient(
                connectionAddress,
                coordinatorPubkey,
                sessionId,
                signatureProvider,
                logger
            );
        }

        public async UniTask<AllianceGamesClient> CreateLocalClient(
            string sessionId,
            string sessionData,
            string connectAddress,
            Buffer coordinatorPubkey,
            SignatureProvider signatureProvider,
            ILogger logger = null
        )
        {
            return await transport.CreateClient(
                sessionId,
                sessionData,
                connectAddress,
                coordinatorPubkey,
                signatureProvider,
                logger
            );
        }

        public async UniTask<bool> StartClient(SignatureProvider signatureProvider)
        {
            NetworkConfig.ConnectionData = signatureProvider.PubKey.Bytes;
            var initCs = new UniTaskCompletionSource<bool>();
            transport.OnStarted += () => initCs.TrySetResult(true);
            transport.OnFailure += () => initCs.TrySetResult(false);
            transport.OnShutdown += () => OnShutdown?.Invoke();
            base.StartClient();
            return await initCs.Task;
        }

        public async UniTask<AllianceGamesServer> CreateServer(
            INodeConfig nodeConfig = null,
            ILogger logger = null
        )
        {
            return await transport.CreateServer(nodeConfig, logger);
        }

        public async UniTask<bool> StartServer(Func<UniTask<string>> entrypoint)
        {
            transport.entrypoint = entrypoint;

            var initCs = new UniTaskCompletionSource<bool>();
            transport.OnStarted += () => initCs.TrySetResult(true);
            transport.OnFailure += () => initCs.TrySetResult(false);
            transport.OnShutdown += () => OnShutdown?.Invoke();
            base.StartServer();
            return await initCs.Task;
        }
    }
}