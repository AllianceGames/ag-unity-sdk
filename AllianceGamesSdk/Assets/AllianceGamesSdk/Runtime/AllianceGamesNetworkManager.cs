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

        private AllianceGamesNetworkTransport transport => NetworkConfig.NetworkTransport as AllianceGamesNetworkTransport;

        private void Awake()
        {
            // TODO interface for AG transports which defines the behavior needed for 
            // this network manager. For now we are using the WebSocketTransport 
            //base.NetworkConfig.NetworkTransport = new WebSocketTransport();
        }

        public async UniTask<AllianceGamesClient> StartClient(
            string connectionAddress,
            Buffer coordinatorPubkey,
            string sessionId,
            SignatureProvider signatureProvider,
            ILogger logger = null
        )
        {
            transport.SetClientConfig(connectionAddress, coordinatorPubkey, sessionId, signatureProvider, logger);
            return await StartClient(signatureProvider);
        }

        public async UniTask<AllianceGamesClient> StartLocalClient(
            string sessionId,
            string sessionData,
            string connectAddress,
            Buffer coordinatorPubkey,
            SignatureProvider signatureProvider,
            ILogger logger = null
        )
        {
            transport.SetClientConfig(sessionId, sessionData, connectAddress, coordinatorPubkey, signatureProvider, logger);
            return await StartClient(signatureProvider);
        }

        private async UniTask<AllianceGamesClient> StartClient(SignatureProvider signatureProvider)
        {
            NetworkConfig.ConnectionData = signatureProvider.PubKey.Bytes;
            var initCs = new UniTaskCompletionSource<bool>();
            transport.OnStarted += () => initCs.TrySetResult(true);
            transport.OnFailure += () => initCs.TrySetResult(false);
            transport.OnShutdown += () => OnShutdown?.Invoke();
            base.StartClient();
            var ret = await initCs.Task;
            return ret ? transport.Client : null;
        }

        public async UniTask<AllianceGamesServer> StartServer(
            UniTask<string> entrypoint,
            INodeConfig nodeConfig = null,
            ILogger logger = null
        )
        {
            transport.SetServerConfig(entrypoint, nodeConfig, logger);

            var initCs = new UniTaskCompletionSource<bool>();
            transport.OnStarted += () => initCs.TrySetResult(true);
            transport.OnFailure += () => initCs.TrySetResult(false);
            transport.OnShutdown += () => OnShutdown?.Invoke();
            base.StartServer();
            return await initCs.Task ? transport.Server : null;
        }
    }
}