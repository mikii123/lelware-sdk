using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Client helpers for the portal's matchmaking API. A player joins a queue and waits to
    ///     be matched; the match itself arrives out-of-band as a <c>match_found</c> frame over
    ///     the realtime channel, so these helpers pair with <see cref="LelwareRealtime" />.
    ///
    ///     <para>This is HAND-WRITTEN (not generated): register/leave bracket a realtime
    ///     connect+subscribe handshake plus an async push the codegen can't model — same reason
    ///     storage is hand-written.</para>
    ///
    ///     <para><b>Usage:</b></para>
    ///     <code>
    ///     var realtime = new LelwareRealtime(client);
    ///     realtime.Start();
    ///     realtime.OnMatchFound(m => Debug.Log($"Matched {string.Join(",", m.Players)} (match {m.MatchId})"));
    ///     await client.RegisterMatchmakingAsync(realtime, "ranked-1v1");
    ///     // …later…
    ///     await client.LeaveMatchmakingAsync("ranked-1v1");
    ///     </code>
    ///
    ///     <para>Everything here is exception-free — each call returns a
    ///     <see cref="LelwareResult" /> like the rest of the SDK. The caller must be logged in.</para>
    /// </summary>
    public static class MatchmakingEndpoints
    {
        /// <summary>The realtime channel match notifications arrive on (mirrors the portal side).</summary>
        public const string Channel = "matchmaking";

        /// <summary>
        ///     Subscribe to match notifications. Call once after <see cref="LelwareRealtime.Start" />
        ///     (and before/after registering — subscriptions are re-applied on reconnect). The
        ///     handler runs on the realtime client's captured thread (the Unity main thread).
        /// </summary>
        public static void OnMatchFound(this LelwareRealtime realtime, Action<MatchFound> handler)
        {
            if (realtime == null || handler == null) return;
            realtime.Subscribe<MatchFound>(Channel, handler);
        }

        /// <summary>
        ///     Register the player into queue <paramref name="queueId" />. Requires a connected
        ///     <paramref name="realtime" /> client (its connection id is sent so the server can
        ///     push the match to THIS socket and prune the player if it drops). Returns an error
        ///     result when the socket isn't connected yet — <see cref="LelwareRealtime.Start" /> and
        ///     wait for <see cref="LelwareRealtime.IsConnected" /> first.
        /// </summary>
        public static Task<LelwareResult> RegisterMatchmakingAsync(
            this LelwareClient client, LelwareRealtime realtime, string queueId, CancellationToken ct = default)
        {
            var connectionId = realtime?.ConnectionId;
            if (string.IsNullOrEmpty(connectionId))
            {
                return Task.FromResult(new LelwareResult
                {
                    Error = true,
                    Code = 0,
                    Message = "Realtime client is not connected — call Start() and wait for IsConnected before registering."
                });
            }

            var body = JsonConvert.SerializeObject(new { queueId, connectionId });
            return client.SendAsync(UnityWebRequest.kHttpVerbPOST, "Matchmaking/Register", null, body, ct);
        }

        /// <summary>Leave queue <paramref name="queueId" /> (idempotent — fine to call when not queued).</summary>
        public static Task<LelwareResult> LeaveMatchmakingAsync(
            this LelwareClient client, string queueId, CancellationToken ct = default)
        {
            var body = JsonConvert.SerializeObject(new { queueId });
            return client.SendAsync(UnityWebRequest.kHttpVerbPOST, "Matchmaking/Leave", null, body, ct);
        }

        // --- wire DTOs ---------------------------------------------------------

        /// <summary>
        ///     Payload of a <c>match_found</c> frame. <see cref="MatchId"/> is currently a server-
        ///     minted id; a future "game session" feature will make it a real session id (the shape
        ///     stays the same, so code against it now).
        /// </summary>
        [Serializable]
        public sealed class MatchFound
        {
            [JsonProperty("matchId")] public string MatchId;
            [JsonProperty("queueId")] public string QueueId;
            [JsonProperty("players")] public string[] Players;
        }
    }
}
