using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AllianceGamesSdk.Common.Transport;
using AOT;
using Cysharp.Threading.Tasks;
using Serilog;
using WebSocketSharp;

namespace AllianceGamesSdk.Transport.Unity
{
    internal class WebSocketTransportNativeJS : ITransport
    {
        private readonly ILogger logger;

        public WebSocketTransportNativeJS(ILogger logger)
        {
            this.logger = logger;
        }

        public async Task<ITransportConnection> Connect(Uri uri, CancellationToken ct)
        {
            var connectionWrapper = await WebSocketWrapper.Connect(uri.ToString(), logger, ct);
            return new WebSocketConnection(connectionWrapper, logger);
        }

        public IAsyncEnumerable<ITransportConnection> Listen(string ip, int port, CancellationToken ct)
        {
            throw new NotImplementedException("WebSocketTransportNativeJS.Listen cannot listen for connections");
        }

        private class WebSocketWrapper : IWebSocket
        {
            public WebSocketState ReadyState => WebSocketJsWrapper.GetState(url);
            public event Action<byte[]> OnMessage;
            public event Action<CloseStatusCode, string> OnClose;
            public event Action<string> OnError;

            public ulong WaitTime => 0;

            private readonly string url;
            private readonly UniTaskCompletionSource closeTcs = new UniTaskCompletionSource();

            public WebSocketWrapper(string url)
            {
                this.url = url;
            }

            public void Send(byte[] data)
            {
                WebSocketJsWrapper.Send(url, data, 0, data.Length);
            }

            public async UniTask CloseAsync()
            {
                WebSocketJsWrapper.Close(url, CloseStatusCode.Normal, null);
                await closeTcs.Task;
            }

            private UniTaskCompletionSource openTcs = new UniTaskCompletionSource();

            internal void OnOpen()
            {
                openTcs.TrySetResult();
            }

            internal void OnMessageCallback(ArraySegment<byte> data)
            {
                OnMessage?.Invoke(data.Array);
            }

            internal void OnErrorCallback(string message)
            {
                OnError?.Invoke(message);
            }

            internal void OnCloseCallback(CloseStatusCode code)
            {
                closeTcs.TrySetResult();
                OnClose?.Invoke(code, null);
            }

            public static async Task<WebSocketWrapper> Connect(string url, ILogger logger, CancellationToken ct)
            {
                var wrapper = new WebSocketWrapper(url);
                if (!WebSocketJsWrapper.Add(url, wrapper))
                {
                    logger.Error("WebSocket connection already exists for {url}", url);
                    return null;
                }

                await wrapper.openTcs.Task.AttachExternalCancellation(ct);

                return wrapper;
            }
        }

        private static class WebSocketJsWrapper
        {
            private static ConcurrentDictionary<string, WebSocketWrapper> wrappers
                = new ConcurrentDictionary<string, WebSocketWrapper>();

            internal static bool Add(string url, WebSocketWrapper wrapper)
            {
                var success = wrappers.TryAdd(url, wrapper);
                if (success)
                {
                    Connect(url, OnOpenEvent, OnMessageEvent, OnErrorEvent, OnCloseEvent);
                }
                return success;
            }

            internal static bool Remove(string url)
            {
                return wrappers.TryRemove(url, out _);
            }

            [DllImport("__Internal")]
            internal static extern void Connect(
                string url,
                OnOpenCallback onOpenCallback,
                OnMessageCallback onMessageCallback,
                OnErrorCallback onErrorCallback,
                OnCloseCallback onCloseCallback
            );
#if UNITY_WEBGL && !UNITY_EDITOR
            [DllImport("__Internal")]
            internal static extern WebSocketState GetState(string url);
            [DllImport("__Internal")]
            internal static extern void Send(string url, byte[] data, int offset, int count);
            [DllImport("__Internal")]
            internal static extern void Close(string url, CloseStatusCode code = CloseStatusCode.Normal, string reason = null);
#else
            internal static WebSocketState GetState(string url) => WebSocketState.Closed;
            internal static void Send(string url, byte[] data, int offset, int count) => UnityEngine.Debug.Log("WebGL only");
            internal static void Close(string url, CloseStatusCode code = CloseStatusCode.Normal, string reason = null)
                => UnityEngine.Debug.Log("WebGL only");
#endif

            internal delegate void OnOpenCallback(string url);
            internal delegate void OnMessageCallback(string url, IntPtr messagePointer, int messageSize);
            internal delegate void OnErrorCallback(string url, IntPtr errorPointer);
            internal delegate void OnCloseCallback(string url, int closeCode);

            [MonoPInvokeCallback(typeof(OnOpenCallback))]
            internal static void OnOpenEvent(string url)
            {
                if (wrappers.TryGetValue(url, out var wrapper))
                {
                    wrapper.OnOpen();
                }
            }

            [MonoPInvokeCallback(typeof(OnMessageCallback))]
            internal static void OnMessageEvent(string url, IntPtr payloadPointer, int length)
            {
                if (!wrappers.TryGetValue(url, out var wrapper))
                {
                    return;
                }

                var buffer = new byte[length];

                Marshal.Copy(payloadPointer, buffer, 0, length);
                wrapper.OnMessageCallback(new ArraySegment<byte>(buffer, 0, length));
            }

            [MonoPInvokeCallback(typeof(OnErrorCallback))]
            internal static void OnErrorEvent(string url, IntPtr errorPointer)
            {
                if (!wrappers.TryGetValue(url, out var wrapper))
                {
                    return;
                }

                string errorMessage = Marshal.PtrToStringAuto(errorPointer);
                wrapper.OnErrorCallback(errorMessage);
            }

            [MonoPInvokeCallback(typeof(OnCloseCallback))]
            internal static void OnCloseEvent(string url, int disconnectCode)
            {
                if (!wrappers.TryGetValue(url, out var wrapper))
                {
                    return;
                }

                CloseStatusCode code = (CloseStatusCode)disconnectCode;

                if (!Enum.IsDefined(typeof(CloseStatusCode), code))
                {
                    code = CloseStatusCode.Undefined;
                }

                wrapper.OnCloseCallback(code);
            }
        }
    }
}