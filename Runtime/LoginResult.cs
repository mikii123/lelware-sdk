using System;
using Newtonsoft.Json;

namespace Lelware.Sdk
{
    /// <summary>
    ///     The bearer-token half of the portal's login response — the standard ASP.NET Core
    ///     Identity <c>AccessTokenResponse</c> shape (camelCase JSON). The portal writes this
    ///     to the body and then APPENDS its own custom payload after a <c>||Response:</c>
    ///     marker, so <see cref="LoginResult" /> splits the two before deserializing.
    /// </summary>
    [Serializable]
    public sealed class AccessTokenResponse
    {
        [JsonProperty("tokenType")] public string TokenType;
        [JsonProperty("accessToken")] public string AccessToken;
        [JsonProperty("expiresIn")] public long ExpiresIn;
        [JsonProperty("refreshToken")] public string RefreshToken;
    }

    /// <summary>
    ///     The portal-specific half appended after the <c>||Response:</c> marker on login.
    ///     <see cref="CustomData" /> is the JSON-serialized return value of the project's
    ///     OnLogin script (if any), passed through verbatim — deserialize it yourself if the
    ///     project defines one.
    /// </summary>
    [Serializable]
    public sealed class LoginPayload
    {
        [JsonProperty("PlayerID")] public string PlayerId;
        [JsonProperty("CustomData")] public string CustomData;
        [JsonProperty("Error")] public string Error;
    }

    /// <summary>
    ///     Outcome of <see cref="LelwareClient.LoginAsync" />: both halves of the portal's
    ///     dual-payload login response, plus the absolute UTC instant the access token
    ///     expires (derived from <c>expiresIn</c> at the moment of login).
    /// </summary>
    public sealed class LoginResult
    {
        public AccessTokenResponse Token { get; }
        public LoginPayload Payload { get; }
        public DateTime ExpiresAtUtc { get; }

        public string PlayerId => Payload?.PlayerId;

        public LoginResult(AccessTokenResponse token, LoginPayload payload, DateTime expiresAtUtc)
        {
            Token = token;
            Payload = payload;
            ExpiresAtUtc = expiresAtUtc;
        }

        // The portal writes:  {accessTokenJson}||Response:{loginPayloadJson}
        // Either half can be absent depending on how far login got, so parse defensively.
        internal const string Marker = "||Response:";

        internal static LoginResult Parse(string body)
        {
            string tokenJson = body;
            string payloadJson = null;

            var idx = body.IndexOf(Marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                tokenJson = body.Substring(0, idx);
                payloadJson = body.Substring(idx + Marker.Length);
            }

            AccessTokenResponse token = string.IsNullOrWhiteSpace(tokenJson)
                ? null
                : JsonConvert.DeserializeObject<AccessTokenResponse>(tokenJson);

            LoginPayload payload = string.IsNullOrWhiteSpace(payloadJson)
                ? null
                : JsonConvert.DeserializeObject<LoginPayload>(payloadJson);

            // expiresIn is seconds-from-now; pin it to an absolute instant so callers can
            // cheaply check expiry later without tracking when login happened.
            var expiresAt = token != null
                ? DateTime.UtcNow.AddSeconds(token.ExpiresIn)
                : DateTime.MinValue;

            return new LoginResult(token, payload, expiresAt);
        }
    }
}
