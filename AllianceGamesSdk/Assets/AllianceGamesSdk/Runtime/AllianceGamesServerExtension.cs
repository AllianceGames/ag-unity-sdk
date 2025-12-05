using AllianceGamesSdk.Server;
using Serilog;
using System.Collections.Generic;
using Buffer = Chromia.Buffer;

namespace AllianceGamesSdk.Unity.Netcode
{
    public static class AllianceGamesServerExtension
    {
        private static Dictionary<ulong, Buffer> cachedClientPubKeys;
        private static Dictionary<Buffer, ulong> cachedClientIds;
        public static ulong GetClientId(this AllianceGamesServer server, Buffer pubKey)
        {
            EnsureClientCache(server);
            Log.Logger.Information($"Getting client id for pubkey {pubKey.Parse()}, cached client ids: {string.Join(", ", cachedClientIds.Keys)}");
            return cachedClientIds[pubKey];
        }

        public static ulong GetClientId(this AllianceGamesNetworkManager networkManager, Buffer pubKey)
        {
            return networkManager.transport.Server.GetClientId(pubKey);
        }

        public static Buffer GetClientPubKey(this AllianceGamesServer server, ulong clientId)
        {
            EnsureClientCache(server);
            return cachedClientPubKeys[clientId];
        }

        public static Buffer GetClientPubKey(this AllianceGamesNetworkManager networkManager, ulong clientId)
        {
            return networkManager.transport.Server.GetClientPubKey(clientId);
        }

        private static void EnsureClientCache(AllianceGamesServer server)
        {
            if (cachedClientPubKeys == null)
            {
                cachedClientPubKeys = new();
                cachedClientIds = new();
                var i = 1ul;
                foreach (var pubkey in server.Clients)
                {
                    cachedClientPubKeys.Add(i, pubkey);
                    cachedClientIds.Add(pubkey, i);
                    i++;
                }
            }
        }
    }
}