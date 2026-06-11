# Lelware SDK (Unity)

Client SDK for the LelwarePortal API. It logs in with a bearer token, caches it,
and attaches it to every request, always sends the `Is-Client: api-client` header,
and lets you generate typed request/response classes from a schema manifest
(build-time) — with a generic escape hatch for any custom schema of your own.

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
    projectId: "Clearwater",          // project ID = route segment (GUID or literal)
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

await client.SetPlayerDataAsync("level", 7);

var lvl = await client.GetPlayerDataAsync("level");          // LelwareResult<PlayerDataEntryDto>
if (lvl.Error && lvl.Code == 404) { /* key does not exist */ }
else if (lvl.Ok) { int level = lvl.Data.value.As<int>(); }   // .As<T>() from Lelware.Sdk

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

The portal exposes **one global OpenAPI 3 document** for the entire client API
(`GET /api/sdk/OpenApi`), from which the SDK generates all calls. **The only permanent
hand-written part is `Authentication`** (login + token); additionally `Storage` (multipart
presigned) has its own hand-written module. Everything else — static endpoints
(`GetSettings`, player-data CRUD) and **the routes of all scripts across all projects**
(a distinct union) — is generated dynamically.

1. In the portal, set the API key in `appsettings` (`Api:Key`).
2. In Unity: `Tools > Lelware > Generate SDK from Portal` → provide the Base URL + secret →
   *Fetch schema & generate*. It writes `Assets/Lelware/Generated/LelwarePortalApi.Generated.cs`.

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
- **All** endpoints under `/api/...` are generated except `Authentication` (hand-written login)
  and `Storage` (hand-written multipart). This includes endpoints outside the
  `api/{projectId}/...` schema — e.g. Clearwater's `api/clearwater/tiles/{x}/{y}/{z}.vector`.
  The remaining path params (here `x/y/z`) become method arguments; `projectId`/`pid` always
  come from the client.
- Endpoints returning bytes (e.g. Clearwater tiles, `application/x-protobuf`) are generated
  as `LelwareResult<byte[]>` (data in `.Data`); JSON → `LelwareResult<T>`; empty 2xx →
  `LelwareResult`. For binary content to appear in OpenAPI, the action must declare it
  (`[Produces(...)]` + `[ProducesResponseType(typeof(byte[]), 200)]`).
- Scripts have no parameter schema on the server side, so their request/response are
  **loosely typed** (`object`). To type them — see the manifest below.
- The schema endpoint is protected by an API key (`X-Api-Key`), compared in constant time
  against `Api:Key`. Without a configured key the endpoint is closed (fail-safe).

## Typing scripts with a manifest (optional)

Custom scripts (`api/{pid}/RunScript/{route}`) have no static schema on the server side —
the script reads raw JSON. So for **selected** scripts that you want to give strong
request/response types, you **describe the shape in a JSON manifest**, and the generator
emits typed classes and call methods from it. (Method names differ from the portal mode —
there it's `Script{Route}Async`, here it's `{Name}Async` — so they don't collide unless you
give one the same name yourself.)

1. `Tools > Lelware > Create Sample Schema` — creates `Assets/Lelware/lelware-sdk.json`.
2. Edit the manifest (example):

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

3. `Tools > Lelware > Generate SDK` — writes
   `Assets/Lelware/Generated/LelwareSdk.Generated.cs`.

Generated calls (extension methods on `LelwareClient`):

```csharp
using Lelware.Sdk.Generated;

var res = await client.SaveItemAsync(new SaveItemRequest { id = "sword", qty = 1 });
if (res.Ok) Debug.Log($"ok: {res.Data.ok}");
var items = await client.ListItemsAsync(new ListItemsRequest { page = 0 });
```

### Manifest fields

- `types[]` — reusable DTOs (`name`, `fields`).
- `endpoints[]`:
  - `name` — the `{Name}Async` method, the `{Name}Request` / `{Name}Response` classes.
  - `route` — the `RunScript/{route}` segment.
  - `method` — `POST` (JSON body) or `GET` (query string).
  - `request` / `response` — either `{ "type": "..." }` (use an existing type), or
    `{ "fields": [...] }` (generate a class). An omitted `request` = a parameterless method.
- `fields[]` — `name` (the C# and JSON field name), `type` (any C# type, e.g. `int`,
  `List<Foo>`, `Foo[]`), optionally `json` (a different name on the wire → `[JsonProperty]`).

Generated classes are `partial` — you can extend them in a separate file without losing
changes on regeneration.

## Custom schema without the generator (escape hatch)

For anything not in the manifest — your own classes + a generic call:

```csharp
class MyReq  { public int a; public string b; }
class MyResp { public bool ok; public string msg; }

var r = await client.CallScriptAsync<MyReq, MyResp>("myRoute", new MyReq { a = 1, b = "x" });
if (r.Ok) Debug.Log(r.Data.msg);
// GET variant:
var r2 = await client.GetScriptAsync<MyResp>("myRoute",
    new Dictionary<string,string> { ["x"] = "1" });
```

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

## Notes

- The network layer is `UnityWebRequest` wrapped in an awaiter (`async/await`, works on
  WebGL). Continuation after `await` returns to the main thread.
- Login **and register** return a two-part body (`AccessTokenResponse` + `||Response:{...}`),
  which isn't valid JSON on its own; the SDK splits it on the `||Response:` marker for you
  (`LoginResult.Token` / `LoginResult.Payload`, the OnLogin/OnRegister output in
  `Payload.CustomData`). Register tolerates a missing token half (it need not auto-login).
- `GetSettings` on the portal side requires a route segment, even though it ignores it — the
  SDK sends a placeholder, so you don't have to do anything.
```
