using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lelware.Sdk.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Sidecar WORKER client — attaches this process to a project's sidecar queues and executes
    ///     jobs, over the low-latency WebSocket control channel (<c>api/{pid}/Sidecar/Connect</c>).
    ///     The dispatch model is push-to-wake + pull-to-claim: the portal pushes a tiny <c>wake</c>
    ///     the instant a job lands, the worker atomically <c>claim</c>s it, runs your handler, and
    ///     reports <c>complete</c>/<c>fail</c>; a poll loop drains missed jobs and a progress loop
    ///     keeps in-flight leases alive. Reconnect is automatic with capped backoff.
    ///
    ///     <para>This is HAND-WRITTEN (not generated): the whole thing is a stateful full-duplex WS
    ///     protocol, not a JSON round-trip the OpenAPI generator could model — so the Sidecar
    ///     controller is <c>[ApiExplorerSettings(IgnoreApi = true)]</c> and the worker lives here,
    ///     same reason storage / realtime / matchmaking are hand-written.</para>
    ///
    ///     <para><b>Auth is an API KEY, not a player login</b> (<c>X-Api-Key</c>): a worker is trusted
    ///     infrastructure, so it authenticates with the portal-global key OR the owning org's key —
    ///     NOT a bearer token. That key is a secret: only run a worker where you can hold it safely
    ///     (a server / a trusted machine / a headless build), NOT in a shipped game client.</para>
    ///
    ///     <para><b>Threading:</b> the socket + your handler run on background threads (a job is CPU/
    ///     GPU work you don't want on the render thread). Your handler therefore must NOT touch Unity
    ///     APIs directly — marshal to the main thread yourself if it needs to. Companion to
    ///     <see cref="LelwareClient" /> (it supplies base URL + project id) and reuses the same WS
    ///     transport as <see cref="LelwareRealtime" /> (<see cref="ILelwareRealtimeSocket" />).</para>
    ///
    ///     <para><b>Usage:</b></para>
    ///     <code>
    ///     var worker = new LelwareSidecar(client, apiKey: "…", node: "gpu-1",
    ///         queues: new[] { "densify" },
    ///         handler: async (job, ct) =>
    ///         {
    ///             var input = job.PayloadAs&lt;MyInput&gt;();
    ///             var output = await DoWork(input, ct);
    ///             return output;                 // serialized as the job's result JSON
    ///         });
    ///     worker.Start();
    ///     // …later…
    ///     worker.Stop();
    ///     </code>
    /// </summary>
    public sealed class LelwareSidecar : IDisposable
    {
        /// <summary>Header the worker key rides on (matches the portal's <c>ApiKey.HeaderName</c>).</summary>
        public const string ApiKeyHeaderName = "X-Api-Key";

        private readonly LelwareClient _client;
        private readonly string _apiKey;
        private readonly string _node;
        private readonly string[] _queues;
        private readonly Func<SidecarJob, CancellationToken, Task<object>> _handler;
        private readonly Func<ILelwareRealtimeSocket> _socketFactory;

        private readonly object _gate = new object();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, byte> _inFlight = new ConcurrentDictionary<string, byte>();

        private CancellationTokenSource _cts;
        private ILelwareRealtimeSocket _socket; // the CURRENT connection's socket (null when down)
        private bool _running;
        private bool _connected;
        private int _backoffMs = 1000;

        // Cadence from the welcome frame (the project's per-queue timings); sane defaults until then.
        private double _pollSeconds = 5;
        private double _progressSeconds = 10;

        /// <param name="client">Portal client — supplies base URL + project id (need NOT be logged in; the worker uses an API key).</param>
        /// <param name="apiKey">Worker key (portal-global or the owning org's) sent as <c>X-Api-Key</c>.</param>
        /// <param name="node">Stable worker name (upserted to a durable node id server-side).</param>
        /// <param name="queues">The queue names this worker serves.</param>
        /// <param name="handler">
        ///     Runs one job and returns its result (serialized as the job's result JSON, or null for
        ///     none). THROW to fail the job — it's re-queued while retries remain, else dead-lettered.
        ///     Runs on a background thread; don't touch Unity APIs directly from it.
        /// </param>
        /// <param name="socketFactory">Optional WS transport factory (defaults to <see cref="ClientWebSocketTransport" />; override for WebGL).</param>
        public LelwareSidecar(
            LelwareClient client, string apiKey, string node, IEnumerable<string> queues,
            Func<SidecarJob, CancellationToken, Task<object>> handler, Func<ILelwareRealtimeSocket> socketFactory = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _apiKey = apiKey;
            _node = string.IsNullOrWhiteSpace(node) ? "unity-worker" : node.Trim();
            _queues = Normalize(queues);
            _socketFactory = socketFactory ?? (() => new ClientWebSocketTransport());
        }

        /// <summary>True while the worker socket is connected and has received its welcome frame.</summary>
        public bool IsConnected { get { lock (_gate) { return _connected; } } }

        // --- lifecycle ---------------------------------------------------------

        /// <summary>Open the connection and start the background receive + poll + progress loops. Idempotent.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _running = true;
                _cts = new CancellationTokenSource();
            }

            _ = RunAsync(_cts.Token);
        }

        /// <summary>Stop for good (no reconnect) and release the socket. Idempotent.</summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                _connected = false;
                cts = _cts;
                _cts = null;
            }

            try { cts?.Cancel(); } catch { /* ignore */ }
            try { cts?.Dispose(); } catch { /* ignore */ }
        }

        public void Dispose() => Stop();

        // --- producer (HTTP) ---------------------------------------------------

        /// <summary>
        ///     Enqueue a job onto <paramref name="queue" /> (the server-to-server producer path,
        ///     <c>POST api/{pid}/Sidecar/Enqueue</c>) — wakes the queue's workers immediately.
        ///     <paramref name="payload" /> is serialized to JSON and stored verbatim. Authenticated
        ///     with the same worker <c>X-Api-Key</c>. Returns the new job id in
        ///     <see cref="LelwareResult{T}.Data" />; never throws. Call from the Unity main thread
        ///     (it uses <see cref="UnityWebRequest" />).
        /// </summary>
        public async Task<LelwareResult<string>> EnqueueAsync(string queue, object payload = null, CancellationToken ct = default)
        {
            var url = (_client.BaseUrl ?? string.Empty)
                      + "/api/" + Uri.EscapeDataString(_client.ProjectId ?? string.Empty) + "/Sidecar/Enqueue";
            var body = JsonConvert.SerializeObject(new { queue, payload });

            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)) { contentType = "application/json" },
                downloadHandler = new DownloadHandlerBuffer()
            };
            request.SetRequestHeader(ApiKeyHeaderName, _apiKey ?? string.Empty);
            if (_client.Config.TimeoutSeconds > 0) request.timeout = _client.Config.TimeoutSeconds;

            using var registration = ct.CanBeCanceled ? ct.Register(() => request.Abort()) : default;
            await request.SendWebRequest();

            if (ct.IsCancellationRequested)
                return new LelwareResult<string> { Error = true, Code = 0, Message = "Enqueue was cancelled." };

#if UNITY_2020_2_OR_NEWER
            var ok = request.result == UnityWebRequest.Result.Success;
#else
            var ok = !request.isHttpError && !request.isNetworkError;
#endif
            var responseBody = request.downloadHandler != null ? request.downloadHandler.text : null;
            if (!ok)
            {
                return new LelwareResult<string>
                {
                    Error = true, Code = request.responseCode,
                    Message = $"Enqueue failed ({request.responseCode}): {request.error}", RawBody = responseBody
                };
            }

            var result = new LelwareResult<string> { Error = false, Code = request.responseCode, RawBody = responseBody };
            try { result.Data = JObject.Parse(responseBody ?? "{}")["jobId"]?.ToString(); }
            catch (Exception ex) { result.Error = true; result.Message = "Failed to parse enqueue response: " + ex.Message; }
            return result;
        }

        // --- background socket loop -------------------------------------------

        private async Task RunAsync(CancellationToken ct)
        {
            var uri = BuildConnectUri();
            var headers = new Dictionary<string, string> { { ApiKeyHeaderName, _apiKey ?? string.Empty } };

            while (!ct.IsCancellationRequested)
            {
                ILelwareRealtimeSocket socket = null;
                // Per-connection token: cancelled in the finally so this session's poll/progress loops
                // stop the instant the socket drops (they restart on the next connection's welcome).
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    socket = _socketFactory();
                    await socket.ConnectAsync(uri, headers, ct);
                    lock (_gate) { _socket = socket; }
                    await ReceiveLoop(socket, sessionCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break; // Stop()
                }
                catch (Exception ex)
                {
                    Log("Sidecar: connection error: " + ex.Message);
                }
                finally
                {
                    try { sessionCts.Cancel(); } catch { /* ignore */ }
                    lock (_gate) { _socket = null; _connected = false; }
                    socket?.Dispose();
                }

                if (ct.IsCancellationRequested) break;

                int delay;
                lock (_gate) { delay = _backoffMs; _backoffMs = Math.Min(_backoffMs * 2, 30000); }
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task ReceiveLoop(ILelwareRealtimeSocket socket, CancellationToken ct)
        {
            var session = new Session();
            while (!ct.IsCancellationRequested)
            {
                var json = await socket.ReceiveAsync(ct);
                if (json == null) return; // peer closed
                await HandleFrame(json, session, ct);
            }
        }

        private async Task HandleFrame(string json, Session session, CancellationToken ct)
        {
            JObject msg;
            try { msg = JObject.Parse(json); } catch { return; }

            var type = (string)msg["type"];
            switch (type)
            {
                case "welcome":
                    _pollSeconds = (double?)msg["pollSeconds"] ?? _pollSeconds;
                    _progressSeconds = (double?)msg["progressSeconds"] ?? _progressSeconds;
                    lock (_gate) { _connected = true; _backoffMs = 1000; } // healthy — reset backoff
                    await DrainAsync(ct);
                    if (!session.TimersStarted)
                    {
                        session.TimersStarted = true;
                        _ = PollLoop(ct);
                        _ = ProgressLoop(ct);
                    }
                    break;

                case "wake":
                    // A job landed — claim the exact one the wake named (the fast path).
                    var wakeId = (string)msg["jobId"];
                    if (!string.IsNullOrEmpty(wakeId)) await SendJsonAsync(new { type = "claim", jobId = wakeId }, ct);
                    break;

                case "granted":
                    if (msg["job"] is JObject job) StartProcessing(job, ct);
                    break;

                case "taken":
                    break; // lost the race — another worker got it; wait for the next wake

                case "ack":
                    var ackId = (string)msg["jobId"];
                    if (!string.IsNullOrEmpty(ackId)) _inFlight.TryRemove(ackId, out _);
                    break;

                case "ping":
                    await SendJsonAsync(new { type = "pong" }, ct);
                    break;

                case "error":
                    Log("Sidecar: server error: " + (string)msg["message"]);
                    break;
            }
        }

        // Run the handler for a granted job, then report the outcome. Fire-and-forget — tracked via
        // _inFlight so the progress loop keeps its lease alive while it runs.
        private void StartProcessing(JObject job, CancellationToken ct)
        {
            var id = (string)job["id"];
            if (string.IsNullOrEmpty(id)) return;

            var parsed = new SidecarJob
            {
                Id = id,
                Queue = (string)job["queue"] ?? string.Empty,
                Payload = job["payload"],
                Attempts = (int?)job["attempts"] ?? 0,
                EnqueuedAt = (string)job["enqueuedAt"],
                LeaseUntil = (string)job["leaseUntil"]
            };

            _inFlight[id] = 1;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _handler(parsed, ct);
                    await SendJsonAsync(new { type = "complete", jobId = id, result }, ct);
                }
                catch (OperationCanceledException) { /* shutting down — leave the lease to expire/reclaim */ }
                catch (Exception ex)
                {
                    Log("Sidecar: job " + id + " failed: " + ex.Message);
                    try { await SendJsonAsync(new { type = "fail", jobId = id, error = ex.Message }, ct); } catch { /* socket dying */ }
                }
                finally
                {
                    _inFlight.TryRemove(id, out _);
                }
            }, ct);
        }

        // Fallback poll: periodically drain each queue in case a wake was missed (e.g. a job that
        // landed during a reconnect gap).
        private async Task PollLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _pollSeconds)), ct);
                    await DrainAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* session ended */ }
        }

        // Extend the lease of every in-flight job so a long-running job isn't reclaimed mid-flight.
        private async Task ProgressLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _progressSeconds)), ct);
                    var ids = new List<string>(_inFlight.Keys);
                    if (ids.Count > 0) await SendJsonAsync(new { type = "progress", jobIds = ids }, ct);
                }
            }
            catch (OperationCanceledException) { /* session ended */ }
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            foreach (var q in _queues) await SendJsonAsync(new { type = "claim", queue = q }, ct);
        }

        // --- transport helpers -------------------------------------------------

        // Always sends over the CURRENT socket (grabbed under the gate) so a completion that lands
        // after a reconnect targets the live socket, not a captured dead one. No-op when down.
        private async Task SendJsonAsync(object frame, CancellationToken ct)
        {
            ILelwareRealtimeSocket socket;
            lock (_gate) { socket = _socket; }
            if (socket == null || !socket.IsOpen) return;

            var text = JsonConvert.SerializeObject(frame);
            await _sendLock.WaitAsync(ct);
            try
            {
                if (socket.IsOpen) await socket.SendAsync(text, ct);
            }
            catch { /* socket dying — the receive loop will reconnect */ }
            finally { _sendLock.Release(); }
        }

        private Uri BuildConnectUri()
        {
            // BaseUrl is http(s); the WebSocket scheme is ws(s).
            var baseUrl = (_client.BaseUrl ?? string.Empty).Replace("https://", "wss://").Replace("http://", "ws://");
            var url = baseUrl + "/api/" + Uri.EscapeDataString(_client.ProjectId ?? string.Empty)
                      + "/Sidecar/Connect?node=" + Uri.EscapeDataString(_node)
                      + "&queues=" + Uri.EscapeDataString(string.Join(",", _queues));
            return new Uri(url);
        }

        private static string[] Normalize(IEnumerable<string> queues)
        {
            if (queues == null) return Array.Empty<string>();
            var set = new List<string>();
            foreach (var q in queues)
            {
                var t = q?.Trim();
                if (!string.IsNullOrEmpty(t) && !set.Contains(t)) set.Add(t);
            }
            return set.ToArray();
        }

        private void Log(string message)
        {
            if (_client.Logger != null) _client.Logger(message);
        }

        // Per-connection state (so the poll/progress timers start exactly once per socket).
        private sealed class Session
        {
            public bool TimersStarted;
        }

        // --- job DTO -----------------------------------------------------------

        /// <summary>
        ///     One claimed job handed to the worker's handler. <see cref="Payload" /> is the enqueued
        ///     payload as embedded JSON (an object/array/scalar, or null) — read it with
        ///     <see cref="PayloadAs{T}" />. The rest is bookkeeping the portal attached.
        /// </summary>
        public sealed class SidecarJob
        {
            public string Id;
            public string Queue;

            /// <summary>The enqueued payload as parsed JSON (null if none). Use <see cref="PayloadAs{T}" /> to type it.</summary>
            public JToken Payload;

            /// <summary>How many times this job has been attempted (this claim included).</summary>
            public int Attempts;

            /// <summary>When it was enqueued (ISO-8601 string), as reported by the portal.</summary>
            public string EnqueuedAt;

            /// <summary>Current lease expiry (ISO-8601 string) — extended automatically while the handler runs.</summary>
            public string LeaseUntil;

            /// <summary>Deserialize the payload into <typeparamref name="T" /> (Newtonsoft). Returns default when null.</summary>
            public T PayloadAs<T>() => Payload == null ? default : Payload.ToObject<T>();

            /// <summary>The payload as raw JSON text (null when there's no payload).</summary>
            public string PayloadJson => Payload?.ToString(Formatting.None);
        }
    }
}
