using AllianceGamesSdk.Common;
using AllianceGamesSdk.Common.Profiler;
using AllianceGamesSdk.Common.Transport;
using AllianceGamesSdk.Unity;
using Cysharp.Threading.Tasks;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp.Net.WebSockets;
using WebSocketSharp.Server;


namespace AllianceGamesSdk.Transport.Unity
{
    internal class WebSocketTransport : ITransport
    {
        // Profiler markers for automatic timing tracking
        private static readonly TimingReporter ProfilerConnect = new("WebSocketTransport/Connect");
        private static readonly TimingReporter ProfilerListen = new("WebSocketTransport/Listen");

        private readonly ILogger logger;
        private readonly ITaskRunner taskRunner = new UniTaskRunner();
        private readonly System.Threading.Channels.Channel<WebSocketConnection> channel
            = System.Threading.Channels.Channel.CreateUnbounded<WebSocketConnection>();

        public WebSocketTransport(ILogger logger)
        {
            this.logger = logger;
        }

        public async Task<ITransportConnection> Connect(Uri uri, CancellationToken ct)
        {
            using (ProfilerConnect.Auto())
            {
                logger.Information("[Unity] WebSocketTransport: Connecting to {Uri}", uri);
                var builder = new UriBuilder(uri);
                if (builder.Scheme == "http")
                {
                    builder.Scheme = "ws";
                }
                else if (builder.Scheme == "https")
                {
                    builder.Scheme = "wss";
                }
                uri = builder.Uri;
                logger.Information("[Unity] WebSocketTransport: Changed scheme to {Uri}", uri);

                var webSocket = new WebSocketSharp.WebSocket(uri.ToString());
                logger.Information("[Unity] WebSocketTransport: Created WebSocket");
                webSocket.EnableRedirection = true;
                var cts = new UniTaskCompletionSource<bool>();
                ct.Register(() => cts.TrySetResult(false));
                logger.Information("[Unity] WebSocketTransport: Registering cancellation token");
                webSocket.OnOpen += (sender, e) => cts.TrySetResult(true);
                logger.Information("[Unity] WebSocketTransport: Connecting to {Uri}", uri);
                try
                {
                    webSocket.Connect();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "[Unity] WebSocketTransport: Failed to connect to {Uri}", uri);
                    return null;
                }

                if (await cts.Task)
                {
                    logger.Information("[Unity] WebSocketTransport: Connected to {Uri}", uri);
                    return new WebSocketConnection(new WebSocketSharpWrapper(webSocket), logger);
                }
                else
                {
                    logger.Information("[Unity] WebSocketTransport: Failed to connect to {Uri}", uri);
                    return null;
                }
            }
        }

        public async IAsyncEnumerable<ITransportConnection> Listen(
            string ip,
            int port,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            using (ProfilerListen.Auto())
            {
                var server = new WebSocketServer(port);
                server.AllowForwardedRequest = true;
                ct.Register(() =>
                {
                    server.Stop();
                });

                WebSocketServerConnectionBehavior.OnConnection += async (context) =>
                {
                    var connection = new WebSocketConnection(new WebSocketSharpWrapper(context.WebSocket), logger);
                    await channel.Writer.WriteAsync(connection, ct);
                };


                server.AddWebSocketService<WebSocketServerConnectionBehavior>("/");
                server.Start();

                await foreach (var connection in taskRunner.Yield(channel.Reader, ct))
                {
                    yield return connection;
                }
            }
        }

        private class WebSocketServerConnectionBehavior : WebSocketBehavior
        {
            public static Action<WebSocketContext> OnConnection;

            protected override void OnOpen()
            {
                OnConnection?.Invoke(Context);
            }
        }
    }
}
