using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Client helper for the portal's Triton (inference-server) endpoint resolution
    ///     (<c>GET api/{pid}/Triton/Endpoint</c>). It answers ONE question: HOW should this player
    ///     reach Triton right now — straight to an on-prem box on the LAN, or through the portal's
    ///     own gRPC ingress. The actual inference is a gRPC call the app makes with its own Triton
    ///     client against the returned address; the SDK only resolves WHERE to dial (a gRPC channel
    ///     isn't something UnityWebRequest can carry, so the SDK stops at the address).
    ///
    ///     <para>HAND-WRITTEN (not generated): the resolution itself is a plain JSON GET, but it's
    ///     surfaced as one dedicated, clearly-named method (and kept out of the generated surface via
    ///     the SDK doc filter) so it reads as a first-class feature alongside storage / matchmaking /
    ///     game-sessions rather than a raw generated <c>EndpointAsync</c>.</para>
    ///
    ///     <para><b>Usage:</b></para>
    ///     <code>
    ///     var r = await client.GetTritonEndpointAsync();
    ///     if (r.Ok) switch (r.Data.Mode)
    ///     {
    ///         case TritonAccessMode.Direct: DialGrpc(r.Data.GrpcUrl); break;   // LAN box
    ///         case TritonAccessMode.Proxy:  DialGrpc(r.Data.GrpcUrl); break;   // portal gRPC ingress
    ///         case TritonAccessMode.Unavailable: /* no inference available */  break;
    ///     }
    ///     </code>
    /// </summary>
    public static class TritonEndpoints
    {
        /// <summary>
        ///     Resolve where/how this player should reach Triton. Returns a
        ///     <see cref="LelwareResult{TritonEndpointInfo}" /> — never throws. The caller must be
        ///     logged in and a player of the project (the answer is tenant-scoped to the project's
        ///     owning org).
        /// </summary>
        public static Task<LelwareResult<TritonEndpointInfo>> GetTritonEndpointAsync(
            this LelwareClient client, CancellationToken ct = default)
        {
            return client.SendAsync<TritonEndpointInfo>(
                UnityWebRequest.kHttpVerbGET, "Triton/Endpoint", dataKey: null, body: null, ct);
        }

        // --- wire DTOs (mirror the portal's TritonEndpointInfo / TritonAccessMode) -----------------

        /// <summary>
        ///     How the client should reach Triton. Values MATCH the portal enum (serialized as its
        ///     numeric value): <see cref="Unavailable" />=0, <see cref="Proxy" />=1,
        ///     <see cref="Direct" />=2.
        /// </summary>
        public enum TritonAccessMode
        {
            /// <summary>No Triton available for this project right now.</summary>
            Unavailable = 0,

            /// <summary>Dial the portal's own gRPC ingress (<see cref="TritonEndpointInfo.GrpcUrl" />).</summary>
            Proxy = 1,

            /// <summary>Dial an on-prem box directly on the LAN (<see cref="TritonEndpointInfo.GrpcUrl" />).</summary>
            Direct = 2
        }

        /// <summary>Resolved Triton access for this player (payload of <see cref="GetTritonEndpointAsync" />).</summary>
        [Serializable]
        public sealed class TritonEndpointInfo
        {
            [JsonProperty("mode")] public TritonAccessMode Mode;

            /// <summary>The gRPC endpoint to dial (portal ingress for Proxy, LAN address for Direct). Null when Unavailable.</summary>
            [JsonProperty("grpcUrl")] public string GrpcUrl;

            /// <summary>Optional on-prem HTTP/REST address — only meaningful for <see cref="TritonAccessMode.Direct" />.</summary>
            [JsonProperty("httpUrl")] public string HttpUrl;
        }
    }
}
