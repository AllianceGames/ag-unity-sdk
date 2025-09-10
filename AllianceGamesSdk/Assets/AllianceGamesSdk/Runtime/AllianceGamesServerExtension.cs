using AllianceGamesSdk.Server;
using System;
using System.Linq;
using Buffer = Chromia.Buffer;

namespace AllianceGamesSdk.Unity.Netcode
{
    public static class AllianceGamesServerExtension
    {
        public static ulong GetClientId(this AllianceGamesServer server, Buffer pubKey)
        {
            var id = server.Clients.ToList().IndexOf(pubKey);
            if (id == -1)
            {
                throw new ArgumentOutOfRangeException($"Client with public key {pubKey.Parse()} not found");
            }
            return (ulong)id + 1;
        }

        public static ulong GetClientId(this AllianceGamesNetworkManager networkManager, Buffer pubKey)
        {
            return networkManager.transport.Server.GetClientId(pubKey);
        }

        public static Buffer GetClientPubKey(this AllianceGamesServer server, ulong clientId)
        {
            return server.Clients.ToList()[(int)clientId - 1];
        }

        public static Buffer GetClientPubKey(this AllianceGamesNetworkManager networkManager, ulong clientId)
        {
            return networkManager.transport.Server.GetClientPubKey(clientId);
        }
    }
}