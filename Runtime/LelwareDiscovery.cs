using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace Lelware.Sdk
{
    /// <summary>
    ///     One portal instance heard on the LAN via the discovery beacon. This is what you pick
    ///     from before logging in — <see cref="BaseUrl" /> + <see cref="ProjectId" /> are exactly the
    ///     two values <see cref="LelwareClientConfig" /> needs, so <see cref="ToConfig" /> hands you a
    ///     ready config to build a <see cref="LelwareClient" />.
    /// </summary>
    public sealed class LelwareDiscoveredProject
    {
        /// <summary>Public project id (the route segment) — advertised as <c>pid</c>.</summary>
        public string ProjectId;

        /// <summary>Human-readable project name (for a picker UI). May be null/empty.</summary>
        public string Name;

        /// <summary>
        ///     Deterministic id of the template this instance derived from — advertised as
        ///     <c>templateId</c>. Unlike <see cref="ProjectId" /> (a per-instance random GUID), this is
        ///     stable, so filter on it to recognise the KIND of project regardless of which instance was
        ///     found. May be null for a legacy/standalone project.
        /// </summary>
        public string TemplateId;

        /// <summary>Portal base URL reachable on the LAN, e.g. <c>http://192.168.1.50:8080</c>.</summary>
        public string BaseUrl;

        /// <summary>UTC time this project was last heard in a beacon (for staleness / "still alive" UI).</summary>
        public DateTime LastSeenUtc;

        /// <summary>
        ///     Build a <see cref="LelwareClientConfig" /> pointed at this discovered project — the
        ///     bridge from "found it on the LAN" to "log in". Optionally carry a device id through.
        /// </summary>
        public LelwareClientConfig ToConfig(string deviceId = null, int timeoutSeconds = 30)
        {
            return new LelwareClientConfig(BaseUrl, ProjectId, deviceId, timeoutSeconds);
        }
    }

    /// <summary>
    ///     Standalone LAN auto-discovery receiver — deliberately SEPARATE from
    ///     <see cref="LelwareClient" /> / <see cref="LelwareClientConfig" /> so you can run it on its
    ///     own, BEFORE anyone logs in, to find which portal + projects are on the network. It listens
    ///     for the portal's UDP multicast beacon (see the portal's on-prem discovery feature) and
    ///     surfaces each advertised project.
    ///
    ///     <para>Two ways to use it:</para>
    ///     <list type="bullet">
    ///       <item><description><b>One-shot</b> — <see cref="DiscoverAsync" /> listens for a fixed
    ///         window and returns the unique set of projects heard. Ideal for a "scanning…" step on a
    ///         connect screen.</description></item>
    ///       <item><description><b>Continuous</b> — <see cref="Start" /> / <see cref="Stop" /> keep a
    ///         listener open and raise <see cref="ProjectDiscovered" /> the first time each project is
    ///         seen. Ideal for a live lobby list.</description></item>
    ///     </list>
    ///
    ///     <para><b>Threading:</b> the beacon arrives on a background thread, so
    ///     <see cref="ProjectDiscovered" /> is raised OFF the Unity main thread and the continuation of
    ///     an <c>await DiscoverAsync(...)</c> may resume off it too. Marshal back to the main thread
    ///     yourself before touching Unity objects (e.g. a main-thread dispatcher, or
    ///     <c>UniTask.SwitchToMainThread()</c>).</para>
    ///
    ///     <para><b>Platforms:</b> uses BCL UDP sockets (<see cref="UdpClient" />) — works on
    ///     standalone / mobile / dedicated-server builds. <b>WebGL is NOT supported</b> (no raw
    ///     sockets in the browser sandbox), same limitation as realtime.</para>
    ///
    ///     <para>Exception-free at the surface, like the rest of the SDK: socket / parse failures are
    ///     logged via <see cref="Logger" /> and swallowed rather than thrown.</para>
    /// </summary>
    public sealed class LelwareDiscovery : IDisposable
    {
        /// <summary>Default multicast group the portal beacons on (matches the portal's default).</summary>
        public const string DefaultMulticastGroup = "239.255.42.99";

        /// <summary>Default UDP port the portal beacons on (matches the portal's default).</summary>
        public const int DefaultPort = 45678;

        private readonly string _group;
        private readonly int _port;

        // Continuous-mode state. _known dedupes across the session so ProjectDiscovered fires once
        // per project; guarded by its own lock because it's touched from the receive-loop thread.
        private readonly Dictionary<string, LelwareDiscoveredProject> _known =
            new Dictionary<string, LelwareDiscoveredProject>(StringComparer.Ordinal);

        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private Task _loop;

        /// <summary>
        ///     Raised (in continuous mode) the first time each distinct project is heard. See the
        ///     class remarks: this fires on a BACKGROUND thread — marshal to the main thread for UI.
        /// </summary>
        public event Action<LelwareDiscoveredProject> ProjectDiscovered;

        /// <summary>
        ///     Sink for diagnostics (socket open failures, ignored malformed beacons). Defaults to
        ///     <see cref="Debug.Log" />; set to null to silence.
        /// </summary>
        public Action<string> Logger = msg => Debug.Log(msg);

        /// <summary>
        ///     Create a receiver. Defaults match the portal's out-of-the-box beacon; override
        ///     <paramref name="multicastGroup" /> / <paramref name="port" /> only if the portal was
        ///     reconfigured (<c>Discovery:MulticastGroup</c> / <c>Discovery:Port</c>).
        /// </summary>
        public LelwareDiscovery(string multicastGroup = DefaultMulticastGroup, int port = DefaultPort)
        {
            _group = string.IsNullOrWhiteSpace(multicastGroup) ? DefaultMulticastGroup : multicastGroup;
            _port = port > 0 ? port : DefaultPort;
        }

        /// <summary>True while a continuous listener (<see cref="Start" />) is running.</summary>
        public bool IsRunning => _loop != null && !_loop.IsCompleted;

        /// <summary>Snapshot of every project heard so far in continuous mode (thread-safe copy).</summary>
        public IReadOnlyList<LelwareDiscoveredProject> Known
        {
            get
            {
                lock (_known)
                {
                    return new List<LelwareDiscoveredProject>(_known.Values);
                }
            }
        }

        /// <summary>
        ///     Listen for beacons for <paramref name="timeout" /> and return the unique set of
        ///     projects heard (deduped by project id). One-shot: opens its own socket, closes it on
        ///     return. Returns an empty list on timeout with nothing heard or if the socket can't open.
        ///     Safe to call again. Not thread-tied to the continuous mode — you can use either.
        /// </summary>
        public async Task<List<LelwareDiscoveredProject>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            var found = new Dictionary<string, LelwareDiscoveredProject>(StringComparer.Ordinal);

            UdpClient udp;
            try
            {
                udp = CreateSocket();
            }
            catch (Exception ex)
            {
                Logger?.Invoke($"[LelwareDiscovery] failed to open socket on {_group}:{_port}: {ex.Message}");
                return new List<LelwareDiscoveredProject>();
            }

            // A blocked UdpClient.ReceiveAsync() has no CancellationToken overload on the Unity BCL,
            // so the only way to unblock it is to close the socket — we register that on the linked
            // token (timeout OR the caller's ct), and treat the resulting exception as "done".
            using (udp)
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                linked.CancelAfter(timeout);
                using (linked.Token.Register(() => { try { udp.Close(); } catch { /* already gone */ } }))
                {
                    while (!linked.IsCancellationRequested)
                    {
                        UdpReceiveResult result;
                        try
                        {
                            result = await udp.ReceiveAsync();
                        }
                        catch (ObjectDisposedException) { break; } // socket closed by the cancel callback
                        catch (SocketException) { break; }

                        foreach (var proj in ParseFrame(result.Buffer))
                        {
                            if (found.TryGetValue(proj.ProjectId, out var existing))
                            {
                                existing.LastSeenUtc = proj.LastSeenUtc;
                            }
                            else
                            {
                                found[proj.ProjectId] = proj;
                            }
                        }
                    }
                }
            }

            return new List<LelwareDiscoveredProject>(found.Values);
        }

        /// <summary>
        ///     Start a continuous listener. Raises <see cref="ProjectDiscovered" /> once per newly
        ///     seen project and accumulates them in <see cref="Known" />. No-op if already running or
        ///     if the socket can't open (logged). Call <see cref="Stop" /> (or <see cref="Dispose" />)
        ///     to end it.
        /// </summary>
        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            try
            {
                _udp = CreateSocket();
            }
            catch (Exception ex)
            {
                Logger?.Invoke($"[LelwareDiscovery] failed to open socket on {_group}:{_port}: {ex.Message}");
                _udp = null;
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = ReceiveLoopAsync(_udp, _cts.Token);
        }

        /// <summary>Stop the continuous listener and release the socket. Safe to call when not running.</summary>
        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _udp?.Close(); } catch { /* ignore */ } // unblocks a pending ReceiveAsync
            _cts?.Dispose();
            _cts = null;
            _udp = null;
            _loop = null;
        }

        /// <summary><see cref="Stop" />s the listener.</summary>
        public void Dispose()
        {
            Stop();
        }

        private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync();
                }
                catch (ObjectDisposedException) { break; } // Stop() closed the socket
                catch (SocketException) { break; }

                foreach (var proj in ParseFrame(result.Buffer))
                {
                    bool isNew;
                    lock (_known)
                    {
                        if (_known.TryGetValue(proj.ProjectId, out var existing))
                        {
                            existing.LastSeenUtc = proj.LastSeenUtc;
                            isNew = false;
                        }
                        else
                        {
                            _known[proj.ProjectId] = proj;
                            isNew = true;
                        }
                    }

                    if (isNew)
                    {
                        SafeRaiseDiscovered(proj);
                    }
                }
            }
        }

        // A bad subscriber must not kill the receive loop.
        private void SafeRaiseDiscovered(LelwareDiscoveredProject proj)
        {
            try
            {
                ProjectDiscovered?.Invoke(proj);
            }
            catch (Exception ex)
            {
                Logger?.Invoke($"[LelwareDiscovery] ProjectDiscovered handler threw: {ex.Message}");
            }
        }

        private UdpClient CreateSocket()
        {
            var udp = new UdpClient();
            // ReuseAddress before Bind so several receivers (e.g. two Unity instances during dev, or a
            // DiscoverAsync overlapping a Start) can share the host/port.
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            udp.JoinMulticastGroup(IPAddress.Parse(_group));
            return udp;
        }

        // Parse one datagram into zero-or-more projects. Never throws (malformed frames are logged and
        // skipped) — kept out of the yield/try mix so we can catch around the parse cleanly.
        private List<LelwareDiscoveredProject> ParseFrame(byte[] buffer)
        {
            var list = new List<LelwareDiscoveredProject>();

            BeaconFrame frame;
            try
            {
                var json = Encoding.UTF8.GetString(buffer);
                frame = JsonConvert.DeserializeObject<BeaconFrame>(json);
            }
            catch (Exception ex)
            {
                Logger?.Invoke($"[LelwareDiscovery] ignored malformed beacon: {ex.Message}");
                return list;
            }

            if (frame?.Projects == null)
            {
                return list;
            }

            var now = DateTime.UtcNow;
            foreach (var p in frame.Projects)
            {
                if (p == null || string.IsNullOrEmpty(p.Pid) || string.IsNullOrEmpty(p.BaseUrl))
                {
                    continue; // a project needs at least an id + a URL to be actionable
                }

                list.Add(new LelwareDiscoveredProject
                {
                    ProjectId = p.Pid,
                    Name = p.Name,
                    TemplateId = p.TemplateId,
                    BaseUrl = p.BaseUrl,
                    LastSeenUtc = now
                });
            }

            return list;
        }

        // Wire shape of the portal's beacon: { "portal": "...", "projects": [ { pid, name, baseUrl } ] }.
        [Serializable]
        private sealed class BeaconFrame
        {
            [JsonProperty("portal")] public string Portal;
            [JsonProperty("projects")] public BeaconProjectDto[] Projects;
        }

        [Serializable]
        private sealed class BeaconProjectDto
        {
            [JsonProperty("pid")] public string Pid;
            [JsonProperty("name")] public string Name;
            [JsonProperty("templateId")] public string TemplateId;
            [JsonProperty("baseUrl")] public string BaseUrl;
        }
    }
}
