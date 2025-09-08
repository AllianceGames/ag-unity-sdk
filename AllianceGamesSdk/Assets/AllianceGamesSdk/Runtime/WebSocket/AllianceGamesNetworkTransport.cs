using AllianceGamesSdk.Client;
using AllianceGamesSdk.Common.Transport;
using AllianceGamesSdk.Server;
using AllianceGamesSdk.Unity;
using Chromia;
using Cysharp.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Unity.Netcode;
using UnityEngine;
using Buffer = Chromia.Buffer;
using ILogger = Serilog.ILogger;

namespace AllianceGamesSdk.Transport.Unity.Netcode
{
    public class AllianceGamesNetworkTransport : NetworkTransport
    {
        private enum Mode { None, Client, Server }
        private struct Message
        {
            public NetworkEvent Type;
            public ulong ClientId;
            public ArraySegment<byte> Payload;
        }

        internal event Action OnStarted;
        internal event Action OnFailure;
        internal event Action OnShutdown;

        private ITransport transport;

        internal AllianceGamesClient Client => client;
        internal AllianceGamesServer Server => server;

        private AllianceGamesClient client = null;
        private AllianceGamesServer server = null;
        private readonly System.Threading.Channels.Channel<Message> messageQueue
            = System.Threading.Channels.Channel.CreateUnbounded<Message>();
        private bool isStarted = false;
        private ILogger logger = null;

        // client
        internal IClientConfig clientConfig = null;

        // server
        internal Func<UniTask<string>> entrypoint = null;
        internal CancellationTokenSource serverCts = default;
        internal string sessionResult = null;
        internal INodeConfig nodeConfig = null;

        public uint WebSocketProtocolHeader => 40902u;
        public override ulong ServerClientId => 0;

        internal async UniTask<AllianceGamesClient> CreateClient(
            string connectAddress,
            Buffer coordinatorPubkey,
            string sessionId,
            SignatureProvider signatureProvider,
            ILogger logger = null
        )
        {
            clientConfig = new ClientConfig(
                sessionId,
                connectAddress,
                coordinatorPubkey,
                signatureProvider,
                new UniTaskRunner(),
                new UnityHttpClient(),
                logger
            );

            this.logger = logger;
            transport = WebSocketTransportFactory.Get(logger);
            return await CreateClient(clientConfig);
        }

        internal async UniTask<AllianceGamesClient> CreateClient(
            string sessionId,
            string sessionData,
            string connectAddress,
            Buffer coordinatorPubkey,
            SignatureProvider signatureProvider,
            ILogger logger
        )
        {
            clientConfig = new LocalTestClientConfig(
                sessionId,
                sessionData,
                signatureProvider.PubKey,
                new Uri(connectAddress),
                coordinatorPubkey,
                signatureProvider,
                new UniTaskRunner(),
                new UnityHttpClient(),
                logger
            );

            this.logger = logger;
            transport = WebSocketTransportFactory.Get(logger);
            return await CreateClient(clientConfig);
        }

        private async UniTask<AllianceGamesClient> CreateClient(IClientConfig clientConfig)
        {
            if (clientConfig is LocalTestClientConfig)
            {
                client = await AllianceGamesClient.CreateTest(
                    transport,
                    clientConfig as LocalTestClientConfig
                ).AsUniTask();
            }
            else
            {
                client = AllianceGamesClient.Create(transport, clientConfig);
            }

            return client;
        }

        internal async UniTask<AllianceGamesServer> CreateServer(
            INodeConfig nodeConfig,
            ILogger logger
        )
        {
            this.logger = logger;
            transport = WebSocketTransportFactory.Get(logger);

            if (nodeConfig is LocalTestNodeConfig)
            {
                server = await AllianceGamesServer.CreateTest(
                    transport,
                    nodeConfig as LocalTestNodeConfig,
                    new UnityHttpClient()
                ).AsUniTask();
            }
            else
            {
                server = AllianceGamesServer.Create(transport, nodeConfig);
            }

            return server;
        }

        public override async void Initialize(NetworkManager networkManager = null)
        {
            try
            {
                if (networkManager.IsClient)
                {
                    await StartClientInternal();
                }
                else if (networkManager.IsServer)
                {
                    await StartServerInternal();
                }
                else
                {
                    throw new InvalidOperationException("Mode not set");
                }
            }
            catch (Exception e)
            {
                LogError(e, "Failed to initialize WebSocketTransport");
            }
        }
        public override bool StartClient()
        {
            if (isStarted)
            {
                throw new InvalidOperationException("Socket already started");
            }
            else if (clientConfig == null)
            {
                throw new InvalidOperationException("Client config not set");
            }
            isStarted = true;

            return true;
        }

        public override bool StartServer()
        {
            if (isStarted)
            {
                throw new InvalidOperationException("Socket already started");
            }
            isStarted = true;

            return true;
        }

        private async UniTask StartClientInternal()
        {
            if (client == null)
            {
                OnFailure?.Invoke();
                return;
            }

            var connectMessage = new Message()
            {
                Type = NetworkEvent.Connect,
                ClientId = 0,
                Payload = null
            };
            if (!messageQueue.Writer.TryWrite(connectMessage))
            {
                LogError("Failed to write connectMessage message to queue");
            }

            client.RegisterMessageHandler(WebSocketProtocolHeader, (buffer) =>
            {

                var payload = new ArraySegment<byte>(buffer.Bytes);
                var message = new Message()
                {
                    Type = NetworkEvent.Data,
                    ClientId = ServerClientId,
                    Payload = payload
                };
                if (!messageQueue.Writer.TryWrite(message))
                {
                    LogError("Failed to write WebSocketProtocolHeader message to queue");
                }
            });

            var success = await client.Start(default).AsUniTask();
            if (success)
            {
                OnStarted?.Invoke();
            }
            else
            {
                OnFailure?.Invoke();
            }
        }

        private async UniTask StartServerInternal()
        {
            if (server == null)
            {
                OnFailure?.Invoke();
                return;
            }

            var startupTcs = new UniTaskCompletionSource();
            var connectedClients = new ConcurrentDictionary<Buffer, bool>();
            server.Clients.ToList().ForEach(client => connectedClients.TryAdd(client, false));
            server.RegisterMessageHandler(WebSocketProtocolHeader, async (pubKey, buffer) =>
            {
                await startupTcs.Task;

                var payload = new ArraySegment<byte>(buffer.Bytes);
                var sender = (ulong)server.Clients.ToList().IndexOf(pubKey) + 1;
                var message = new Message()
                {
                    Type = NetworkEvent.Data,
                    ClientId = sender,
                    Payload = payload
                };
                if (!messageQueue.Writer.TryWrite(message))
                {
                    LogError("Failed to write WebSocketProtocolHeader message to queue");
                }
            });
            server.OnClientConnect += (pubKey) =>
            {
                var message = new Message()
                {
                    Type = NetworkEvent.Connect,
                    ClientId = (ulong)server.Clients.ToList().IndexOf(pubKey) + 1,
                    Payload = null
                };
                if (!messageQueue.Writer.TryWrite(message))
                {
                    LogError("Failed to write OnClientConnect to queue");
                }

                connectedClients[pubKey] = true;
                if (connectedClients.Values.All(v => v))
                {
                    startupTcs.TrySetResult();
                }
            };
            server.OnClientDisconnect += (pubKey) =>
            {
                var message = new Message()
                {
                    Type = NetworkEvent.Disconnect,
                    ClientId = (ulong)server.Clients.ToList().IndexOf(pubKey) + 1,
                    Payload = null
                };
                if (!messageQueue.Writer.TryWrite(message))
                {
                    LogError("Failed to write OnClientDisconnect to queue");
                }
            };

            server.OnStarted += () => OnStarted?.Invoke();

            UniTask.RunOnThreadPool(() =>
                server.Run(() => System.Threading.Tasks.Task.FromResult("")),
                configureAwait: false
            ).Forget();
        }

        public override async void DisconnectLocalClient()
        {
            if (client != null)
            {
                await client.Stop(default).AsUniTask();
                client = null;
            }
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            // TODO what here? we don't really want to support that
        }

        // TODO needed?
        public override ulong GetCurrentRtt(ulong clientId)
        {
            return 0;
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            try
            {
                if (!messageQueue.Reader.TryRead(out var message))
                {
                    clientId = 0;
                    payload = default;
                    receiveTime = 0;
                    return NetworkEvent.Nothing;
                }

                clientId = message.ClientId;
                payload = message.Payload;
                receiveTime = Time.realtimeSinceStartup;
                return message.Type;
            }
            catch (Exception e)
            {
                LogError(e, "Failed to poll event");
                clientId = 0;
                payload = default;
                receiveTime = 0;
                return NetworkEvent.Nothing;
            }
        }

        public override async void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
            try
            {
                if (!isStarted)
                {
                    return;
                }

                var buffer = Buffer.From(payload);
                if (clientId == ServerClientId)
                {
                    await client.Send(WebSocketProtocolHeader, buffer, default).AsUniTask();
                }
                else
                {
                    var client = server.Clients.ToList()[(int)clientId - 1];
                    await server.Send(WebSocketProtocolHeader, client, buffer, default).AsUniTask();
                }
            }
            catch (Exception e)
            {
                LogError(e, "Failed to send message");
            }
        }

        public override async void Shutdown()
        {
            if (client != null)
            {
                await client.Stop(default).AsUniTask();
                client = null;
            }
            else if (server != null)
            {
                serverCts?.Cancel();
                server = null;
            }

            isStarted = false;
            OnShutdown?.Invoke();
        }

        private void LogError(string message)
        {
            LogError(null, message);
        }

        private void LogError(Exception e, string message)
        {
            if (logger != null)
            {
                logger.Error(e, message);
            }
            else
            {
                Debug.LogError($"{message}\n{e}");
            }
        }
    }
}
