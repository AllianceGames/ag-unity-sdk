using System;
using Cysharp.Threading.Tasks;
using WebSocketSharp;

namespace AllianceGamesSdk.Transport.Unity
{
    internal interface IWebSocket
    {
        WebSocketState ReadyState { get; }
        ulong WaitTime { get; }

        event Action<byte[]> OnMessage;
        event Action<CloseStatusCode, string> OnClose;
        event Action<string> OnError;

        void Send(byte[] data);
        UniTask CloseAsync();
    }

    internal class WebSocketSharpWrapper : IWebSocket
    {
        public WebSocketState ReadyState => webSocket.ReadyState;
        public ulong WaitTime => (ulong)webSocket.WaitTime.Milliseconds;

        public event Action<byte[]> OnMessage;
        public event Action<CloseStatusCode, string> OnClose;
        public event Action<string> OnError;

        private readonly WebSocketSharp.WebSocket webSocket;
        private readonly UniTaskCompletionSource closeTcs = new UniTaskCompletionSource();

        public WebSocketSharpWrapper(WebSocketSharp.WebSocket webSocket)
        {
            this.webSocket = webSocket;
            webSocket.OnMessage += (sender, e) =>
            {
                UnityEngine.Debug.Log("Received message on websocket");
                OnMessage?.Invoke(e.RawData);
            };
            webSocket.OnClose += (sender, e) =>
            {
                closeTcs.TrySetResult();
                OnClose?.Invoke((CloseStatusCode)e.Code, e.Reason);
            };
            webSocket.OnError += (sender, e) => OnError?.Invoke(e.Message);
        }

        public void Send(byte[] data)
        {
            webSocket.Send(data);
        }

        public async UniTask CloseAsync()
        {
            webSocket.CloseAsync();
            await closeTcs.Task;
        }
    }

}

