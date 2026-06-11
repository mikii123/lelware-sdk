# Lelware SDK (Unity)

Kliencki SDK do API portalu LelwarePortal. Loguje się przez bearer token, cache'uje
go i dokleja do każdego żądania, zawsze wysyła nagłówek `Is-Client: api-client`, oraz
pozwala generować typowane klasy request/response z manifestu schematu (build-time) —
z generycznym escape hatchem dla dowolnych własnych schem.

## Instalacja (UPM)

Pakiet zależy od `com.unity.nuget.newtonsoft-json` (rozwiązuje się automatycznie z
Package Managera). Dodaj do `Packages/manifest.json` projektu Unity:

```json
"com.lelware.sdk": "https://dev.azure.com/.../_git/...?path=/sdk/unity/com.lelware.sdk"
```

albo skopiuj folder `com.lelware.sdk/` do `Packages/` w projekcie.

## Szybki start

```csharp
using Lelware.Sdk;

var config = new LelwareClientConfig(
    baseUrl:   "https://portal.lelware.com",
    projectId: "Clearwater",          // ID projektu = segment route (GUID lub literał)
    deviceId:  SystemInfo.deviceUniqueIdentifier); // opcjonalnie, stałe per urządzenie

var client = new LelwareClient(config);
```

`LelwareClientConfig` jest `[Serializable]`, więc zamiast tworzyć go w kodzie możesz
wystawić go jako pole na `MonoBehaviour`/`ScriptableObject` i wypełnić w Inspectorze:

```csharp
public class LelwareBootstrap : MonoBehaviour
{
    [SerializeField] private LelwareClientConfig config; // edytowalne w Inspectorze
    private LelwareClient _client;

    private void Awake()
    {
        var err = config.Validate();        // null = ok, inaczej komunikat
        if (err != null) { Debug.LogError(err); return; }
        _client = new LelwareClient(config);
    }
}

// 1. Login — token jest cache'owany w pamięci i dołączany do kolejnych wywołań.
var login = await client.LoginAsync("user@example.com", "haslo");
if (login.Error)
{
    Debug.LogError($"Login nieudany ({login.Code}): {login.Message}");
    return;
}
Debug.Log($"PlayerID: {login.Data.PlayerId}");

// (opcjonalnie) zachowaj token między uruchomieniami:
client.PersistToken();
// ...przy starcie:  if (client.TryRestoreToken()) { /* pomijamy login */ }

// 2. Stałe endpointy portalu (GENEROWANE z OpenAPI, `using Lelware.Sdk.Generated;`) —
//    wszystkie zwracają LelwareResult<T>:
var settings = await client.GetSettingsAsync();
if (settings.Ok) foreach (var s in settings.Data) { /* ... */ }

await client.SetPlayerDataAsync("level", 7);

var lvl = await client.GetPlayerDataAsync("level");          // LelwareResult<PlayerDataEntryDto>
if (lvl.Error && lvl.Code == 404) { /* klucz nie istnieje */ }
else if (lvl.Ok) { int level = lvl.Data.value.As<int>(); }   // .As<T>() z Lelware.Sdk

var all = await client.GetAllPlayerDataAsync();
await client.DeletePlayerDataAsync("level");
```

Każde wywołanie automatycznie:
- ustawia `Is-Client: api-client`,
- dołącza `Authorization: Bearer <token>` (po loginie),
- wysyła `X-Device-Id`, jeśli podano `deviceId` w configu.

## Obsługa błędów (bez wyjątków)

SDK **nigdy nie rzuca wyjątków** na powierzchni API. Każde wywołanie zwraca
`LelwareResult` (warianty bez body, np. `SetPlayerData`/`Delete`) albo
`LelwareResult<T>` (z payloadem w `Data`). Pola:

- `Error` (`bool`) — `true` dla dowolnego niepowodzenia,
- `Code` (`long`) — status HTTP, albo `0` dla błędu transportu / anulowania / błędu
  serializacji przed wysłaniem,
- `Message` (`string`) — opis błędu (`null` przy sukcesie),
- `RawBody` (`string`) — surowe body odpowiedzi (np. terse error z portalu),
- `Ok` — odwrotność `Error`; `IsAuthError` — `true` dla 401/403,
- `Data` (tylko `LelwareResult<T>`) — zdeserializowany payload, `default` przy błędzie
  lub pustym 2xx.

`Error == true` zbiera trzy przypadki: błąd transportu (`Code == 0`), status spoza 2xx
(`Code` = status, `RawBody` = treść błędu) oraz poprawne 2xx z body, którego nie dało
się sparsować (`Code` = status 2xx, ale `Error == true`, a powód w `Message`).

## Generowanie z portalu (OpenAPI) — główny tryb

Portal wystawia **jeden globalny dokument OpenAPI 3** dla całego client API
(`GET /api/sdk/OpenApi`), z którego SDK generuje wszystkie wywołania. **Stały ręczny
jest tylko `Authentication`** (login + token); dodatkowo `Storage` (multipart presigned)
ma własny ręczny moduł. Cała reszta — statyczne endpointy (`GetSettings`, player-data
CRUD) i **route'y wszystkich skryptów ze wszystkich projektów** (unia distinct) — jest
generowana dynamicznie.

1. W portalu ustaw API key w `appsettings` (`Api:Key`).
2. W Unity: `Tools > Lelware > Generate SDK from Portal` → podaj Base URL + sekret →
   *Fetch schema & generate*. Zapisuje `Assets/Lelware/Generated/LelwarePortalApi.Generated.cs`.

```csharp
using Lelware.Sdk.Generated;

var all = await client.GetAllPlayerDataAsync();          // List<PlayerDataEntryDto>
var s   = await client.GetSettingsAsync();               // List<ProjectSetting>
var r   = await client.ScriptExampleAsync(new { input = "x" }); // skrypt 'example'
```

Uwagi:
- `projectId` **nie** jest argumentem metody — bierze się z `LelwareClient.ProjectId`
  (seedowane z configu, zmienialne w runtime), więc jeden wygenerowany SDK obsługuje każdy
  projekt (route, którego dany projekt nie ma, zwróci 404 w runtime).
- Generują się **wszystkie** endpointy pod `/api/...` poza `Authentication` (ręczny login)
  i `Storage` (ręczny multipart). To obejmuje endpointy spoza schematu `api/{projectId}/...` —
  np. Clearwater `api/clearwater/tiles/{x}/{y}/{z}.vector`. Pozostałe path‑paramy (tu `x/y/z`)
  stają się argumentami metody; `projectId`/`pid` zawsze idą z clienta.
- Endpointy zwracające bajty (np. kafelki Clearwater, `application/x-protobuf`) generują się
  jako `LelwareResult<byte[]>` (dane w `.Data`); JSON → `LelwareResult<T>`; puste 2xx →
  `LelwareResult`. Żeby binarny content wyszedł w OpenAPI, akcja musi go zadeklarować
  (`[Produces(...)]` + `[ProducesResponseType(typeof(byte[]), 200)]`).
- Skrypty nie mają schemy parametrów po stronie serwera, więc ich request/response są
  **luźno typowane** (`object`). Żeby je otypować — patrz manifest niżej.
- Endpoint schemy jest chroniony API key'em (`X-Api-Key`), porównywanym w stałym czasie
  z `Api:Key`. Bez skonfigurowanego klucza endpoint jest zamknięty (fail-safe).

## Otypowanie skryptów manifestem (opcjonalne)

Custom skrypty (`api/{pid}/RunScript/{route}`) nie mają statycznej schemy po stronie
serwera — skrypt czyta surowy JSON. Dlatego dla **wybranych** skryptów, którym chcesz dać
mocne typy request/response, **opisujesz kształt w manifeście JSON**, a generator emituje
z niego typowane klasy i metody wywołań. (Nazwy metod różnią się od trybu portalowego —
tam `Script{Route}Async`, tu `{Name}Async` — więc nie kolidują, dopóki sam nie nadasz
tej samej nazwy.)

1. `Tools > Lelware > Create Sample Schema` — tworzy `Assets/Lelware/lelware-sdk.json`.
2. Edytuj manifest (przykład):

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

3. `Tools > Lelware > Generate SDK` — zapisuje
   `Assets/Lelware/Generated/LelwareSdk.Generated.cs`.

Wygenerowane wywołania (extension methods na `LelwareClient`):

```csharp
using Lelware.Sdk.Generated;

var res = await client.SaveItemAsync(new SaveItemRequest { id = "sword", qty = 1 });
if (res.Ok) Debug.Log($"ok: {res.Data.ok}");
var items = await client.ListItemsAsync(new ListItemsRequest { page = 0 });
```

### Pola manifestu

- `types[]` — wielokrotnego użytku DTO (`name`, `fields`).
- `endpoints[]`:
  - `name` — metoda `{Name}Async`, klasy `{Name}Request` / `{Name}Response`.
  - `route` — segment `RunScript/{route}`.
  - `method` — `POST` (body JSON) lub `GET` (query string).
  - `request` / `response` — albo `{ "type": "..." }` (użyj istniejącego typu), albo
    `{ "fields": [...] }` (wygeneruj klasę). Pominięty `request` = metoda bez parametru.
- `fields[]` — `name` (nazwa pola C# i JSON), `type` (dowolny typ C#, np. `int`,
  `List<Foo>`, `Foo[]`), opcjonalnie `json` (inna nazwa na drucie → `[JsonProperty]`).

Wygenerowane klasy są `partial` — możesz je dopisywać w osobnym pliku bez utraty zmian
przy regeneracji.

## Własna schema bez generatora (escape hatch)

Dla wszystkiego, czego nie ma w manifeście — własne klasy + generyczne wywołanie:

```csharp
class MyReq  { public int a; public string b; }
class MyResp { public bool ok; public string msg; }

var r = await client.CallScriptAsync<MyReq, MyResp>("myRoute", new MyReq { a = 1, b = "x" });
if (r.Ok) Debug.Log(r.Data.msg);
// GET wariant:
var r2 = await client.GetScriptAsync<MyResp>("myRoute",
    new Dictionary<string,string> { ["x"] = "1" });
```

## Storage (assety per-player, multipart)

Pliki binarne trzymane są w object storage (MinIO/S3) w izolowanej przestrzeni
playera (`{projectId}/players/{playerId}/...`). Upload idzie **multipart, bezpośrednio do
storage** przez presigned URL-e (omija portal, limity IIS i 100 MB Cloudflare); portal tylko
podpisuje URL-e i finalizuje upload. Wszystko zwraca `LelwareResult` (bez wyjątków):

```csharp
// upload (chunkowany automatycznie, domyślnie 8 MiB/part):
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

### Progress (pollowalny, bez callbacków)

Upload i download przyjmują opcjonalny `LelwareTransferProgress`. SDK **niczego nie pushuje**
(brak callbacków co klatkę, coroutine ani MonoBehaviour) — trzyma tylko wskaźnik na żądanie
aktualnie w locie, a Ty **odczytujesz** `Fraction` / `TransferredBytes` / `IsComplete` kiedy
chcesz (zwykle w swoim `Update()`, na main thread). Postęp jest hybrydowy: ukończone party +
bajtowy `uploadProgress` party w locie (party istnieją głównie po to, by duży plik przeszedł
przez limit 100 MB Cloudflare).

```csharp
var progress = new LelwareTransferProgress();
var task = client.UploadAssetAsync("avatars/hero.png", bytes, contentType: "image/png", progress: progress);

// gdzieś w Update() / własnej pętli, na main thread:
slider.value = progress.Fraction;          // 0..1
label.text = $"{progress.TransferredBytes}/{progress.TotalBytes} B";

var up = await task;
if (up.Error) Debug.LogError($"upload ({up.Code}): {up.Message}");

// download — total nieznany do nadejścia nagłówków, wtedy Fraction = downloadProgress Unity:
var dlProgress = new LelwareTransferProgress();
var dl = await client.DownloadAssetAsync("avatars/hero.png", progress: dlProgress);
```

Izolacja: playerId bierze portal z bearer tokenu (nigdy z requestu) — gracz operuje wyłącznie
na swojej przestrzeni. Wymaga skonfigurowanego `Storage:*` po stronie portalu.

## Uwagi

- Warstwa sieciowa to `UnityWebRequest` opakowany w awaiter (`async/await`, działa na
  WebGL). Kontynuacja po `await` wraca na main thread.
- Login zwraca dwuczęściowy body (`AccessTokenResponse` + `||Response:{...}`); SDK
  rozdziela to za Ciebie (`LoginResult.Token` / `LoginResult.Payload`).
- `GetSettings` po stronie portalu wymaga segmentu route, choć go ignoruje — SDK wysyła
  placeholder, nie musisz nic robić.
