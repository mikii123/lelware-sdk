using System;
using UnityEngine;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Connection settings for a <see cref="LelwareClient" />. The two things that never
    ///     change for a given build are the portal base URL and the project ID (the project ID
    ///     IS the public route segment on the portal — e.g. a GUID, or the literal "Clearwater"),
    ///     so they live here rather than being threaded through every call.
    ///
    ///     <para>This type is designed to be authored two ways:</para>
    ///     <list type="bullet">
    ///       <item><description><b>From the Unity Inspector</b> — drop it as a serialized field on
    ///         a MonoBehaviour/ScriptableObject and fill it in. Unity serializes FIELDS, not
    ///         properties, so the values below are <see cref="SerializeField" /> backing
    ///         fields exposed through accessor properties whose getters normalise the
    ///         raw input — trimming the URL's trailing slash, nulling a blank device id — so the
    ///         normalisation applies equally to inspector-entered and code-entered values).</description></item>
    ///       <item><description><b>From code</b> — use the convenience constructor.</description></item>
    ///     </list>
    ///
    ///     Construction never throws (matching the SDK's exception-free contract); call
    ///     <see cref="Validate" /> to check required fields and get a human-readable error, or
    ///     <see cref="IsValid" /> for a quick bool.
    /// </summary>
    [Serializable]
    public sealed class LelwareClientConfig
    {
        [SerializeField]
        [Tooltip("Portal root, e.g. https://portal.lelware.com. Trailing slash is optional (it's trimmed).")]
        private string baseUrl = "https://portal.lelware.com";

        [SerializeField]
        [Tooltip("Public project id = the route segment, e.g. a GUID or the literal \"Clearwater\".")]
        private string projectId = string.Empty;

        [SerializeField]
        [Tooltip("Optional stable device id sent as X-Device-Id. Leave blank to opt out of device tracking.")]
        private string deviceId = string.Empty;

        [SerializeField]
        [Min(0)]
        [Tooltip("Per-request network timeout in seconds. 0 = Unity's default.")]
        private int timeoutSeconds = 30;

        [SerializeField]
        [Tooltip("Log every outgoing request (verb + URL + status) via the client's logger (Debug.Log by default).")]
        private bool enableRequestLogging;

        [SerializeField]
        [Tooltip("When request logging is on, also include request/response bodies. Off by default — bodies can be large and may contain credentials (e.g. the login password).")]
        private bool logRequestBodies;

        /// <summary>
        ///     Portal root, e.g. <c>https://portal.lelware.com</c>. The trailing slash is trimmed
        ///     here so URL composition can always assume there isn't one. All endpoints hang off
        ///     <c>{BaseUrl}/api/{ProjectId}/...</c>.
        /// </summary>
        public string BaseUrl
        {
            get => baseUrl?.TrimEnd('/');
            set => baseUrl = value;
        }

        /// <summary>Public project id (route segment). Sent as <c>{pid}</c> in every URL.</summary>
        public string ProjectId
        {
            get => projectId;
            set => projectId = value;
        }

        /// <summary>
        ///     Optional stable device identifier sent as <c>X-Device-Id</c>. The portal only
        ///     honours this header for API clients (it never sets a cookie for us), so if you
        ///     want device tracking you MUST supply and persist one yourself — a per-launch
        ///     random GUID would make every session look like a brand-new device. A blank value
        ///     is treated as "not set" (the header is omitted).
        /// </summary>
        public string DeviceId
        {
            get => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
            set => deviceId = value;
        }

        /// <summary>Per-request network timeout in seconds. 0 = Unity's default.</summary>
        public int TimeoutSeconds
        {
            get => timeoutSeconds;
            set => timeoutSeconds = value;
        }

        /// <summary>
        ///     When true, the client logs a line for every outgoing request (verb + URL) and its
        ///     outcome (status code, or the transport error) through <see cref="LelwareClient.Logger" />
        ///     (which defaults to <see cref="Debug.Log" />). Off by default so a shipped build is quiet.
        /// </summary>
        public bool EnableRequestLogging
        {
            get => enableRequestLogging;
            set => enableRequestLogging = value;
        }

        /// <summary>
        ///     When request logging is on, also include the request and response bodies in the log.
        ///     Off by default on purpose: bodies can be large and may carry sensitive data — most
        ///     notably the <see cref="LelwareClient.LoginAsync" /> request body contains the plaintext
        ///     password. Only enable while debugging, and never in a shipped build.
        /// </summary>
        public bool LogRequestBodies
        {
            get => logRequestBodies;
            set => logRequestBodies = value;
        }

        /// <summary>
        ///     Parameterless constructor for Unity serialization / the Inspector. The
        ///     <see cref="SerializeField" /> defaults above apply; fill the rest in the Inspector.
        /// </summary>
        public LelwareClientConfig()
        {
        }

        /// <summary>
        ///     Convenience constructor for code-driven setup. Does NOT throw on missing values —
        ///     normalisation happens on read and validity is checked via <see cref="Validate" />.
        /// </summary>
        public LelwareClientConfig(string baseUrl, string projectId, string deviceId = null, int timeoutSeconds = 30)
        {
            this.baseUrl = baseUrl;
            this.projectId = projectId;
            this.deviceId = deviceId;
            this.timeoutSeconds = timeoutSeconds;
        }

        /// <summary>True when the required fields (<see cref="BaseUrl" />, <see cref="ProjectId" />) are present.</summary>
        public bool IsValid => Validate() == null;

        /// <summary>
        ///     Validates the required fields. Returns <c>null</c> when the config is usable, or a
        ///     human-readable error message describing the first problem — mirroring the SDK's
        ///     "string error, null = ok" convention so callers can branch without try/catch.
        /// </summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "BaseUrl is required.";
            }

            if (string.IsNullOrWhiteSpace(projectId))
            {
                return "ProjectId is required.";
            }

            return null;
        }
    }
}
