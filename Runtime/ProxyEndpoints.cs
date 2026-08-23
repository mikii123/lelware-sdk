using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Client helpers for the portal's per-project reverse-proxy
    ///     (<c>api/{pid}/Proxy/{target}/{**path}</c>). A proxy TARGET is configured on the portal
    ///     (base URL, allowed verbs, injected headers, and — crucially — a credential read from the
    ///     CALLING player's own <b>secret</b> player-data): so the client calls a third-party API
    ///     with its own session/token WITHOUT that credential ever living in the build. The caller
    ///     must be logged in and a player of the project; the target name selects which upstream.
    ///
    ///     <para>This is HAND-WRITTEN (not generated): the proxy is a transparent pass-through with
    ///     an arbitrary <c>{**path}</c> tail and arbitrary response bytes/content-type — not a fixed
    ///     JSON round-trip the OpenAPI generator could model — so it's excluded from the generated
    ///     surface (<c>[ApiExplorerSettings(IgnoreApi = true)]</c>) and lives here, same as storage.</para>
    ///
    ///     <para>Everything returns a <see cref="LelwareResult" /> — never throws. Pick the shape by
    ///     the upstream's content: <see cref="ProxyJsonAsync{T}" /> for a JSON body,
    ///     <see cref="ProxyBytesAsync" /> for binary (images, etc.), and
    ///     <see cref="ProxyBatchAsync" /> to fan many sub-requests out in ONE round-trip.</para>
    ///
    ///     <para><b>Usage:</b></para>
    ///     <code>
    ///     // one JSON GET through the "myapi" target:
    ///     var r = await client.ProxyJsonAsync&lt;ItemDto&gt;("myapi", "v1/items/123");
    ///     // an image:
    ///     var img = await client.ProxyBytesAsync("myapi", "images/thumb.jpg");
    ///     // 200 records in one call:
    ///     var batch = await client.ProxyBatchAsync("myapi", new ProxyBatchRequest {
    ///         Requests = { new ProxyBatchItem { Path = "v1/users/1" }, new ProxyBatchItem { Path = "v1/users/2" } }
    ///     });
    ///     </code>
    /// </summary>
    public static class ProxyEndpoints
    {
        /// <summary>
        ///     Proxy a request to <paramref name="target" /> and deserialize the JSON response into
        ///     <typeparamref name="T" />. <paramref name="path" /> is the upstream path (relative to
        ///     the target's configured base URL); optional <paramref name="query" /> becomes the query
        ///     string. Defaults to GET — pass <paramref name="method" /> for another verb (subject to
        ///     the target's allow-list). A JSON <paramref name="body" /> is sent as
        ///     <c>application/json</c> (for other content types, use <see cref="ProxyBatchAsync" />,
        ///     whose items carry an explicit content type).
        /// </summary>
        public static Task<LelwareResult<T>> ProxyJsonAsync<T>(
            this LelwareClient client, string target, string path,
            IReadOnlyDictionary<string, string> query = null, string method = null, string body = null, CancellationToken ct = default)
        {
            return client.SendPathAsync<T>(method ?? UnityWebRequest.kHttpVerbGET, ProxyPath(client, target, path, query), body, ct);
        }

        /// <summary>
        ///     Proxy a request and return the RAW response bytes (for images / binary upstreams). The
        ///     bytes land in <see cref="LelwareResult{T}.Data" />; the upstream status is in
        ///     <see cref="LelwareResult.Code" />. Same target/path/query/verb rules as
        ///     <see cref="ProxyJsonAsync{T}" />.
        /// </summary>
        public static Task<LelwareResult<byte[]>> ProxyBytesAsync(
            this LelwareClient client, string target, string path,
            IReadOnlyDictionary<string, string> query = null, string method = null, string body = null, CancellationToken ct = default)
        {
            return client.SendBytesAsync(method ?? UnityWebRequest.kHttpVerbGET, ProxyPath(client, target, path, query), body, ct);
        }

        /// <summary>
        ///     Fan-out: run many sub-requests against the SAME target in ONE round-trip
        ///     (<c>POST .../{target}/$batch</c>). The whole call is 200 even when individual items
        ///     fail — per-item status/body/error lives in <see cref="ProxyBatchResponse.Responses" />,
        ///     in the SAME order as the input. Text-ish bodies come back as strings; binary ones as
        ///     base64 (<see cref="ProxyBatchResponseItem.BodyBase64" /> set — use
        ///     <see cref="ProxyBatchResponseItem.GetBytes" />).
        /// </summary>
        public static Task<LelwareResult<ProxyBatchResponse>> ProxyBatchAsync(
            this LelwareClient client, string target, ProxyBatchRequest request, CancellationToken ct = default)
        {
            var body = JsonConvert.SerializeObject(request ?? new ProxyBatchRequest());
            // "$batch" is a literal route segment (it beats the {**path} catch-all server-side); '$'
            // is a valid path char, so it's appended un-escaped after the (escaped) target.
            var rel = ProxyBase(client, target) + "/$batch";
            return client.SendPathAsync<ProxyBatchResponse>(UnityWebRequest.kHttpVerbPOST, rel, body, ct);
        }

        /// <summary>
        ///     Invalidate this player's cached proxy responses for one resource path (and any
        ///     sub-path under it), scoped to this target: <c>POST .../{target}/$purge</c>. The escape
        ///     hatch after a mutation a cached READ can't see — so the next read reflects the new state
        ///     instead of the stale cached body. Only the caller's OWN cached entries are touched.
        /// </summary>
        public static Task<LelwareResult> ProxyPurgeAsync(
            this LelwareClient client, string target, string path, CancellationToken ct = default)
        {
            var body = JsonConvert.SerializeObject(new { path });
            return client.SendPathAsync(UnityWebRequest.kHttpVerbPOST, ProxyBase(client, target) + "/$purge", body, ct);
        }

        // --- url building ------------------------------------------------------

        // /api/{pid}/Proxy/{target}
        private static string ProxyBase(LelwareClient client, string target)
        {
            return "/api/" + Uri.EscapeDataString(client.ProjectId ?? string.Empty)
                   + "/Proxy/" + Uri.EscapeDataString(target ?? string.Empty);
        }

        // /api/{pid}/Proxy/{target}/{path}?{query} — the path is the {**path} tail, so its internal
        // slashes are preserved (only a leading slash is trimmed); the target is escaped as one segment.
        private static string ProxyPath(LelwareClient client, string target, string path, IReadOnlyDictionary<string, string> query)
        {
            var sb = new StringBuilder(ProxyBase(client, target));
            sb.Append('/').Append((path ?? string.Empty).TrimStart('/'));

            if (query != null)
            {
                var first = true;
                foreach (var kv in query)
                {
                    sb.Append(first ? '?' : '&');
                    first = false;
                    sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value ?? string.Empty));
                }
            }

            return sb.ToString();
        }

        // --- wire DTOs (camelCase to match the portal's System.Text.Json output) ------------------

        /// <summary>Body for <see cref="ProxyBatchAsync" />: the ordered list of sub-requests.</summary>
        [Serializable]
        public sealed class ProxyBatchRequest
        {
            [JsonProperty("requests")] public List<ProxyBatchItem> Requests = new List<ProxyBatchItem>();
        }

        /// <summary>One sub-request in a <see cref="ProxyBatchRequest" />.</summary>
        [Serializable]
        public sealed class ProxyBatchItem
        {
            /// <summary>Upstream path (may include its own query string), relative to the target's base URL.</summary>
            [JsonProperty("path")] public string Path;
            /// <summary>HTTP verb; defaults to GET server-side. Subject to the target's allowed methods.</summary>
            [JsonProperty("method")] public string Method;
            /// <summary>Optional query string ("a=b" or "?a=b"), appended if <see cref="Path" /> has none.</summary>
            [JsonProperty("query")] public string Query;
            /// <summary>Content-Type for <see cref="Body" />, if any.</summary>
            [JsonProperty("contentType")] public string ContentType;
            /// <summary>Request body as text (or base64 when <see cref="BodyBase64" />). Ignored for GET/HEAD/DELETE.</summary>
            [JsonProperty("body")] public string Body;
            [JsonProperty("bodyBase64")] public bool BodyBase64;
            /// <summary>Optional extra request headers (the blocked/sensitive set is still stripped server-side).</summary>
            [JsonProperty("headers")] public Dictionary<string, string> Headers;
        }

        /// <summary>Response envelope for <see cref="ProxyBatchAsync" />.</summary>
        [Serializable]
        public sealed class ProxyBatchResponse
        {
            [JsonProperty("responses")] public ProxyBatchResponseItem[] Responses;
        }

        /// <summary>One sub-response, in the same order as the request items.</summary>
        [Serializable]
        public sealed class ProxyBatchResponseItem
        {
            [JsonProperty("path")] public string Path;
            [JsonProperty("status")] public int Status;
            [JsonProperty("contentType")] public string ContentType;
            /// <summary>Response body as text, or base64 when <see cref="BodyBase64" /> (binary like images).</summary>
            [JsonProperty("body")] public string Body;
            [JsonProperty("bodyBase64")] public bool BodyBase64;
            /// <summary>Rewritten same-target redirect Location, if the upstream returned one.</summary>
            [JsonProperty("location")] public string Location;
            /// <summary>Set instead of a body when this single sub-request failed (host pin, verb, upstream error).</summary>
            [JsonProperty("error")] public string Error;

            /// <summary>Decode <see cref="Body" /> to bytes — handles both the text and base64 cases.</summary>
            public byte[] GetBytes()
            {
                if (Body == null) return null;
                return BodyBase64 ? Convert.FromBase64String(Body) : Encoding.UTF8.GetBytes(Body);
            }
        }
    }
}
