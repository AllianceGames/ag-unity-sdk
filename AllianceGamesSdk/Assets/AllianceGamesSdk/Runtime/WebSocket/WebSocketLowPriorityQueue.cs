using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;

namespace AllianceGamesSdk.Transport.Unity.Netcode
{
    internal class WebSocketLowPriorityQueue
    {
        public readonly int Capacity;

        private readonly LinkedList<Entry> queue = new();
        private readonly Dictionary<(ulong client, ulong key), LinkedListNode<Entry>> index = new();
        private readonly object queueLock = new();
        private readonly SemaphoreSlim signal = new(0, 1);

        public WebSocketLowPriorityQueue(int capacity) {
            this.Capacity = Math.Max(1, capacity);
        }
        public bool Enqueue(ArraySegment<byte> payload, ulong clientId, NetworkDelivery delivery)
        {
            var entry = new Entry(payload, clientId, delivery);

            lock (queueLock)
            {
                bool wasEmpty = queue.Count == 0;

                if (delivery == NetworkDelivery.UnreliableSequenced)
                {
                    var key = (clientId, entry.Key);
                    if (index.TryGetValue(key, out var node))
                    {
                        node.Value = entry;
                        return true;
                    }

                    if (queue.Count >= Capacity)
                    {
                        DropOldest();
                    }

                    var newNode = queue.AddLast(entry);
                    index[key] = newNode;
                    if (wasEmpty)
                    {
                        signal.Release();
                    }
                    return true;
                }

                if (queue.Count >= Capacity)
                {
                    DropOldest();
                }

                queue.AddLast(entry);
                if (wasEmpty)
                {
                    signal.Release();
                }
                return true;
            }
        }

        public bool TryDequeue(out ArraySegment<byte> payload, out ulong clientId)
        {
            lock (queueLock)
            {
                if (queue.Count == 0)
                {
                    payload = default;
                    clientId = 0;
                    return false;
                }

                var node = queue.First!;
                queue.RemoveFirst();

                var entry = node.Value;
                if (entry.Delivery == NetworkDelivery.UnreliableSequenced)
                {
                    index.Remove((entry.ClientId, entry.Key));
                }

                payload = entry.Payload;
                clientId = entry.ClientId;
                return true;
            }
        }

        public async UniTask WaitForItemAsync(CancellationToken ct)
        {
            // fast-path check to avoid lost wakeups
            lock (queueLock)
            {
                if (queue.Count > 0)
                {
                    return;
                }
            }
            await signal.WaitAsync(ct);
        }

        private void DropOldest()
        {
            var oldest = queue.First!;
            var entry = oldest.Value;
            queue.RemoveFirst();
            if (entry.Delivery == NetworkDelivery.UnreliableSequenced)
            {
                index.Remove((entry.ClientId, entry.Key));
            }
        }

        readonly struct Entry
        {
            public readonly ArraySegment<byte> Payload;
            public readonly ulong ClientId;
            public readonly NetworkDelivery Delivery;
            public readonly ulong Key;

            public Entry(ArraySegment<byte> payload, ulong clientId, NetworkDelivery delivery)
            {
                Payload = payload;
                ClientId = clientId;
                Delivery = delivery;
                Key = delivery == NetworkDelivery.UnreliableSequenced ? DeriveStreamKey(payload) : 0;
            }

            private static ulong DeriveStreamKey(ArraySegment<byte> seg)
            {
                ulong h = 1469598103934665603UL;
                int n = Math.Min(seg.Count, 12);
                var arr = seg.Array!;
                int off = seg.Offset;
                for (int i = 0; i < n; i++)
                {
                    h = (h ^ arr[off + i]) * 1099511628211UL;
                }
                return h;
            }
        }
    }
}