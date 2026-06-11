using System.Collections.Generic;
using Newtonsoft.Json;

namespace Lelware.Sdk.Editor
{
    /// <summary>
    ///     Deserialized form of the SDK schema manifest (the JSON file you author/edit). This
    ///     is the single source of truth the build-time generator reads to emit typed request/
    ///     response classes and the strongly-typed call methods for each custom script endpoint.
    ///
    ///     The portal has no static parameter schema for custom scripts — a script just reads a
    ///     raw <c>JsonElement</c> — so the SDK can't infer types from the server. You describe
    ///     the shape you expect here, regenerate, and get compile-time-checked calls. Anything
    ///     not described here still works via the generic
    ///     <see cref="LelwareClient.CallScriptAsync{TRequest,TResponse}" /> escape hatch.
    /// </summary>
    public sealed class SdkSchema
    {
        /// <summary>Reusable DTO types referenced by endpoints' request/response field types.</summary>
        [JsonProperty("types")] public List<TypeDef> Types = new List<TypeDef>();

        /// <summary>Custom script endpoints to generate typed call methods for.</summary>
        [JsonProperty("endpoints")] public List<EndpointDef> Endpoints = new List<EndpointDef>();
    }

    public sealed class TypeDef
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("fields")] public List<FieldDef> Fields = new List<FieldDef>();
    }

    public sealed class FieldDef
    {
        /// <summary>C# member name. Also the JSON wire name unless <see cref="Json" /> overrides it.</summary>
        [JsonProperty("name")] public string Name;

        /// <summary>C# type, passed through verbatim — e.g. <c>int</c>, <c>string</c>, <c>List&lt;Foo&gt;</c>, <c>Foo[]</c>.</summary>
        [JsonProperty("type")] public string Type;

        /// <summary>Optional JSON wire name, when it differs from <see cref="Name" /> (emits a [JsonProperty]).</summary>
        [JsonProperty("json")] public string Json;
    }

    public sealed class EndpointDef
    {
        /// <summary>Logical name; the generated method is <c>{Name}Async</c> and classes are <c>{Name}Request/Response</c>.</summary>
        [JsonProperty("name")] public string Name;

        /// <summary>The script route — the trailing segment of <c>api/{pid}/RunScript/{route}</c>.</summary>
        [JsonProperty("route")] public string Route;

        /// <summary>HTTP method: <c>POST</c> (body) or <c>GET</c> (query string). Defaults to POST.</summary>
        [JsonProperty("method")] public string Method = "POST";

        /// <summary>Request shape. Provide <see cref="ShapeDef.Type" /> to reuse a type, or <see cref="ShapeDef.Fields" /> to generate one.</summary>
        [JsonProperty("request")] public ShapeDef Request;

        /// <summary>Response shape. Same rules as <see cref="Request" />.</summary>
        [JsonProperty("response")] public ShapeDef Response;
    }

    /// <summary>
    ///     A request or response shape: either a reference to an existing type (<see cref="Type" />)
    ///     or an inline set of <see cref="Fields" /> the generator turns into a dedicated class.
    /// </summary>
    public sealed class ShapeDef
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("fields")] public List<FieldDef> Fields;
    }
}
