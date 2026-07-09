# Lelware SDK (Unity)

Client SDK for the LelwarePortal client API. It logs in with a bearer token, caches it
and attaches it to every request, and always sends the `Is-Client: api-client` header.
Most of the client surface is **generated from the portal's OpenAPI document** at build
time; a small set of endpoints whose shape doesn't fit a generated JSON call —
authentication, object storage, realtime, matchmaking and game sessions — are hand-written.
For custom scripts there's a generic escape hatch (bring your own request/response types).

## Installation (UPM)

The package depends on `com.unity.nuget.newtonsoft-json` (resolved automatically by
the Package Manager). Add this to your Unity project's `Packages/manifest.json`:

```json
"com.lelware.sdk": "https://github.com/mikii123/lelware-sdk.git"
```

or copy the `com.lelware.sdk/` folder into your project's `Packages/`.

## Quick start

```csharp
using Lelware.Sdk;

var config = new LelwareClientConfig(
    baseUrl:   "https://portal.lelware.com",
    projectId: "my-project",          // project ID = route segment (GUID or fixed id)
    deviceId:  SystemInfo.deviceUniqueIdentifier); // optional, stable per device

var client = new LelwareClient(config);
```

`LelwareClientConfig` is `[Serializable]`, so instead of building it in code you can
expose it as a field on a `MonoBehaviour`/`ScriptableObject` and fill it in the Inspector:

```csharp
public class LelwareBootstrap : MonoBehaviour
{
    [SerializeField] private LelwareClientConfig config; // editable in the Inspector
    private LelwareClient _client;

    private void Awake()
    {
        var err = config.Validate();        // null = ok, otherwise a message
        if (err != null) { Debug.LogError(err); return; }
        _client = new LelwareClient(config);
    }
}

// 1. Login — the token is cached in memory and attached to subsequent calls.
var login = await client.LoginAsync("user@example.com", "password");
if (login.Error)
{
    Debug.LogError($"Login failed ({login.Code}): {login.Message}");
    return;
}
Debug.Log($"PlayerID: {login.Data.PlayerId}");

// 1b. Register — same dual-payload shape as login. The OnRegister script's return value
//     arrives (verbatim) in Data.Payload.CustomData; if register auto-logs-in, the token
//     is cached too (otherwise only the payload is populated).
var reg = await client.RegisterAsync("new@example.com", "password");
if (reg.Error) { Debug.LogError($"Register failed ({reg.Code}): {reg.Message}"); return; }
string custom = reg.Data.Payload?.CustomData; // OnRegister output, deserialize if needed

// (optional) keep the token across runs:
client.PersistToken();
// ...on startup:  if (client.TryRestoreToken()) { /* skip login */ }

// 2. Fixed portal endpoints (GENERATED from OpenAPI, `using Lelware.Sdk.Generated;`) —
//    all of them return LelwareResult<T>:
var settings = await client.GetSettingsAsync();
if (settings.Ok) foreach (var s in settings.Data) { /* ... */ }

await client.SetPlayerDataAsync("level", 7, secret: false);  // 3rd arg maps to the ?secret flag

var lvl = await client.GetPlayerDataAsync("level");          // LelwareResult<PlayerDataEntryDto>
if (lvl.Error && lvl.Code == 404) { /* key does not exist */ }
else if (lvl.Ok)
{
    // .value is the RAW stored string (the raw JSON text for a value stored as JSON) —
    // deserialize it yourself:
    int level = JsonConvert.DeserializeObject<int>(lvl.Data.value);
}

var all = await client.GetAllPlayerDataAsync();
await client.DeletePlayerDataAsync("level");
```

Every call automatically:
- sets `Is-Client: api-client`,
- attaches `Authorization: Bearer <token>` (after login),
- sends `X-Device-Id` if `deviceId` was provided in the config.

## Error handling (no exceptions)

The SDK **never throws** at the API surface. Every call returns a `LelwareResult`
(bodyless variants, e.g. `SetPlayerData`/`Delete`) or a `LelwareResult<T>` (with the
payload in `Data`). Fields:

- `Error` (`bool`) — `true` for any failure,
- `Code` (`long`) — HTTP status, or `0` for a transport error / cancellation / a
  serialization error before sending,
- `Message` (`string`) — error description (`null` on success),
- `RawBody` (`string`) — raw response body (e.g. a terse portal error),
- `Ok` — the inverse of `Error`; `IsAuthError` — `true` for 401/403,
- `Data` (only on `LelwareResult<T>`) — the deserialized payload, `default` on error
  or an empty 2xx.

`Error == true` covers three cases: a transport error (`Code == 0`), a non-2xx status
(`Code` = status, `RawBody` = error body), and a valid 2xx with a body that could not be
parsed (`Code` = a 2xx status, but `Error == true`, with the reason in `Message`).

## Generating from the portal (OpenAPI) — the main mode

The portal exposes an OpenAPI 3 document for the client API (`GET /api/sdk/OpenApi`),
**scoped to the project id you pass** — it folds in that project's own custom-script routes
(and any module-dedicated routes) on top of the shared surface. The SDK generates a typed call
for (almost) every operation in it. A handful of endpoints are **hand-written** instead,
because their shape doesn't fit a generated JSON round-trip: `Authentication`
(login/register), `Storage` + `SharedStorage` (presigned multipart), `Realtime`,
`Matchmaking` and `GameSession` — the portal keeps these out of the generated document.
Everything else — the static endpoints (`GetSettings`, player-data CRUD) and the project's
custom-script routes — is generated.

1. In the portal, set the API key in `appsettings` (`Api:Key`).
2. In Unity: `Tools > Lelware > Generate SDK from Portal` → fill in the **Base URL**, the
   **Explorer secret** (the portal's `Api:Key`) and the **Project ID** (so the schema includes
   that project's script routes) → *Fetch schema & generate*. It writes
   `Assets/Lelware/Generated/LelwarePortalApi.Generated.cs` (namespace `Lelware.Sdk.Generated`,
   calls as extension methods on the static `GeneratedApi` class).

```csharp
using Lelware.Sdk.Generated;

var all = await client.GetAllPlayerDataAsync();          // List<PlayerDataEntryDto>
var s   = await client.GetSettingsAsync();               // List<ProjectSetting>
var r   = await client.ScriptExampleAsync(new { input = "x" }); // script 'example'
```

Notes:
- `projectId` is **not** a method argument — it comes from `LelwareClient.ProjectId`
  (seeded from the config, changeable at runtime), so one generated SDK serves every
  project (a route that a given project doesn't have returns 404 at runtime).
- Every `/api/...` operation in the document is generated except the hand-written ones above
  (`Authentication`, `Storage`/`SharedStorage`, `Realtime`, `Matchmaking`, `GameSession`) and
  the surface the portal deliberately excludes (the custom-page endpoints, and the generic
  `RunScript` template — replaced by one concrete method per script route). This includes
  endpoints outside the `api/{projectId}/...` shape — e.g. `api/maps/tiles/{x}/{y}/{z}.vector`.
  Path params (here `x/y/z`) and query-string params become method arguments, and a JSON
  request body becomes a `body` argument; only `projectId`/`pid` always come from the client.
- Endpoints returning bytes (e.g. vector map tiles, `application/x-protobuf`) are generated
  as `LelwareResult<byte[]>` (data in `.Data`); JSON → `LelwareResult<T>`; empty 2xx →
  `LelwareResult`. For binary content to appear in OpenAPI, the action must declare it
  (`[Produces(...)]` + `[ProducesResponseType(typeof(byte[]), 200)]`).
- Scripts have no parameter schema on the server side, so their request/response are
  **loosely typed** (`object`). To type them — see *Typing custom scripts* below.
- The schema endpoint is protected by an API key (`X-Api-Key`), compared in constant time
  against `Api:Key`. Without a configured key the endpoint is closed (fail-safe).

## Typing custom scripts

Custom scripts (`api/{pid}/RunScript/{route}`) have **no static schema** on the server — a
script just reads raw JSON — so the OpenAPI generator emits them **loosely typed** (`object`
in and out). Two ways to give a script strong types:

### Escape hatch (recommended)

Bring your own request/response classes and use the generic call surface directly — no code
generation needed:

```csharp
class MyReq  { public int a; public string b; }
class MyResp { public bool ok; public string msg; }

var r = await client.CallScriptAsync<MyReq, MyResp>("myRoute", new MyReq { a = 1, b = "x" });
if (r.Ok) Debug.Log(r.Data.msg);
// GET variant (params go on the query string):
var r2 = await client.GetScriptAsync<MyResp>("myRoute",
    new Dictionary<string,string> { ["x"] = "1" });
```

### Manifest generator (advanced — no bundled menu command)

The Editor assembly also ships a manifest-driven generator — `SdkCodeGenerator.Generate` plus
the `SdkSchema` model (namespace `Lelware.Sdk.Editor`) — that turns a JSON description of
selected scripts into typed `{Name}Async` calls (extension methods on a `GeneratedEndpoints`
class, namespace `Lelware.Sdk.Generated`). **There is no menu item for it**: drive it from your
own editor script — deserialize your manifest JSON into `SdkSchema`, call
`SdkCodeGenerator.Generate(schema)`, and write the returned source under `Assets/`. The manifest
shape:

```json
{
  "types": [
    { "name": "Item", "fields": [
      { "name": "id",   "type": "string" },
      { "name": "qty",  "type": "int" }
    ]}
  ],
  "endpoints": [
    { "name": "SaveItem", "route": "saveItem", "method": "POST",
      "request":  { "fields": [ { "name": "id", "type": "string" }, { "name": "qty", "type": "int" } ] },
      "response": { "fields": [ { "name": "ok", "type": "bool" } ] } },
    { "name": "ListItems", "route": "listItems", "method": "GET",
      "request":  { "fields": [ { "name": "page", "type": "int" } ] },
      "response": { "type": "List<Item>" } }
  ]
}
```

It emits, for the example above:

```csharp
using Lelware.Sdk.Generated;

var res = await client.SaveItemAsync(new SaveItemRequest { id = "sword", qty = 1 });
if (res.Ok) Debug.Log($"ok: {res.Data.ok}");
var items = await client.ListItemsAsync(new ListItemsRequest { page = 0 });
```

Manifest fields:

- `types[]` — reusable DTOs (`name`, `fields`).
- `endpoints[]`:
  - `name` — the `{Name}Async` method, the `{Name}Request` / `{Name}Response` classes.
  - `route` — the `RunScript/{route}` segment.
  - `method` — `POST` (JSON body) or `GET` (query string). Defaults to `POST`.
  - `request` / `response` — either `{ "type": "..." }` (use an existing type), or
    `{ "fields": [...] }` (generate a class). An omitted `request` = a parameterless method.
- `fields[]` — `name` (the C# and JSON field name), `type` (any C# type, e.g. `int`,
  `List<Foo>`, `Foo[]`), optionally `json` (a different name on the wire → `[JsonProperty]`).

Generated classes are `partial` — you can extend them in a separate file without losing
changes on regeneration.

## Storage (per-player assets, multipart)

Binary files are kept in object storage (MinIO/S3) in the player's isolated space
(`{projectId}/players/{playerId}/...`). Uploads go **multipart, directly to storage**
via presigned URLs (bypassing the portal, IIS limits, and Cloudflare's 100 MB cap); the
portal only signs the URLs and finalizes the upload. Everything returns a `LelwareResult`
(no exceptions):

```csharp
// upload (chunked automatically, 8 MiB/part by default):
byte[] bytes = System.IO.File.ReadAllBytes(path);
var up = await client.UploadAssetAsync("avatars/hero.png", bytes, contentType: "image/png");
if (up.Error) Debug.LogError($"upload ({up.Code}): {up.Message}");

// download:
var dl = await client.DownloadAssetAsync("avatars/hero.png");
if (dl.Ok) { var raw = dl.Data; /* ... */ }

// list / delete:
var list = await client.ListAssetsAsync();
if (list.Ok) foreach (var o in list.Data.Objects) Debug.Log($"{o.Name} ({o.Size} B)");
await client.DeleteAssetAsync("avatars/hero.png");
```

### Progress (pollable, no callbacks)

Upload and download take an optional `LelwareTransferProgress`. The SDK **pushes nothing**
(no per-frame callbacks, no coroutine, no MonoBehaviour) — it only holds a pointer to the
request currently in flight, and you **read** `Fraction` / `TransferredBytes` / `IsComplete`
whenever you want (typically in your own `Update()`, on the main thread). Progress is hybrid:
completed parts + the byte-level `uploadProgress` of the part in flight (parts exist mainly so
a large file gets through Cloudflare's 100 MB cap).

```csharp
var progress = new LelwareTransferProgress();
var task = client.UploadAssetAsync("avatars/hero.png", bytes, contentType: "image/png", progress: progress);

// somewhere in Update() / your own loop, on the main thread:
slider.value = progress.Fraction;          // 0..1
label.text = $"{progress.TransferredBytes}/{progress.TotalBytes} B";

var up = await task;
if (up.Error) Debug.LogError($"upload ({up.Code}): {up.Message}");

// download — the total is unknown until the headers arrive, until then Fraction = Unity's downloadProgress:
var dlProgress = new LelwareTransferProgress();
var dl = await client.DownloadAssetAsync("avatars/hero.png", progress: dlProgress);
```

Isolation: the portal takes the playerId from the bearer token (never from the request) — a
player operates only on their own space. Requires `Storage:*` to be configured on the portal side.

## Storage (per-project shared assets, read-only)

A second namespace, `{projectId}/shared/...`, is **global to the project** (no per-player
segment) — for assets that are identical for every viewer, e.g. a server-prefetched/derived
cache. Clients can only **read** it; writes happen server-side. Same `LelwareResult` shape, same
off-portal presigned download path as the per-player API:

```csharp
// exists?
var ex = await client.SharedAssetExistsAsync("img-original/.../145897388_p0.jpg");
if (ex.Ok && ex.Data) { /* cached on the portal */ }

// download (presigned GET, optional progress):
var dl = await client.DownloadSharedAssetAsync("img-original/.../145897388_p0.jpg");
if (dl.Ok) { var raw = dl.Data; /* ... */ }

// list one page:
var list = await client.ListSharedAssetsAsync();
if (list.Ok) foreach (var o in list.Data.Objects) Debug.Log($"{o.Name} ({o.Size} B)");
```

There are deliberately no shared `Upload`/`Delete` helpers — the shared namespace is written
only by server-side jobs (e.g. a server-side prefetch/cache job). Requires `Storage:*` on the
portal side; the caller must be a player of the project.

## Realtime (WebSocket channels)

`LelwareRealtime` is a companion to `LelwareClient` — **share one logged-in client** between
them. It connects to `/api/{pid}/Realtime/Connect` reusing the SAME auth as the rest of the
SDK (the `Is-Client` header + cached bearer token ride on the upgrade request), so realtime
needs no cookie: log in first, then `Start()`.

```csharp
var realtime = new LelwareRealtime(client);
realtime.Start();                              // capture the calling (main) thread for handlers

// raw JSON payload:
realtime.Subscribe("news", json => Debug.Log("news: " + json));
// or typed (deserialized with Newtonsoft):
realtime.Subscribe<MyDto>("news", dto => Debug.Log(dto.title));

realtime.Unsubscribe("news");
// ...on teardown:
realtime.Stop();   // (or Dispose()) — no reconnect after this
```

- **Threading:** the socket runs on a background task, but your handlers are marshalled back
  onto the thread that called `Start()` (call it from the Unity main thread), so you can touch
  Unity APIs inside them.
- **Reconnect is automatic** with capped backoff; every subscription is re-applied on each
  reconnect (safe to subscribe before the socket is connected).
- **Never throws** — failures surface via `LelwareClient.Logger` when logging is enabled.
- `IsConnected` / `ConnectionId` expose the socket state; `ConnectionId` is needed by endpoints
  that bind a server-side push to this socket (e.g. matchmaking register).
- **Transport:** defaults to `ClientWebSocketTransport` (works on standalone / mobile /
  dedicated-server, built on `System.Net.WebSockets.ClientWebSocket`). **WebGL is NOT
  supported** by the default transport (no sockets in the browser sandbox, and a browser socket
  can't set custom auth headers). For WebGL or a custom stack, implement `ILelwareRealtimeSocket`
  and pass a factory to the `LelwareRealtime` constructor — the rest (reconnect, subscriptions,
  main-thread marshalling) is transport-agnostic.

## Matchmaking

A player joins a queue and waits to be matched; the match arrives **out-of-band** as a
`match_found` frame on the realtime `matchmaking` channel — so matchmaking pairs with
`LelwareRealtime` (hand-written, not generated). All calls are exception-free (`LelwareResult`).

```csharp
var realtime = new LelwareRealtime(client);
realtime.Start();
realtime.OnMatchFound(m =>
    Debug.Log($"Matched {string.Join(",", m.Players)} (match {m.MatchId})"));

// register once the socket is connected (its ConnectionId is sent so the push reaches THIS socket):
var reg = await client.RegisterMatchmakingAsync(realtime, "ranked-1v1");
if (reg.Error) Debug.LogError($"register ({reg.Code}): {reg.Message}");

// ...later:
await client.LeaveMatchmakingAsync("ranked-1v1");   // idempotent
```

- `RegisterMatchmakingAsync` returns an error result (without hitting the network) if the
  realtime client isn't connected yet — `Start()` and wait for `IsConnected` first.
- `MatchFound.MatchId` **is the created game session's id** when the queue is linked to an
  enabled session definition (pass it to `JoinSession` below); otherwise it's a server-minted id
  with no session behind it. The wire shape is identical either way.

## Game sessions

A session is created server-side when a match forms; its id is the `MatchId` of the
`match_found` frame, and it gets its own **per-session realtime channel**. The player "joins"
by subscribing to that channel, then reads/writes session state through this API (hand-written).
The caller must be logged in and a roster member of the session.

```csharp
realtime.OnMatchFound(m =>
{
    realtime.JoinSession(m.MatchId, json => Debug.Log("session msg: " + json));
    // typed overload also available: realtime.JoinSession<MyMsg>(m.MatchId, msg => ...)
});

// during play (subject to the session definition's flags — a 403 result if players aren't allowed):
await client.SetSessionDataAsync(sessionId, "score", "10");
await client.BroadcastSessionAsync(sessionId, "moved", new { x = 1, y = 2 });

// reads — any roster member:
var data    = await client.GetSessionDataAsync(sessionId);          // all entries (SessionDataEntry[])
var one      = await client.GetSessionDataAsync(sessionId, "score"); // just one key
var players = await client.GetSessionPlayersAsync(sessionId);       // roster (string[])

realtime.LeaveSession(sessionId);   // stop receiving its broadcasts
```

- `BroadcastSessionAsync` / `SetSessionDataAsync` are gated server-side by the session
  definition's player-broadcast / player-set-data flags (a `403` result when players aren't
  allowed — a server-side caller uses the flags differently).
- The join subscription is reconnect-safe (re-applied automatically by the realtime client).

## Notes

- The network layer is `UnityWebRequest` wrapped in an awaiter (`async/await`, works on
  WebGL). Continuation after `await` returns to the main thread.
- Login **and register** return a two-part body (`AccessTokenResponse` + `||Response:{...}`),
  which isn't valid JSON on its own; the SDK splits it on the `||Response:` marker for you
  (`LoginResult.Token` / `LoginResult.Payload`, the OnLogin/OnRegister output in
  `Payload.CustomData`). Register tolerates a missing token half (it need not auto-login).
- Token persistence uses `PlayerPrefs` (plain-text) — `PersistToken` / `TryRestoreToken`
  store the token plus its expiry, and a restore refuses an already-expired token.
```
