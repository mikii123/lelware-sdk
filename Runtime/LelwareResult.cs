namespace Lelware.Sdk
{
    /// <summary>
    ///     The outcome of any SDK call. The SDK is exception-free at the API surface: instead
    ///     of throwing on a bad status, a transport failure, or a malformed body, every call
    ///     returns one of these so callers can branch on <see cref="Error" /> / <see cref="Code" />
    ///     without try/catch.
    ///
    ///     <para>Three failure modes all collapse into <c>Error == true</c>:</para>
    ///     <list type="bullet">
    ///       <item><description>transport/connection failure that never reached the server —
    ///         <see cref="Code" /> is 0;</description></item>
    ///       <item><description>an HTTP status outside 2xx — <see cref="Code" /> is that status
    ///         and <see cref="RawBody" /> carries the portal's terse error string;</description></item>
    ///       <item><description>a 2xx response whose body couldn't be parsed into the expected
    ///         type — <see cref="Code" /> is the (successful) status but <see cref="Error" /> is
    ///         still true, with the parse error in <see cref="Message" />.</description></item>
    ///     </list>
    /// </summary>
    public class LelwareResult
    {
        /// <summary>True for ANY non-success outcome (transport, non-2xx, or parse failure).</summary>
        public bool Error;

        /// <summary>
        ///     HTTP status code of the response, or 0 when the request failed before a response
        ///     arrived (transport/connection error, cancellation).
        /// </summary>
        public long Code;

        /// <summary>Human-readable error description when <see cref="Error" /> is true; null on success.</summary>
        public string Message;

        /// <summary>Raw response body as received, if any. Useful for inspecting portal error text.</summary>
        public string RawBody;

        /// <summary>Convenience inverse of <see cref="Error" />.</summary>
        public bool Ok => !Error;

        /// <summary>True when the failure was authentication/authorization (401/403).</summary>
        public bool IsAuthError => Code == 401 || Code == 403;
    }

    /// <summary>
    ///     A <see cref="LelwareResult" /> that also carries a deserialized payload in
    ///     <see cref="Data" />. On any failure <see cref="Data" /> is <c>default</c> — always
    ///     check <see cref="LelwareResult.Error" /> (or <see cref="LelwareResult.Ok" />) before
    ///     reading it. An empty (but successful) 2xx body also yields <c>default</c> data.
    /// </summary>
    public sealed class LelwareResult<T> : LelwareResult
    {
        /// <summary>The deserialized response, or <c>default</c> on failure / empty body.</summary>
        public T Data;
    }
}
