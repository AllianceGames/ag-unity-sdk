using AllianceGamesSdk.Server;
using Chromia;
using System.Linq;

namespace AllianceGamesSdk.Unity.Netcode
{
    public static class AllianceGamesServerExtension
    {
        public static ulong GetClientId(this AllianceGamesServer server, Buffer pubKey)
        {
            return (ulong)server.Clients.ToList().IndexOf(pubKey) + 1;
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