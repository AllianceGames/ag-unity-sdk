using AllianceGamesSdk.Client;
using AllianceGamesSdk.Common.Transport;
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

namespace AllianceGamesSdk.Transport.Unity.Netcode
{
    public class AllianceGamesNetworkTransport : NetworkTransport
    {
        [SerializeField]
        private int lowQueueMaxSize = 256;
        [SerializeField]
        private int highQueueMaxBurst = 32;

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
        private System.Threading.Channels.Channel<Message> highPriorityReceive = null;
        private System.Threading.Channels.Channel<Message> lowPriorityReceive = null;
        private System.Threading.Channels.Channel<(ArraySegment<byte>, ulong)> highPrioritySend = null;
        private WebSocketLowPriorityQueue lowPrioritySend = null;
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
                highPriorityReceive = System.Threading.Channels.Channel.CreateUnbounded<Message>();
                lowPriorityReceive = System.Threading.Channels.Channel.CreateUnbounded<Message>();
                highPrioritySend = System.Threading.Channels.Channel.CreateUnbounded<(ArraySegment<byte>, ulong)>();
                lowPrioritySend = new WebSocketLowPriorityQueue(lowQueueMaxSize);
                senderCts = new CancellationTokenSource();

                SenderLoop().Forget();
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
                var highPriority = bytes[0] == 1;
                var message = new Message()
                {
                    Type = NetworkEvent.Data,
                    ClientId = ServerClientId,
                    Payload = UnframeWithPriorityByte(buffer.Bytes)
                };
                WriteMessage(message, highPriority);
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
                WriteMessage(connectMessage, true);
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
                var bytes = buffer.Bytes;
                if (bytes == null || bytes.Length == 0)
                {
                    return;
                }

                await startupTcs.Task;

                var sender = (ulong)server.Clients.ToList().IndexOf(pubKey) + 1;
                var highPriority = bytes[0] == 1;
                var message = new Message()
                {
                    Type = NetworkEvent.Data,
                    ClientId = sender,
                    Payload = UnframeWithPriorityByte(buffer.Bytes)
                };
                WriteMessage(message, highPriority);
            });
            server.OnClientConnect += (pubKey) =>
            {
                var message = new Message()
                {
                    Type = NetworkEvent.Connect,
                    ClientId = server.GetClientId(pubKey),
                    Payload = null
                };
                WriteMessage(message, true);

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
                WriteMessage(message, true);
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
            try
            {
                if (highPriorityReceive.Reader.TryRead(out var highPriorityMessage))
                {
                    clientId = highPriorityMessage.ClientId;
                    payload = highPriorityMessage.Payload;
                    receiveTime = Time.realtimeSinceStartup;
                    return highPriorityMessage.Type;
                }
                else if (lowPriorityReceive.Reader.TryRead(out var lowPriorityMessage))
                {
                    clientId = lowPriorityMessage.ClientId;
                    payload = lowPriorityMessage.Payload;
                    receiveTime = Time.realtimeSinceStartup;
                    return lowPriorityMessage.Type;
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

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
            try
            {
                if (!isStarted)
                {
                    return;
                }

                var highPriority = networkDelivery == NetworkDelivery.Reliable
                    || networkDelivery == NetworkDelivery.ReliableFragmentedSequenced
                    || networkDelivery == NetworkDelivery.ReliableSequenced;
                var framedPayload = FrameWithPriorityByte(payload, highPriority);
                if (highPriority)
                {
                    if (!highPrioritySend.Writer.TryWrite((framedPayload, clientId)))
                    {
                        LogError($"Failed to write message to high priority send queue");
                    }
                }
                else
                {
                    if (!lowPrioritySend.Enqueue(framedPayload, clientId, networkDelivery))
                    {
                        LogError($"Failed to write message to low priority send queue");
                    }
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

            senderCts?.Cancel();
            senderCts?.Dispose();
            senderCts = null;

            highPrioritySend.Writer.TryComplete();
            highPriorityReceive.Writer.TryComplete();
            lowPriorityReceive.Writer.TryComplete();

            highPriorityReceive = null;
            lowPriorityReceive = null;
            highPrioritySend = null;
            lowPrioritySend = null;

            isStarted = false;
            OnShutdown?.Invoke();
        }

        private async UniTaskVoid SenderLoop()
        {
            while (!senderCts.IsCancellationRequested)
            {
                var burst = 0;
                while (burst < highQueueMaxBurst && highPrioritySend.Reader.TryRead(out var high))
                {
                    await Send(high.Item1, high.Item2);
                    burst++;
                }

                if (lowPrioritySend.TryDequeue(out var payload, out var clientId) && payload != null && payload.Count > 0)
                {
                    await Send(payload, clientId);
                }

                var waitHi = highPrioritySend.Reader.WaitToReadAsync(senderCts.Token).AsUniTask();
                var waitLo = lowPrioritySend.WaitForItemAsync(senderCts.Token);
                await UniTask.WhenAny(waitHi, waitLo).AttachExternalCancellation(senderCts.Token).AsUniTask();
            }
        }

        private async UniTask Send(ArraySegment<byte> payload, ulong clientId)
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

        private void WriteMessage(Message message, bool highPriority)
        {
            var queue = highPriority ? highPriorityReceive : lowPriorityReceive;
            if (!queue.Writer.TryWrite(message))
            {
                LogError("Failed to write message to queue");
            }
        }

        private ArraySegment<byte> FrameWithPriorityByte(ArraySegment<byte> src, bool high)
        {
            var arr = new byte[src.Count + 1];
            arr[0] = high ? (byte)1 : (byte)0;
            Array.Copy(src.Array!, src.Offset, arr, 1, src.Count);
            return new ArraySegment<byte>(arr);
        }

        private ArraySegment<byte> UnframeWithPriorityByte(ArraySegment<byte> src)
        {
            if (src == null || src.Count <= 1)
            {
                return default;
            }

            var arr = new byte[src.Count - 1];
            Array.Copy(src.Array!, src.Offset + 1, arr, 0, src.Count - 1);
            return new ArraySegment<byte>(arr);
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
