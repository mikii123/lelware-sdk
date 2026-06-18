using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lelware.Sdk.Http;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Entry point for talking to the LelwarePortal client API from Unity.
    ///
    ///     Lifecycle: construct once with a <see cref="LelwareClientConfig" />, call
    ///     <see cref="LoginAsync" /> (which caches the bearer token in memory), then make
    ///     authenticated calls. Two header invariants are enforced for EVERY request:
    ///       • <c>Is-Client: api-client</c> — always, so the portal treats us as an API
    ///         client (no auth cookie, honest device handling — see DeviceIdMiddleware).
    ///       • <c>Authorization: Bearer {token}</c> — once logged in, on every call.
    ///
    ///     The client is transport-only: it serializes a request object to JSON, sends it
    ///     via <see cref="UnityWebRequest" /> (awaited through our awaiter wrapper), and
    ///     deserializes the JSON response into the type you ask for. Typed endpoint classes
    ///     are generated from a schema manifest (see the Editor generator); for anything not
    ///     in the manifest, the generic <see cref="CallScriptAsync{TRequest,TResponse}" />
    ///     escape hatch lets you bring your own request/response types.
    /// </summary>
    public sealed class LelwareClient
    {
        public const string ClientHeaderName = "Is-Client";
        public const string ClientHeaderValue = "api-client";
        public const string DeviceHeaderName = "X-Device-Id";

        private readonly LelwareClientConfig _config;

        // Cached after a successful login. In memory only by default — call
        // PersistToken/TryRestoreToken if you want it to survive an app restart.
        private string _accessToken;
        private DateTime _expiresAtUtc = DateTime.MinValue;

        public LelwareClient(LelwareClientConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            ProjectId = config.ProjectId; // seed the live project id from config.
        }

        public LelwareClientConfig Config => _config;

        /// <summary>
        ///     Sink for the request log (see <see cref="LelwareClientConfig.EnableRequestLogging" />).
        ///     Defaults to <see cref="Debug.Log" />; assign your own (e.g. to route into an in-game
        ///     console or a file) or set it to a no-op to silence a specific instance. Only invoked
        ///     when logging is enabled in the config, so leaving it as-is is harmless in a shipped build.
        /// </summary>
        public Action<string> Logger { get; set; } = Debug.Log;

        /// <summary>
        ///     The project id used to build EVERY request URL (<c>/api/{ProjectId}/...</c>). It is
        ///     never passed per call — generated methods, the escape hatch, and login all read it
        ///     from here — so a single client serves whichever project this points at. Seeded from
        ///     <see cref="LelwareClientConfig.ProjectId" /> but mutable, so one client can be
        ///     re-pointed at another project at runtime: set it, then call <see cref="LoginAsync" />
        ///     again if the new project needs the caller linked as a player there (the bearer token
        ///     itself is per-user, not per-project, so it carries over). A null/blank value is
        ///     tolerated (calls just resolve to an empty segment) to keep the API exception-free.
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>True once a token is cached and not yet past its known expiry.</summary>
        public bool IsAuthenticated =>
            !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAtUtc;

        /// <summary>The cached bearer token, or null if not logged in. Exposed for persistence.</summary>
        public string AccessToken => _accessToken;

        public DateTime ExpiresAtUtc => _expiresAtUtc;

        // --- Authentication ----------------------------------------------------

        /// <summary>
        ///     Logs in against <c>api/{pid}/Authentication/Login</c> and, on success, caches the
        ///     returned bearer token for all subsequent calls. Never throws: bad credentials
        ///     (401), a transport failure, a malformed response, or a 200-without-token all come
        ///     back as a <see cref="LelwareResult{LoginResult}" /> with <c>Error == true</c> (and
        ///     the relevant <see cref="LelwareResult.Code" />). On success the parsed
        ///     <see cref="LoginResult" /> is in <see cref="LelwareResult{T}.Data" />.
        /// </summary>
        public async Task<LelwareResult<LoginResult>> LoginAsync(string email, string password, CancellationToken ct = default)
        {
            var url = $"{_config.BaseUrl}/api/{Uri.EscapeDataString(ProjectId ?? string.Empty)}/Authentication/Login";
            var bodyJson = JsonConvert.SerializeObject(new { Email = email, Password = password });

            // Login is the one call made WITHOUT a bearer token (we don't have one yet),
            // but it still carries Is-Client so the portal treats us as an API client.
            var raw = await SendRawAsync(UnityWebRequest.kHttpVerbPOST, url, bodyJson, includeAuth: false, ct);

            // Carry the transport/status outcome through unchanged; only proceed to parse on 2xx.
            var result = new LelwareResult<LoginResult>
            {
                Error = raw.Error, Code = raw.Code, Message = raw.Message, RawBody = raw.RawBody
            };
            if (raw.Error)
            {
                return result;
            }

            LoginResult login;
            try
            {
                login = LoginResult.Parse(raw.RawBody);
            }
            catch (Exception ex)
            {
                result.Error = true;
                result.Message = "Failed to parse login response: " + ex.Message;
                return result;
            }

            if (login.Token == null || string.IsNullOrEmpty(login.Token.AccessToken))
            {
                // Server returned 200 but no usable token (e.g. an OnLogin payload error):
                // surface the payload error, but keep the (successful) status code.
                result.Error = true;
                result.Message = login.Payload?.Error ?? "Login did not return an access token.";
                return result;
            }

            _accessToken = login.Token.AccessToken;
            _expiresAtUtc = login.ExpiresAtUtc;
            result.Data = login;
            return result;
        }

        /// <summary>
        ///     Registers against <c>api/{pid}/Authentication/Register</c>. The portal's register
        ///     response is the SAME dual-payload, NOT-valid-JSON-on-its-own shape as login:
        ///     (optional) <see cref="AccessTokenResponse" /> JSON, then the <c>||Response:</c>
        ///     marker, then the OnRegister script's payload — with that script's return value
        ///     verbatim in <see cref="LoginPayload.CustomData" />. We split on the marker and
        ///     deserialize each half (see <see cref="LoginResult.Parse" />), exactly like login.
        ///
        ///     Never throws — same contract as <see cref="LoginAsync" />: a non-2xx, transport
        ///     failure, malformed body, or an OnRegister <see cref="LoginPayload.Error" /> all
        ///     come back as a <see cref="LelwareResult{LoginResult}" /> with <c>Error == true</c>.
        ///     Unlike login, a token is NOT required: if the portal issues one (register auto-
        ///     logs-in), it is cached for subsequent calls; if it doesn't, the call still succeeds
        ///     with only <see cref="LoginResult.Payload" /> populated. The parsed
        ///     <see cref="LoginResult" /> is in <see cref="LelwareResult{T}.Data" /> on success.
        /// </summary>
        public async Task<LelwareResult<LoginResult>> RegisterAsync(string email, string password, CancellationToken ct = default)
        {
            var url = $"{_config.BaseUrl}/api/{Uri.EscapeDataString(ProjectId ?? string.Empty)}/Authentication/Register";
            var bodyJson = JsonConvert.SerializeObject(new { Email = email, Password = password });

            // Like login, register runs WITHOUT a bearer token (we don't have one yet) but still
            // carries Is-Client so the portal treats us as an API client.
            var raw = await SendRawAsync(UnityWebRequest.kHttpVerbPOST, url, bodyJson, includeAuth: false, ct);

            var result = new LelwareResult<LoginResult>
            {
                Error = raw.Error, Code = raw.Code, Message = raw.Message, RawBody = raw.RawBody
            };
            if (raw.Error)
            {
                return result;
            }

            LoginResult register;
            try
            {
                register = LoginResult.Parse(raw.RawBody);
            }
            catch (Exception ex)
            {
                result.Error = true;
                result.Message = "Failed to parse register response: " + ex.Message;
                return result;
            }

            // An OnRegister script can report a failure via the payload's Error field even on a
            // 2xx (e.g. a duplicate account or a validation rule the script enforces) — surface
            // it, but keep the (successful) status code so callers can still inspect it.
            if (!string.IsNullOrEmpty(register.Payload?.Error))
            {
                result.Error = true;
                result.Message = register.Payload.Error;
                return result;
            }

            // Register MAY auto-login. Cache the token only if one actually came back; its
            // absence is NOT an error here (the whole point that sets register apart from login).
            if (register.Token != null && !string.IsNullOrEmpty(register.Token.AccessToken))
            {
                _accessToken = register.Token.AccessToken;
                _expiresAtUtc = register.ExpiresAtUtc;
            }

            result.Data = register;
            return result;
        }

        /// <summary>Drops the cached token. The next call will be unauthenticated (and likely 401).</summary>
        public void Logout()
        {
            _accessToken = null;
            _expiresAtUtc = DateTime.MinValue;
        }

        /// <summary>
        ///     Persists the current token to <see cref="PlayerPrefs" /> under <paramref name="key" />.
        ///     PlayerPrefs is plain-text on most platforms — fine for a short-lived bearer token,
        ///     but don't treat it as secure storage. Pair with <see cref="TryRestoreToken" /> on
        ///     startup to skip re-login while the token is still valid.
        /// </summary>
        public void PersistToken(string key = "lelware.token")
        {
            if (string.IsNullOrEmpty(_accessToken))
            {
                PlayerPrefs.DeleteKey(key);
                return;
            }

            // Store token + absolute expiry so a restore can reject an already-expired token.
            PlayerPrefs.SetString(key, $"{_expiresAtUtc.Ticks}|{_accessToken}");
            PlayerPrefs.Save();
        }

        /// <summary>
        ///     Restores a token saved by <see cref="PersistToken" />. Returns false (and caches
        ///     nothing) if there's no saved token or it has already expired.
        /// </summary>
        public bool TryRestoreToken(string key = "lelware.token")
        {
            var stored = PlayerPrefs.GetString(key, null);
            if (string.IsNullOrEmpty(stored))
            {
                return false;
            }

            var sep = stored.IndexOf('|');
            if (sep <= 0 || !long.TryParse(stored.Substring(0, sep), out var ticks))
            {
                return false;
            }

            var expires = new DateTime(ticks, DateTimeKind.Utc);
            if (DateTime.UtcNow >= expires)
            {
                return false;
            }

            _accessToken = stored.Substring(sep + 1);
            _expiresAtUtc = expires;
            return true;
        }

        // --- Generic call surface ---------------------------------------------

        /// <summary>
        ///     Calls a custom project script via <c>POST api/{pid}/RunScript/{route}</c>,
        ///     serializing <paramref name="request" /> as the JSON body and deserializing the
        ///     response into <typeparamref name="TResponse" />. This is the "bring your own
        ///     schema" escape hatch — use it directly with hand-written request/response
        ///     classes, or let the generator emit typed wrappers around it.
        /// </summary>
        public async Task<LelwareResult<TResponse>> CallScriptAsync<TRequest, TResponse>(string route, TRequest request, CancellationToken ct = default)
        {
            string bodyJson;
            try
            {
                bodyJson = JsonConvert.SerializeObject(request);
            }
            catch (Exception ex)
            {
                // Serialization failures are a local (pre-flight) error: Code 0, never sent.
                return new LelwareResult<TResponse>
                {
                    Error = true, Code = 0, Message = "Failed to serialize request: " + ex.Message
                };
            }

            // Custom scripts live under the RunScript action; the route is its {dataKey} segment.
            // (Symmetric with GetScriptAsync, which prefixes the same way.)
            return await SendAsync<TResponse>(UnityWebRequest.kHttpVerbPOST, "RunScript", dataKey: route, bodyJson, ct);
        }

        /// <summary>
        ///     Calls a custom script via <c>GET api/{pid}/RunScript/{route}?...</c>. Parameters
        ///     go on the query string (the portal smart-types them: number/bool/string). Use
        ///     for scripts that must be GET (e.g. tile layers).
        /// </summary>
        public Task<LelwareResult<TResponse>> GetScriptAsync<TResponse>(string route, IReadOnlyDictionary<string, string> query = null, CancellationToken ct = default)
        {
            var path = "RunScript/" + Uri.EscapeDataString(route) + BuildQuery(query);
            return SendAsync<TResponse>(UnityWebRequest.kHttpVerbGET, path, dataKey: null, body: null, ct);
        }

        /// <summary>
        ///     Low-level typed call. <paramref name="action" /> is the controller action (and
        ///     may include extra path/query segments already escaped); <paramref name="dataKey" />
        ///     is the optional trailing route segment many client endpoints take. Returns a
        ///     <see cref="LelwareResult{TResponse}" /> — never throws. <see cref="LelwareResult{T}.Data" />
        ///     is the deserialized body, or <c>default</c> for an empty 2xx response.
        /// </summary>
        public async Task<LelwareResult<TResponse>> SendAsync<TResponse>(string verb, string action, string dataKey, string body, CancellationToken ct = default)
        {
            var url = BuildUrl(action, dataKey);
            var raw = await SendRawAsync(verb, url, body, includeAuth: true, ct);
            return Deserialize<TResponse>(raw);
        }

        /// <summary>
        ///     Bodyless variant for endpoints whose 2xx body is empty (<c>Ok()</c>). Returns a
        ///     plain <see cref="LelwareResult" /> carrying only the status/error — never throws.
        /// </summary>
        public async Task<LelwareResult> SendAsync(string verb, string action, string dataKey, string body, CancellationToken ct = default)
        {
            var url = BuildUrl(action, dataKey);
            return await SendRawAsync(verb, url, body, includeAuth: true, ct);
        }

        /// <summary>
        ///     Typed call to an ABSOLUTE API path (e.g. <c>/api/maps/tiles/1/2/3.vector</c>),
        ///     with all route/query substitution already baked into <paramref name="relativePath" />.
        ///     This is what the OpenAPI-generated methods target: it doesn't assume the
        ///     <c>/api/{ProjectId}/{action}</c> shape, so it handles endpoints with many path params
        ///     or no project id at all. Returns the deserialized JSON body, or <c>default</c> for an
        ///     empty 2xx. Never throws.
        /// </summary>
        public async Task<LelwareResult<TResponse>> SendPathAsync<TResponse>(string verb, string relativePath, string body, CancellationToken ct = default)
        {
            var raw = await SendRawAsync(verb, _config.BaseUrl + relativePath, body, includeAuth: true, ct);
            return Deserialize<TResponse>(raw);
        }

        /// <summary>Bodyless absolute-path variant — for endpoints whose 2xx body is empty.</summary>
        public async Task<LelwareResult> SendPathAsync(string verb, string relativePath, string body, CancellationToken ct = default)
        {
            return await SendRawAsync(verb, _config.BaseUrl + relativePath, body, includeAuth: true, ct);
        }

        /// <summary>
        ///     Binary absolute-path call — for endpoints that return raw bytes rather than JSON
        ///     (e.g. vector map tiles, <c>application/x-protobuf</c>). The bytes land in
        ///     <see cref="LelwareResult{T}.Data" />; a 2xx with no body (e.g. a 201) yields null
        ///     data with <c>Error == false</c>. Never throws.
        /// </summary>
        public async Task<LelwareResult<byte[]>> SendBytesAsync(string verb, string relativePath, string body, CancellationToken ct = default)
        {
            return await SendRawBytesAsync(verb, _config.BaseUrl + relativePath, body, includeAuth: true, ct);
        }

        /// <summary>
        ///     Projects a raw (untyped) result onto a typed one, deserializing the body on
        ///     success. A failed raw result passes through untouched; a 2xx response with an
        ///     unparseable body is downgraded to <c>Error == true</c> (keeping the 2xx
        ///     <see cref="LelwareResult.Code" />) so a malformed payload never throws.
        /// </summary>
        private static LelwareResult<TResponse> Deserialize<TResponse>(LelwareResult raw)
        {
            var result = new LelwareResult<TResponse>
            {
                Error = raw.Error, Code = raw.Code, Message = raw.Message, RawBody = raw.RawBody
            };

            if (raw.Error || string.IsNullOrWhiteSpace(raw.RawBody))
            {
                return result;
            }

            try
            {
                result.Data = JsonConvert.DeserializeObject<TResponse>(raw.RawBody);
            }
            catch (Exception ex)
            {
                result.Error = true;
                result.Message = "Failed to parse response: " + ex.Message;
            }

            return result;
        }

        // --- Request logging ---------------------------------------------------

        /// <summary>
        ///     Logs an outgoing request, if <see cref="LelwareClientConfig.EnableRequestLogging" /> is on.
        ///     Emits verb + URL, the headers it carries (the bearer token is masked unless
        ///     <see cref="LelwareClientConfig.LogRequestBodies" /> is on), and the body only when
        ///     <see cref="LelwareClientConfig.LogRequestBodies" /> is also on — bodies may carry the
        ///     login password, so they're opt-in. Guarded so it's a no-op (and allocation-free) on the
        ///     hot path when logging is disabled.
        /// </summary>
        internal void LogRequest(string verb, string url, string body,
            IEnumerable<KeyValuePair<string, string>> headers = null)
        {
            if (!_config.EnableRequestLogging || Logger == null)
            {
                return;
            }

            var line = $"[Lelware] → {verb} {url}";
            if (headers != null)
            {
                foreach (var h in headers)
                {
                    line += $"\n  {h.Key}: {h.Value}";
                }
            }

            if (_config.LogRequestBodies && !string.IsNullOrEmpty(body))
            {
                line += $"\n  body: {body}";
            }

            Logger(line);
        }

        /// <summary>
        ///     Logs the outcome of a request (the same arrow notation, reversed). Reports the status
        ///     code and, on failure, the transport error; includes the response body only when
        ///     <see cref="LelwareClientConfig.LogRequestBodies" /> is on. No-op when logging is disabled.
        /// </summary>
        internal void LogResponse(string verb, string url, bool error, long code, string message, string responseBody)
        {
            if (!_config.EnableRequestLogging || Logger == null)
            {
                return;
            }

            var line = error
                ? $"[Lelware] ← {verb} {url} FAILED ({code}): {message}"
                : $"[Lelware] ← {verb} {url} OK ({code})";
            if (_config.LogRequestBodies && !string.IsNullOrEmpty(responseBody))
            {
                line += $"\n  body: {responseBody}";
            }

            Logger(line);
        }

        // --- HTTP plumbing -----------------------------------------------------

        private string BuildUrl(string action, string dataKey)
        {
            var sb = new StringBuilder(_config.BaseUrl);
            sb.Append("/api/").Append(Uri.EscapeDataString(ProjectId ?? string.Empty)).Append('/').Append(action);
            if (!string.IsNullOrEmpty(dataKey))
            {
                sb.Append('/').Append(Uri.EscapeDataString(dataKey));
            }

            return sb.ToString();
        }

        private static string BuildQuery(IReadOnlyDictionary<string, string> query)
        {
            if (query == null || query.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder("?");
            var first = true;
            foreach (var kvp in query)
            {
                if (!first)
                {
                    sb.Append('&');
                }

                first = false;
                sb.Append(Uri.EscapeDataString(kvp.Key)).Append('=').Append(Uri.EscapeDataString(kvp.Value ?? string.Empty));
            }

            return sb.ToString();
        }

        /// <summary>
        ///     The single chokepoint every request flows through. Sets the two invariant
        ///     headers, applies the JSON body (if any), awaits the op on the main thread, and
        ///     reports any non-2xx / transport failure / cancellation as a <see cref="LelwareResult" />
        ///     with <c>Error == true</c> — it never throws. The raw response text (which may be a
        ///     portal error string) is always returned in <see cref="LelwareResult.RawBody" />.
        /// </summary>
        private async Task<LelwareResult> SendRawAsync(string verb, string url, string body, bool includeAuth, CancellationToken ct)
        {
            // 'using' so the native request + its handlers are disposed even on throw.
            using var request = new UnityWebRequest(url, verb)
            {
                downloadHandler = new DownloadHandlerBuffer()
            };
            var headers = ConfigureRequest(request, body, includeAuth);
            LogRequest(verb, url, body, headers);

            // Register cancellation: abort the in-flight request, which completes the op
            // with a Result of ConnectionError so the await below unblocks.
            using var registration = ct.CanBeCanceled
                ? ct.Register(() => request.Abort())
                : default;

            await request.SendWebRequest();

            // Cancellation aborts the request (-> ConnectionError above); report it as an error
            // result rather than throwing OperationCanceledException, keeping the API throw-free.
            if (ct.IsCancellationRequested)
            {
                LogResponse(verb, url, error: true, code: 0, message: "cancelled", responseBody: null);
                return new LelwareResult
                {
                    Error = true, Code = 0, Message = $"{verb} {url} was cancelled.", RawBody = null
                };
            }

            var responseBody = request.downloadHandler != null ? request.downloadHandler.text : null;

#if UNITY_2020_2_OR_NEWER
            var ok = request.result == UnityWebRequest.Result.Success;
#else
            var ok = !request.isHttpError && !request.isNetworkError;
#endif
            if (!ok)
            {
                // responseCode is 0 for a transport failure that never reached the server.
                LogResponse(verb, url, error: true, request.responseCode, request.error, responseBody);
                return new LelwareResult
                {
                    Error = true,
                    Code = request.responseCode,
                    Message = $"{verb} {url} failed: {request.error}",
                    RawBody = responseBody
                };
            }

            LogResponse(verb, url, error: false, request.responseCode, null, responseBody);
            return new LelwareResult
            {
                Error = false, Code = request.responseCode, Message = null, RawBody = responseBody
            };
        }

        /// <summary>
        ///     Binary counterpart of <see cref="SendRawAsync" /> for endpoints that return raw bytes
        ///     (e.g. vector map tiles, <c>application/x-protobuf</c>). Same header /
        ///     cancellation / error contract; the payload comes back in <see cref="LelwareResult{T}.Data" />,
        ///     and a 2xx with no body yields null data with <c>Error == false</c>. On a non-2xx the
        ///     (usually plain-text) error body is surfaced via <see cref="LelwareResult.RawBody" />.
        /// </summary>
        private async Task<LelwareResult<byte[]>> SendRawBytesAsync(string verb, string url, string body, bool includeAuth, CancellationToken ct)
        {
            using var request = new UnityWebRequest(url, verb)
            {
                downloadHandler = new DownloadHandlerBuffer()
            };
            var headers = ConfigureRequest(request, body, includeAuth);
            LogRequest(verb, url, body, headers);

            using var registration = ct.CanBeCanceled
                ? ct.Register(() => request.Abort())
                : default;

            await request.SendWebRequest();

            if (ct.IsCancellationRequested)
            {
                LogResponse(verb, url, error: true, code: 0, message: "cancelled", responseBody: null);
                return new LelwareResult<byte[]>
                {
                    Error = true, Code = 0, Message = $"{verb} {url} was cancelled."
                };
            }

#if UNITY_2020_2_OR_NEWER
            var ok = request.result == UnityWebRequest.Result.Success;
#else
            var ok = !request.isHttpError && !request.isNetworkError;
#endif
            if (!ok)
            {
                var errorBody = request.downloadHandler != null ? request.downloadHandler.text : null;
                LogResponse(verb, url, error: true, request.responseCode, request.error, errorBody);
                return new LelwareResult<byte[]>
                {
                    Error = true,
                    Code = request.responseCode,
                    Message = $"{verb} {url} failed: {request.error}",
                    RawBody = errorBody
                };
            }

            var data = request.downloadHandler != null ? request.downloadHandler.data : null;
            // Binary success: report byte count rather than the (binary) body, which isn't loggable text.
            LogResponse(verb, url, error: false, request.responseCode, null,
                _config.LogRequestBodies ? $"<{data?.Length ?? 0} bytes>" : null);
            return new LelwareResult<byte[]>
            {
                Error = false,
                Code = request.responseCode,
                Data = data
            };
        }

        /// <summary>
        ///     Applies the per-request invariants shared by the text and byte send paths: timeout,
        ///     the optional JSON body, the always-on <c>Is-Client</c> header, the optional device
        ///     header, and (when <paramref name="includeAuth" />) the bearer token. Factored out so
        ///     the header/body contract can never drift between the two transports.
        ///
        ///     <para>Returns the headers it set (for the request log), or <c>null</c> when logging is
        ///     off — building the list only when it'll be used keeps the hot path allocation-free.
        ///     UnityWebRequest has no read-back of set headers on every platform, so we mirror them
        ///     into the returned list as we set them — it can't drift from what's actually sent.</para>
        /// </summary>
        private List<KeyValuePair<string, string>> ConfigureRequest(UnityWebRequest request, string body, bool includeAuth)
        {
            // Only collect headers for the log when logging is actually on.
            var headers = _config.EnableRequestLogging && Logger != null
                ? new List<KeyValuePair<string, string>>()
                : null;

            if (_config.TimeoutSeconds > 0)
            {
                request.timeout = _config.TimeoutSeconds;
            }

            if (body != null)
            {
                var payload = Encoding.UTF8.GetBytes(body);
                request.uploadHandler = new UploadHandlerRaw(payload) { contentType = "application/json" };
                // Content-Type isn't set via SetRequestHeader (it rides on the upload handler), so
                // record it explicitly for the log to reflect the wire request faithfully.
                headers?.Add(new KeyValuePair<string, string>("Content-Type", "application/json"));
            }

            // Always identify as an API client.
            request.SetRequestHeader(ClientHeaderName, ClientHeaderValue);
            headers?.Add(new KeyValuePair<string, string>(ClientHeaderName, ClientHeaderValue));

            // Optional device id — only sent if the app supplied a stable one.
            if (!string.IsNullOrEmpty(_config.DeviceId))
            {
                request.SetRequestHeader(DeviceHeaderName, _config.DeviceId);
                headers?.Add(new KeyValuePair<string, string>(DeviceHeaderName, _config.DeviceId));
            }

            // Bearer token on everything except the login call itself.
            if (includeAuth && !string.IsNullOrEmpty(_accessToken))
            {
                request.SetRequestHeader("Authorization", "Bearer " + _accessToken);
                // The bearer token is a credential — mask it in the log unless body logging (the
                // "include sensitive details" switch) is explicitly on.
                headers?.Add(new KeyValuePair<string, string>("Authorization",
                    _config.LogRequestBodies ? "Bearer " + _accessToken : "Bearer ***"));
            }

            return headers;
        }
    }
}
