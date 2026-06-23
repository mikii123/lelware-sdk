using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Pluggable transport for <see cref="LelwareRealtime" />. Unity has no first-class
    ///     WebSocket API, so the SDK abstracts the socket behind this interface and ships a
    ///     default built on <see cref="System.Net.WebSockets.ClientWebSocket" /> (the BCL
    ///     type, which works on Mono + IL2CPP standalone / mobile / dedicated-server builds).
    ///
    ///     <para><b>WebGL:</b> <c>ClientWebSocket</c> is NOT supported there (no threads /
    ///     sockets in the browser sandbox), and a browser WebSocket can't send custom auth
    ///     headers. To run realtime on WebGL, implement this interface over a JS-bridge
    ///     WebSocket (e.g. the community <c>NativeWebSocket</c> package) and pass a factory to
    ///     <see cref="LelwareRealtime" />'s constructor. The rest of the client
    ///     (reconnect, subscriptions, main-thread marshalling) is transport-agnostic.</para>
    /// </summary>
    public interface ILelwareRealtimeSocket : IDisposable
    {
        bool IsOpen { get; }

        /// <summary>
        ///     Open the connection. <paramref name="headers" /> are upgrade-request headers
        ///     (Is-Client / Authorization / X-Device-Id) — a transport that can't set them
        ///     (e.g. a browser socket) should authenticate another way (cookie / query token).
        /// </summary>
        Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct);

        /// <summary>Send one text frame.</summary>
        Task SendAsync(string text, CancellationToken ct);

        /// <summary>
        ///     Receive the next COMPLETE text message, or null when the peer closed the socket.
        ///     Implementations reassemble fragmented frames before returning.
        /// </summary>
        Task<string> ReceiveAsync(CancellationToken ct);

        /// <summary>Hard-abort (used on teardown / reconnect).</summary>
        void Abort();
    }

    /// <summary>
    ///     Default <see cref="ILelwareRealtimeSocket" /> built on <see cref="ClientWebSocket" />.
    ///     Suitable for every Unity target EXCEPT WebGL — see the interface docs.
    /// </summary>
    public sealed class ClientWebSocketTransport : ILelwareRealtimeSocket
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly byte[] _buffer = new byte[4096];

        public bool IsOpen => _socket.State == WebSocketState.Open;

        public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (!string.IsNullOrEmpty(kv.Value)) _socket.Options.SetRequestHeader(kv.Key, kv.Value);
                }
            }

            await _socket.ConnectAsync(uri, ct);
        }

        public Task SendAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        public async Task<string> ReceiveAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(_buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct); } catch { /* ignore */ }
                    return null; // signals "closed" to the receive loop
                }

                sb.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            return sb.ToString();
        }

        public void Abort()
        {
            try { _socket.Abort(); } catch { /* already gone */ }
        }

        public void Dispose()
        {
            try { _socket.Dispose(); } catch { /* already disposed */ }
        }
    }

    /// <summary>
    ///     Realtime (WebSocket) channel client for the portal. Companion to
    ///     <see cref="LelwareClient" /> — share one logged-in client between them.
    ///
    ///     <para>Connects to <c>/api/{pid}/Realtime/Connect</c> using the SAME auth the rest
    ///     of the SDK uses: the <c>Is-Client</c> header makes the portal treat us as an API
    ///     client (token auth, no cookie) and the cached bearer token rides on the upgrade
    ///     request. So realtime needs no cookie — log in with
    ///     <see cref="LelwareClient.LoginAsync" /> first, then <see cref="Start" /> here.</para>
    ///
    ///     <para><b>Protocol</b> (JSON text frames): the server sends a <c>welcome</c> frame
    ///     with a connection id, then <c>message</c> frames; it pings, we pong (keep-alive).
    ///     Subscription is by HTTP endpoint — once we have the connection id we POST it plus
    ///     the channel name to <c>Realtime/Subscribe</c>, and the server joins our socket to
    ///     the channel group.</para>
    ///
    ///     <para><b>Transport:</b> defaults to <see cref="ClientWebSocketTransport" /> (works
    ///     on standalone / mobile / server, NOT WebGL). For WebGL or a custom stack, pass a
    ///     <see cref="ILelwareRealtimeSocket" /> factory to the constructor.</para>
    ///
    ///     <para><b>Threading:</b> the socket runs on a background task, but your handlers are
    ///     marshalled back onto the thread that called <see cref="Start" /> (the Unity main
    ///     thread) via its <see cref="SynchronizationContext" />, so you can touch Unity APIs
    ///     in them. Reconnection is automatic with capped backoff; subscriptions are re-applied
    ///     on every reconnect. Never throws — failures surface via
    ///     <see cref="LelwareClient.Logger" /> when logging is enabled.</para>
    /// </summary>
    public sealed class LelwareRealtime : IDisposable
    {
        private readonly LelwareClient _client;
        private readonly Func<ILelwareRealtimeSocket> _socketFactory;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Action<JToken>> _handlers = new Dictionary<string, Action<JToken>>();

        private SynchronizationContext _sync;
        private CancellationTokenSource _cts;
        private string _connectionId;
        private bool _running;
        private int _backoffMs = 1000;

        /// <param name="client">A (logged-in) portal client — supplies base URL, project id and bearer token.</param>
        /// <param name="socketFactory">
        ///     Optional transport factory. Defaults to <see cref="ClientWebSocketTransport" />; pass
        ///     your own (e.g. a WebGL JS-bridge socket) to override. Called once per (re)connect.
        /// </param>
        public LelwareRealtime(LelwareClient client, Func<ILelwareRealtimeSocket> socketFactory = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _socketFactory = socketFactory ?? (() => new ClientWebSocketTransport());
        }

        /// <summary>True once the socket is connected and has reported its connection id.</summary>
        public bool IsConnected
        {
            get { lock (_gate) { return _connectionId != null; } }
        }

        // --- lifecycle ---------------------------------------------------------

        /// <summary>
        ///     Open the connection and start the background receive loop. Capture the calling
        ///     thread's <see cref="SynchronizationContext" /> so handlers marshal back to it
        ///     (call from the Unity main thread). Idempotent.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _running = true;
                _sync = SynchronizationContext.Current;
                _cts = new CancellationTokenSource();
            }

            _ = RunAsync(_cts.Token);
        }

        /// <summary>Stop the connection for good (no reconnect) and release resources.</summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                _connectionId = null;
                cts = _cts;
                _cts = null;
            }

            try { cts?.Cancel(); } catch { /* ignore */ }
            try { cts?.Dispose(); } catch { /* ignore */ }
        }

        public void Dispose() => Stop();

        // --- subscription ------------------------------------------------------

        /// <summary>
        ///     Subscribe to a channel; <paramref name="handler" /> receives the message payload
        ///     as a raw JSON string on each message. Applied immediately if connected, otherwise
        ///     as soon as the connection is (re)established.
        /// </summary>
        public void Subscribe(string channel, Action<string> handler)
        {
            if (string.IsNullOrEmpty(channel) || handler == null) return;
            SubscribeToken(channel, token => handler(token?.ToString(Formatting.None) ?? "null"));
        }

        /// <summary>
        ///     Typed overload — the message payload is deserialized to <typeparamref name="T" />
        ///     (Newtonsoft) before your handler runs. A payload that doesn't fit is reported via
        ///     the logger and the handler is skipped.
        /// </summary>
        public void Subscribe<T>(string channel, Action<T> handler)
        {
            if (string.IsNullOrEmpty(channel) || handler == null) return;
            SubscribeToken(channel, token =>
            {
                T value;
                try { value = token == null ? default : token.ToObject<T>(); }
                catch (Exception ex) { Log("Realtime: failed to deserialize payload on '" + channel + "': " + ex.Message); return; }
                handler(value);
            });
        }

        private void SubscribeToken(string channel, Action<JToken> handler)
        {
            bool connected;
            lock (_gate)
            {
                _handlers[channel] = handler;
                connected = _connectionId != null;
            }

            if (connected) SendSubscribe(channel);
        }

        /// <summary>Stop receiving a channel — removes the server-side group membership and the local handler.</summary>
        public void Unsubscribe(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return;

            string connId;
            lock (_gate)
            {
                _handlers.Remove(channel);
                connId = _connectionId;
            }

            if (connId != null)
            {
                var body = JsonConvert.SerializeObject(new { connectionId = connId, channel });
                _ = _client.SendAsync(UnityWebRequest.kHttpVerbPOST, "Realtime/Unsubscribe", null, body);
            }
        }

        private void SendSubscribe(string channel)
        {
            string connId;
            lock (_gate) { connId = _connectionId; }
            if (connId == null) return;

            var body = JsonConvert.SerializeObject(new { connectionId = connId, channel });
            _ = SubscribeRequest(channel, body);
        }

        private async Task SubscribeRequest(string channel, string body)
        {
            // Fire-and-forget POST through the standard client (carries Is-Client + bearer, so
            // the server resolves the same player). Errors surface via the client's logger.
            var res = await _client.SendAsync(UnityWebRequest.kHttpVerbPOST, "Realtime/Subscribe", null, body);
            if (res.Error) Log("Realtime: subscribe to '" + channel + "' failed: " + res.Message);
        }

        // --- background socket loop -------------------------------------------

        private async Task RunAsync(CancellationToken ct)
        {
            var headers = BuildHeaders();
            var uri = new Uri(BuildWsUrl());

            while (!ct.IsCancellationRequested)
            {
                ILelwareRealtimeSocket socket = null;
                try
                {
                    socket = _socketFactory();
                    await socket.ConnectAsync(uri, headers, ct);
                    await ReceiveLoop(socket, ct);
                }
                catch (OperationCanceledException)
                {
                    break; // Stop() was called
                }
                catch (Exception ex)
                {
                    Log("Realtime: connection error: " + ex.Message);
                }
                finally
                {
                    socket?.Dispose();
                }

                lock (_gate) { _connectionId = null; }
                if (ct.IsCancellationRequested) break;

                int delay;
                lock (_gate) { delay = _backoffMs; _backoffMs = Math.Min(_backoffMs * 2, 30000); }
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task ReceiveLoop(ILelwareRealtimeSocket socket, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var json = await socket.ReceiveAsync(ct);
                if (json == null) return; // socket closed
                HandleFrame(socket, json, ct);
            }
        }

        private void HandleFrame(ILelwareRealtimeSocket socket, string json, CancellationToken ct)
        {
            JObject msg;
            try { msg = JObject.Parse(json); } catch { return; }

            var type = (string)msg["type"];
            if (type == "welcome")
            {
                List<string> channels;
                lock (_gate)
                {
                    _connectionId = (string)msg["connectionId"];
                    _backoffMs = 1000; // healthy connection — reset backoff
                    channels = new List<string>(_handlers.Keys);
                }

                // (Re)apply every subscription on the main thread (the POST uses UnityWebRequest).
                Post(() => { foreach (var ch in channels) SendSubscribe(ch); });
            }
            else if (type == "message")
            {
                var channel = (string)msg["channel"];
                var data = msg["data"];

                Action<JToken> handler;
                lock (_gate) { _handlers.TryGetValue(channel ?? "", out handler); }
                if (handler != null) Post(() => handler(data));
            }
            else if (type == "ping")
            {
                // Heartbeat — reply so the server's sweep keeps us alive. Sent from the socket
                // thread (no Unity API involved).
                _ = SendPong(socket, ct);
            }
        }

        private async Task SendPong(ILelwareRealtimeSocket socket, CancellationToken ct)
        {
            try { await socket.SendAsync("{\"type\":\"pong\"}", ct); }
            catch { /* socket dying — the receive loop will reconnect */ }
        }

        // --- helpers -----------------------------------------------------------

        private IReadOnlyDictionary<string, string> BuildHeaders()
        {
            var headers = new Dictionary<string, string>
            {
                { LelwareClient.ClientHeaderName, LelwareClient.ClientHeaderValue }
            };
            if (!string.IsNullOrEmpty(_client.AccessToken))
                headers["Authorization"] = "Bearer " + _client.AccessToken;
            if (!string.IsNullOrEmpty(_client.Config.DeviceId))
                headers[LelwareClient.DeviceHeaderName] = _client.Config.DeviceId;
            return headers;
        }

        private string BuildWsUrl()
        {
            // BaseUrl is http(s); the WebSocket scheme is ws(s).
            var baseUrl = (_client.Config.BaseUrl ?? "").Replace("https://", "wss://").Replace("http://", "ws://");
            return baseUrl + "/api/" + Uri.EscapeDataString(_client.ProjectId ?? "") + "/Realtime/Connect";
        }

        // Marshal an action onto the captured (main) thread; run inline if none was captured.
        private void Post(Action action)
        {
            var sync = _sync;
            if (sync != null) sync.Post(_ => action(), null);
            else action();
        }

        private void Log(string message)
        {
            if (_client.Logger != null) _client.Logger(message);
        }
    }
}
