using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lelware.Sdk.Editor
{
    /// <summary>
    ///     Editor window that pulls the portal's global OpenAPI document and generates the typed
    ///     client from it — the "fetch the schema from the portal" workflow. Settings (portal URL,
    ///     secret, output path) persist in <see cref="EditorPrefs" /> so a regenerate after a
    ///     schema change is one click.
    ///
    ///     The fetch runs on a thread-pool thread (<see cref="Task.Run{T}" /> + block) rather than
    ///     awaiting on the Editor's main thread: an EditorWindow button handler isn't async, and
    ///     blocking directly on an HttpClient call that captured the main-thread context can
    ///     deadlock. Off-thread + block sidesteps that cleanly for a one-shot dev-time request.
    /// </summary>
    public sealed class PortalSdkGeneratorWindow : EditorWindow
    {
        private const string KeyBaseUrl = "lelware.sdk.baseUrl";
        private const string KeySecret = "lelware.sdk.secret";
        private const string KeyProjectId = "lelware.sdk.projectId";
        private const string KeyOutput = "lelware.sdk.outputPath";

        private const string DefaultOutput = "Assets/Lelware/Generated/LelwarePortalApi.Generated.cs";

        private string _baseUrl;
        private string _secret;
        private string _projectId;
        private string _output;
        private string _status;
        private bool _busy;

        [MenuItem("Tools/Lelware/Generate SDK from Portal")]
        public static void Open()
        {
            var window = GetWindow<PortalSdkGeneratorWindow>(true, "Lelware SDK — Generate from Portal");
            window.minSize = new Vector2(460, 220);
        }

        private void OnEnable()
        {
            _baseUrl = EditorPrefs.GetString(KeyBaseUrl, "https://portal.lelware.com");
            _secret = EditorPrefs.GetString(KeySecret, "");
            _projectId = EditorPrefs.GetString(KeyProjectId, "");
            _output = EditorPrefs.GetString(KeyOutput, DefaultOutput);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Portal", EditorStyles.boldLabel);
            _baseUrl = EditorGUILayout.TextField("Base URL", _baseUrl);
            _secret = EditorGUILayout.PasswordField("Explorer secret", _secret);
            _projectId = EditorGUILayout.TextField("Project ID", _projectId);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            _output = EditorGUILayout.TextField("File (Assets/...)", _output);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_busy))
            {
                if (GUILayout.Button(_busy ? "Generating…" : "Fetch schema & generate"))
                {
                    Persist();
                    Generate();
                }
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_status, MessageType.None);
            }
        }

        private void Persist()
        {
            EditorPrefs.SetString(KeyBaseUrl, _baseUrl?.Trim() ?? "");
            EditorPrefs.SetString(KeySecret, _secret ?? "");
            EditorPrefs.SetString(KeyProjectId, _projectId?.Trim() ?? "");
            EditorPrefs.SetString(KeyOutput, _output?.Trim() ?? DefaultOutput);
        }

        private void Generate()
        {
            _busy = true;
            _status = "Fetching OpenAPI document…";
            Repaint();

            try
            {
                // Scope the schema to one project: the portal folds in THIS project's own
                // additional endpoints (custom scripts + any module-dedicated routes). Without a
                // projectId the portal returns only the project-agnostic surface.
                var url = $"{(_baseUrl ?? "").TrimEnd('/')}/api/sdk/OpenApi";
                if (!string.IsNullOrEmpty(_projectId))
                {
                    url += "?projectId=" + Uri.EscapeDataString(_projectId);
                }

                var json = FetchSchema(url, _secret);

                var source = OpenApiCodeGenerator.Generate(json);

                var full = Path.GetFullPath(_output);
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                File.WriteAllText(full, source);

                AssetDatabase.Refresh();
                _status = $"OK — wrote {_output}.";
                Debug.Log($"[Lelware SDK] Generated {_output} from {url}.");
            }
            catch (Exception ex)
            {
                // Surface the failure in the window AND the console; never leave a half-written file
                // path implying success.
                _status = "Failed: " + ex.Message;
                Debug.LogError($"[Lelware SDK] Generation failed: {ex}");
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        // One-shot synchronous GET with the shared secret header. Runs off the main thread to
        // avoid a context-capture deadlock when blocking on the result.
        private static string FetchSchema(string url, string secret)
        {
            return Task.Run(async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(secret))
                {
                    req.Headers.Add("X-Api-Key", secret);
                }

                using var resp = await http.SendAsync(req).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new Exception($"HTTP {(int)resp.StatusCode} from {url}. " +
                                        (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                                            ? "Check the explorer secret."
                                            : body));
                }

                return body;
            }).GetAwaiter().GetResult();
        }
    }
}
