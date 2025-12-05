using AllianceGamesSdk.Server;
using Serilog;
using System.Linq;
using Buffer = Chromia.Buffer;

namespace AllianceGamesSdk.Unity.Netcode
{
    public static class AllianceGamesServerExtension
    {
        public static ulong GetClientId(this AllianceGamesServer server, Buffer pubKey)
        {
            var clients = server.Clients.ToList();
            var index = clients.IndexOf(pubKey);
            if (index == -1)
            {
                throw new System.ArgumentException($"Client with pubKey {pubKey} not found");
            }
            return (ulong)(index + 1);
        }

        public static ulong GetClientId(this AllianceGamesNetworkManager networkManager, Buffer pubKey)
        {
            return networkManager.transport.Server.GetClientId(pubKey);
        }

        public static Buffer GetClientPubKey(this AllianceGamesServer server, ulong clientId)
        {
            var clients = server.Clients.ToList();
            if (clientId == 0 || clientId > (ulong)clients.Count)
            {
                throw new System.ArgumentException($"Invalid clientId: {clientId}");
            }
            return clients[(int)(clientId - 1)];
        }

        public static Buffer GetClientPubKey(this AllianceGamesNetworkManager networkManager, ulong clientId)
        {
            return networkManager.transport.Server.GetClientPubKey(clientId);
        }
    }
}