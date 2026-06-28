using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Client helpers for the portal's game-session API. A session is created server-side when
    ///     a match forms (its id arrives as the <c>matchId</c> of a <c>match_found</c> frame — see
    ///     <see cref="MatchmakingEndpoints.MatchFound" />), together with its own per-session
    ///     realtime channel. The matched player "joins" by subscribing to that channel and then
    ///     reads/writes the session's state through this API.
    ///
    ///     <para>HAND-WRITTEN (not generated), like matchmaking/storage: the subscribe-to-join +
    ///     the async broadcast push aren't a single JSON round-trip the codegen models.</para>
    ///
    ///     <para><b>Usage:</b></para>
    ///     <code>
    ///     realtime.OnMatchFound(m => {
    ///         realtime.JoinSession(m.MatchId, json => Debug.Log("session msg: " + json));
    ///     });
    ///     // …during play…
    ///     await client.SetSessionDataAsync(sessionId, "score", "10");   // if the definition allows players
    ///     await client.BroadcastSessionAsync(sessionId, "moved", new { x = 1, y = 2 });
    ///     </code>
    ///
    ///     <para>The data/broadcast calls are exception-free — each returns a
    ///     <see cref="LelwareResult" /> like the rest of the SDK. The caller must be logged in and a
    ///     roster member of the session.</para>
    /// </summary>
    public static class GameSessionEndpoints
    {
        /// <summary>
        ///     The realtime channel for a session — mirrors the portal's
        ///     <c>GameSessionService.ChannelKey</c>. It's a Player-scoped channel: subscribing joins
        ///     your own bucket, and the server only broadcasts to roster members' buckets.
        /// </summary>
        public static string ChannelFor(string sessionId) => "gs:" + sessionId;

        /// <summary>
        ///     Join a session: subscribe to its realtime channel so the session's broadcasts reach
        ///     this client. <paramref name="handler" /> receives each message payload as raw JSON on
        ///     the realtime client's captured (main) thread. The subscription is reconnect-safe (the
        ///     realtime client re-applies it automatically). Call after the <c>match_found</c> frame.
        /// </summary>
        public static void JoinSession(this LelwareRealtime realtime, string sessionId, Action<string> handler)
        {
            if (realtime == null || handler == null || string.IsNullOrEmpty(sessionId)) return;
            realtime.Subscribe(ChannelFor(sessionId), handler);
        }

        /// <summary>Typed overload — the payload is deserialized to <typeparamref name="T" /> before your handler runs.</summary>
        public static void JoinSession<T>(this LelwareRealtime realtime, string sessionId, Action<T> handler)
        {
            if (realtime == null || handler == null || string.IsNullOrEmpty(sessionId)) return;
            realtime.Subscribe(ChannelFor(sessionId), handler);
        }

        /// <summary>Leave a session — stop receiving its broadcasts (removes the channel subscription).</summary>
        public static void LeaveSession(this LelwareRealtime realtime, string sessionId)
        {
            if (realtime == null || string.IsNullOrEmpty(sessionId)) return;
            realtime.Unsubscribe(ChannelFor(sessionId));
        }

        /// <summary>
        ///     Broadcast <paramref name="data" /> as event <paramref name="eventName" /> to everyone
        ///     in the session. Gated server-side by the session definition's player-broadcast flag
        ///     (a 403 result when players aren't allowed).
        /// </summary>
        public static Task<LelwareResult> BroadcastSessionAsync(
            this LelwareClient client, string sessionId, string eventName, object data, CancellationToken ct = default)
        {
            var body = JsonConvert.SerializeObject(new { sessionId, @event = eventName, data });
            return client.SendAsync(UnityWebRequest.kHttpVerbPOST, "GameSession/Broadcast", null, body, ct);
        }

        /// <summary>
        ///     Set a session KV entry. Gated server-side by the definition's player-set-data flag
        ///     (a 403 result when players aren't allowed).
        /// </summary>
        public static Task<LelwareResult> SetSessionDataAsync(
            this LelwareClient client, string sessionId, string key, string value, CancellationToken ct = default)
        {
            var body = JsonConvert.SerializeObject(new { sessionId, key, value });
            return client.SendAsync(UnityWebRequest.kHttpVerbPOST, "GameSession/SetSessionData", null, body, ct);
        }

        /// <summary>
        ///     Read session KV — one entry when <paramref name="key" /> is given, or all entries
        ///     when null. Any roster member may read.
        /// </summary>
        public static Task<LelwareResult<SessionDataEntry[]>> GetSessionDataAsync(
            this LelwareClient client, string sessionId, string key = null, CancellationToken ct = default)
        {
            var action = "GameSession/GetSessionData?sessionId=" + Uri.EscapeDataString(sessionId ?? "");
            if (!string.IsNullOrEmpty(key))
            {
                action += "&key=" + Uri.EscapeDataString(key);
            }
            return client.SendAsync<SessionDataEntry[]>(UnityWebRequest.kHttpVerbGET, action, null, null, ct);
        }

        /// <summary>The session's roster (player ids). Any roster member may read.</summary>
        public static Task<LelwareResult<string[]>> GetSessionPlayersAsync(
            this LelwareClient client, string sessionId, CancellationToken ct = default)
        {
            var action = "GameSession/GetSessionPlayers?sessionId=" + Uri.EscapeDataString(sessionId ?? "");
            return client.SendAsync<string[]>(UnityWebRequest.kHttpVerbGET, action, null, null, ct);
        }

        // --- wire DTOs ---------------------------------------------------------

        /// <summary>One key/value entry of a session's KV store.</summary>
        [Serializable]
        public sealed class SessionDataEntry
        {
            [JsonProperty("key")] public string Key;
            [JsonProperty("value")] public string Value;
        }
    }
}
