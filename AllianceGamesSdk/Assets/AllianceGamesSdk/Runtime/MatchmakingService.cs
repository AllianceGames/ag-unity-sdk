using AllianceGamesSdk.Matchmaking.Models;
using Chromia;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Buffer = Chromia.Buffer;

#if ENABLE_IL2CPP
using Newtonsoft.Json.Utilities;
using UnityEngine;
#endif

namespace AllianceGamesSdk.Matchmaking
{
    public static class MatchmakingServiceFactory
    {
#if ENABLE_IL2CPP
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void EnsureAotTypes()
        {
            AotHelper.EnsureType<GetMatchmakingTicketStatusResult>();
        }
#endif

        public static IMatchmakingService Get(
            ChromiaClient chromiaClient
        )
        {
            return new MatchmakingService(chromiaClient);
        }

        public static async Task<IMatchmakingService> Get(
            Common.AllianceGamesBlockchain.Target target
        )
        {
            var chromiaClient = await Common.AllianceGamesBlockchain.Get(target);
            return new MatchmakingService(chromiaClient);
        }
    }

    public interface IMatchmakingService
    {
        Task<CreateTicketResponse> CreateTicket(
            CreateTicketRequest request,
            CancellationToken ct
        );

        Task<GetTicketStatusResult> GetTicketStatus(
            GetTicketStatusRequest request,
            CancellationToken ct
        );

        Task<GetMatchResponse> GetMatch(
            GetMatchRequest request,
            CancellationToken ct
        );

        Task<TransactionReceipt> CancelTicket(
            CancelTicketRequest request,
            CancellationToken ct
        );
    }

    public enum MatchmakingTicketState
    {
        Open,
        WaitingForServer,
        Matched,
        Closed,
        Canceled
    }

    public class MatchmakingService : IMatchmakingService
    {
        private readonly ChromiaClient chromiaClient;

        public MatchmakingService(
            ChromiaClient chromiaClient
        )
        {
            this.chromiaClient = chromiaClient;
        }

        public async Task<TransactionReceipt> CreateTicket(
            CreateTicketRequest request,
            CancellationToken ct
        )
        {
            return await chromiaClient.SendUniqueTransaction(
                new Operation("ag.IMatchmaking.create_ticket", request), ct: ct);
        }

        public async Task<string> GetMatchmakingTicket(
            GetMatchmakingTicketRequest request,
            CancellationToken ct
        )
        {
            return await chromiaClient.Query<string>(
                "ag.IMatchmaking.get_ticket_id",
                ("par", new object[] { request.Identifier, request.Duid, request.QueueName })
            );
        }

        public async Task<int> GetAmountTicketsInQueue(
            GetAmountTicketsInQueueRequest request,
            CancellationToken ct
        )
        {
            return await chromiaClient.Query<int>(
                "ag.IMatchmaking.get_amount_tickets_in_queue",
                ("par", new object[] { request.Duid, request.QueueName })
            );
        }

        public async Task<GetTicketStatusResult> GetTicketStatus(
            GetTicketStatusRequest request,
            CancellationToken ct
        )
        {
            return await chromiaClient.Query<GetTicketStatusResult>(
                "ag.IMatchmaking.get_ticket_status",
                ct,
                ("par", new object[] { request.TicketId })
            );
        }

        public async Task<GetConnectionDetailsResponse> GetConnectionDetails(
            GetConnectionDetailsRequest request,
            CancellationToken ct
        )
        {
            return await chromiaClient.Query<GetConnectionDetailsResponse>(
                "ag.ISession.get_connection_details",
                ct,
                ("session_id", request.SessionId)
            );
        }

        public async Task<TransactionReceipt> CancelTicket(
            CancelTicketRequest request,
            CancellationToken ct
        )
        {
            return await chromiaClient.SendUniqueTransaction(
                new Operation("ag.IMatchmaking.cancel_ticket", request), ct: ct);
        }

        public async Task<TransactionReceipt> CancelAllMatchmakingTicketsForPlayer(
            CancelAllMatchmakingTicketRequests request,
            CancellationToken ct
        )
        {
            return await chromiaClient.SendUniqueTransaction(
                new Operation("ag.IMatchmaking.cancel_all_tickets", request), ct: ct);
        }

        public static async Task<string> GetDuid(
            ChromiaClient chromiaClient,
            string displayName,
            CancellationToken ct
        )
        {
            return await chromiaClient.Query<string>(
                "ag.IDappProvider.get_uid",
                ct,
                ("display_name", displayName)
            );
        }
    }

    namespace Models
    {
        public class CreateTicketRequest
        {
            [JsonProperty("identifier")]
            public Buffer Identifier;
            [JsonProperty("network_signer")]
            public Buffer NetworkSigner;
            [JsonProperty("duid")]
            public string Duid;
            [JsonProperty("queue_name")]
            public string QueueName;
            [JsonProperty("match_data")]
            public string MatchData = "[]";
            [JsonProperty("attributes")]
            public Dictionary<string, object> Attributes = new Dictionary<string, object>();

            [JsonConstructor]
            public CreateTicketRequest() { }
        }

        public class CreateTicketResponse
        {
            [JsonProperty("identifier")]
            public Buffer Identifier;
            [JsonProperty("network_signer")]
            public Buffer NetworkSigner;
            [JsonProperty("duid")]
            public string Duid;
            [JsonProperty("queue_name")]
            public string QueueName;
            [JsonProperty("match_data")]
            public string MatchData = "[]";
            [JsonProperty("attributes")]
            public Dictionary<string, object> Attributes = new Dictionary<string, object>();

            [JsonConstructor]
            public CreateTicketResponse() { }
        }

        public class GetTicketStatusRequest
        {
            [JsonProperty("ticket_id")]
            public string TicketId;

            [JsonConstructor]
            public GetTicketStatusRequest() { }
        }

        public class GetTicketStatusResult
        {
            [JsonProperty("ticket_id")]
            public string TicketId;
            [JsonProperty("queue_name")]
            public string QueueName;
            [JsonProperty("created_at")]
            public long CreatedAtTimestamp;
            public DateTime CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtTimestamp).DateTime;
            [JsonProperty("status")]
            public MatchmakingTicketState Status;
            [JsonProperty("give_up_after_seconds")]
            public int GiveUpAfterSeconds;
            [JsonProperty("identifier")]
            public Buffer Identifier;
            [JsonProperty("session_id")]
            public string SessionId;

            [JsonConstructor]
            public GetTicketStatusResult() { }
        }

        public class GetMatchResponse
        {
            [JsonProperty("match_id")]
            public string MatchId;
        }

        public class GetMatchRequest
        {
            [JsonProperty("match_id")]
            public string MatchId;
        }

        public class CancelTicketRequest
        {
            [JsonProperty("identifier")]
            public Buffer Identifier;
            [JsonProperty("ticket_id")]
            public string TicketId;

            [JsonConstructor]
            public CancelTicketRequest() { }
        }

        public class CancelTicketResponse
        {
            [JsonProperty("identifier")]
            public Buffer Identifier;
            [JsonProperty("ticket_id")]
            public string TicketId;
        }

        public enum TicketStatus
        {
            [EnumMember(Value = "open")]
            open,
            [EnumMember(Value = "canceled")]
            canceled,
            [EnumMember(Value = "matched")]
            matched
        }
    }
}