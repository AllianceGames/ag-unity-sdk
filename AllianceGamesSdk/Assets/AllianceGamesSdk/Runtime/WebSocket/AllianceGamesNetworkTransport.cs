using AllianceGamesSdk.Client;
using AllianceGamesSdk.Common.Transport;
using AllianceGamesSdk.Common.Profiler;
using AllianceGamesSdk.Server;
using AllianceGamesSdk.Unity;
using AllianceGamesSdk.Unity.Netcode;
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
using Serilog;

namespace AllianceGamesSdk.Transport.Unity.Netcode
{
    public class AllianceGamesNetworkTransport : NetworkTransport
    {
        private struct Message
        {
            public NetworkEvent Type;
            public ulong ClientId;
            public ArraySegment<byte> Payload;
        }

        // Profiler markers for automatic timing tracking
        // private static readonly TimingReporter ProfilerPollEvent = new("Unity/AllianceGamesNetworkTransport/PollEvent");
        // private static readonly TimingReporter ProfilerSend = new("Unity/AllianceGamesNetworkTransport/Send");
        // private static readonly TimingReporter ProfilerSendAsync = new("Unity/AllianceGamesNetworkTransport/SendAsync");
        // private static readonly TimingReporter ProfilerWriteMessage = new("Unity/AllianceGamesNetworkTransport/WriteMessage");

        internal event Action OnStarted;
        internal event Action OnFailure;
        internal event Action OnShutdown;

        private ITransport transport;

        internal AllianceGamesClient Client => client;
        internal AllianceGamesServer Server => server;

        private AllianceGamesClient client = null;
        private AllianceGamesServer server = null;
        private System.Threading.Channels.Channel<Message> receiveQueue = null;
        private bool isStarted = false;
        private ILogger logger = null;
        private CancellationTokenSource senderCts = null;

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
            Buffer identifierPubKey,
            SignatureProvider signatureProvider,
            ILogger logger = null
        )
        {
            clientConfig = new ClientConfig(
                sessionId,
                connectAddress,
                coordinatorPubkey,
                identifierPubKey,
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
            this.logger = logger ?? nodeConfig?.Logger ?? Log.Logger;
            transport = WebSocketTransportFactory.Get(this.logger);

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
                receiveQueue = System.Threading.Channels.Channel.CreateUnbounded<Message>();
                senderCts = new CancellationTokenSource();

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

            client.RegisterMessageHandler(WebSocketProtocolHeader, (buffer) =>
            {
                var bytes = buffer.Bytes;
                if (bytes == null || bytes.Length == 0)
                {
                    return;
                }
                var message = new Message()
                {
                    Type = NetworkEvent.Data,
                    ClientId = ServerClientId,
                    Payload = buffer.Bytes
                };
                WriteMessage(message);
            });

            var success = await client.Start(default).AsUniTask();
            if (success)
            {
                var connectMessage = new Message()
                {
                    Type = NetworkEvent.Connect,
                    ClientId = 0,
                    Payload = null
                };
                WriteMessage(connectMessage);
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
            foreach (var client in server.Clients)
            {
                connectedClients.TryAdd(client, false);
            }
            server.RegisterMessageHandler(WebSocketProtocolHeader, async (pubKey, buffer) =>
            {
                var bytes = buffer.Bytes;
                if (bytes == null || bytes.Length == 0)
                {
                    return;
                }

                await startupTcs.Task;

                var message = new Message()
                {
                    Type = NetworkEvent.Data,
                    ClientId = server.GetClientId(pubKey),
                    Payload = buffer.Bytes
                };
                WriteMessage(message);
            });
            server.OnClientConnect += (pubKey) =>
            {
                var message = new Message()
                {
                    Type = NetworkEvent.Connect,
                    ClientId = server.GetClientId(pubKey),
                    Payload = null
                };
                var connectedClientss = NetworkManager.Singleton.ConnectedClients;
                logger.Information(
                    "[Unity] AllianceGamesNetworkTransport: server.OnClientConnect {ClientId} connected clients {ConnectedClients}",
                    message.ClientId,
                    string.Join(", ", connectedClientss.Select(c => c.Key.ToString()))
                );
                // WriteMessage(message);

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
                    ClientId = server.GetClientId(pubKey),
                    Payload = null
                };
                WriteMessage(message);
            };

            server.OnStarted += () => OnStarted?.Invoke();
            serverCts = new CancellationTokenSource();
            await server.Run(() => entrypoint().AttachExternalCancellation(serverCts.Token).AsTask()).AsUniTask();
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
            // using (ProfilerPollEvent.Auto())
            {
                try
                {
                    if (receiveQueue.Reader.TryRead(out var message))
                    {
                        if (message.Type == NetworkEvent.Connect)
                        {
                            var connectedClients = NetworkManager.Singleton.ConnectedClients;
                            logger.Information(
                                "[Unity] AllianceGamesNetworkTransport: PollEvent {ClientId} connected clients {ConnectedClients}",
                                message.ClientId,
                                string.Join(", ", connectedClients.Select(c => c.Key.ToString()))
                            );
                        }
                        clientId = message.ClientId;
                        payload = message.Payload;
                        receiveTime = Time.realtimeSinceStartup;
                        return message.Type;
                    }
                    else
                    {
                        clientId = 0;
                        payload = default;
                        receiveTime = 0;
                        return NetworkEvent.Nothing;
                    }
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
        }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
            // using (ProfilerSend.Auto())
            {
                try
                {
                    if (!isStarted)
                    {
                        return;
                    }

                    Send(payload, clientId).Forget();
                }
                catch (Exception e)
                {
                    LogError(e, "Failed to send message");
                }
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

            senderCts?.Cancel();
            senderCts?.Dispose();
            senderCts = null;

            receiveQueue.Writer.TryComplete();
            receiveQueue = null;

            isStarted = false;
            OnShutdown?.Invoke();
        }

        private async UniTaskVoid Send(ArraySegment<byte> payload, ulong clientId)
        {
            // using (ProfilerSendAsync.Auto())
            {
                var buffer = Buffer.From(payload);
                if (clientId == ServerClientId)
                {
                    await client.Send(WebSocketProtocolHeader, buffer, senderCts.Token).AsUniTask();
                }
                else
                {
                    var client = server.GetClientPubKey(clientId);
                    await server.Send(WebSocketProtocolHeader, client, buffer, senderCts.Token).AsUniTask();
                }
            }
        }

        private void WriteMessage(Message message)
        {
            // using (ProfilerWriteMessage.Auto())
            {
                if (!receiveQueue.Writer.TryWrite(message))
                {
                    LogError("Failed to write message to receive queue");
                }
            }
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
