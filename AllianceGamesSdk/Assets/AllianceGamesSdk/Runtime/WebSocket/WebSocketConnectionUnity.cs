using AllianceGamesSdk.Common.Profiler;
using AllianceGamesSdk.Common.Transport;
using Cysharp.Threading.Tasks;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp;

namespace AllianceGamesSdk.Transport.Unity
{
    internal class WebSocketConnection : ITransportConnection
    {
        // Profiler markers for automatic timing tracking
        // private static readonly TimingReporter ProfilerSend = new("Unity/WebSocketConnection/Send");
        // private static readonly TimingReporter ProfilerDisconnect = new("Unity/WebSocketConnection/Disconnect");

        private IWebSocket webSocket;
        private readonly ILogger logger;

        public event Action<byte[]> OnMessage;

        internal WebSocketConnection(IWebSocket webSocket, ILogger logger)
        {
            this.webSocket = webSocket;
            this.logger = logger;
            webSocket.OnMessage += data =>
            {
                if (OnMessage != null)
                {

                    OnMessage?.Invoke(data);
                }
                else
                {
                    logger.Warning($"No listener on WebSocketConnection.OnMessage");
                }
            };
            webSocket.OnClose += (code, reason) =>
            {
                logger?.Information("[Unity] WebSocketConnection: Closed with code {Code} and reason {Reason}", code, reason);
            };
            webSocket.OnError += (message) =>
            {
                logger?.Error("[Unity] WebSocketConnection: Error {Message}", message);
            };
        }

        public Task Send(byte[] message, CancellationToken ct)
        {
            // using (ProfilerSend.Auto())
            {
                if (webSocket == null || webSocket.ReadyState != WebSocketState.Open)
                {
                    logger?.Error($"Cannot send on closed socket.");
                    return Task.CompletedTask;
                }

                try
                {
                    webSocket.Send(message);
                }
                catch (Exception e)
                {
                    logger?.Error(e, $"Error while running send task for WebSocket.");
                }
                return Task.CompletedTask;
            }
        }

        public async Task Disconnect(CancellationToken ct)
        {
            // using (ProfilerDisconnect.Auto())
            {
                if (webSocket == null)
                {
                    return;
                }

                try
                {
                    try
                    {
                        if (webSocket.ReadyState == WebSocketState.Closed)
                        {
                            return;
                        }

                        await webSocket.CloseAsync();
                    }
                    catch (OperationCanceledException)
                    { }
                    finally
                    {
                        webSocket = null;
                    }
                }
                catch (ObjectDisposedException)
                { }
                catch (Exception e)
                {
                    logger?.Error(e, $"Error while disconnecting from WebSocket.");
                }
            }
        }

        public override bool Equals(object obj)
        {
            return (obj is WebSocketConnection c) && webSocket.Equals(c.webSocket);
        }

        public override int GetHashCode()
        {
            return webSocket.GetHashCode();
        }
    }
}