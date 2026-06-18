using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lelware.Sdk.Http;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace Lelware.Sdk
{
    /// <summary>
    ///     Client helpers for the portal's object storage. Two namespaces:
    ///     <list type="bullet">
    ///       <item><b>Per-player</b> (<c>UploadAsset</c>/<c>DownloadAsset</c>/<c>ListAssets</c>/
    ///       <c>DeleteAsset</c>) — the caller's own isolated namespace, read+write. Large binaries
    ///       are uploaded via S3 multipart: the portal hands out a presigned PUT URL per part, the
    ///       client uploads each part DIRECTLY to storage (bypassing the portal, IIS limits and
    ///       Cloudflare's 100 MB per-request cap), then the portal finalises the upload.</item>
    ///       <item><b>Per-project SHARED</b> (<c>SharedAssetExists</c>/<c>DownloadSharedAsset</c>/
    ///       <c>ListSharedAssets</c>) — a namespace global to the project, READ-ONLY for clients
    ///       (writes happen server-side).</item>
    ///     </list>
    ///
    ///     <para>Everything here is exception-free — each method returns a
    ///     <see cref="LelwareResult" /> / <see cref="LelwareResult{T}" /> like the rest of the
    ///     SDK. The caller must be logged in (the player is resolved from the bearer token on
    ///     the portal side).</para>
    /// </summary>
    public static class StorageEndpoints
    {
        // S3 requires every part except the last to be >= 5 MiB; 8 MiB is a safe default that
        // also keeps each PUT well under Cloudflare's 100 MB request cap.
        public const int DefaultPartSizeBytes = 8 * 1024 * 1024;

        /// <summary>
        ///     Upload <paramref name="data" /> as <paramref name="name" /> in the player's
        ///     storage, chunked into multipart parts of <paramref name="partSizeBytes" />. On any
        ///     part failure the multipart upload is aborted (best-effort) and an error result is
        ///     returned. Returns a plain <see cref="LelwareResult" /> (no payload) — check
        ///     <see cref="LelwareResult.Error" />.
        ///
        ///     <para>By default an asset that already exists under <paramref name="name" /> is
        ///     left untouched and the call returns success without re-uploading. Pass
        ///     <paramref name="force" /> to overwrite it instead.</para>
        ///
        ///     <para>Pass a <see cref="LelwareTransferProgress" /> in <paramref name="progress" />
        ///     to make the transfer pollable: the SDK does NOT push callbacks — it only keeps the
        ///     handle pointed at the part currently in flight, and the caller READS
        ///     <see cref="LelwareTransferProgress.Fraction" /> /
        ///     <see cref="LelwareTransferProgress.TransferredBytes" /> whenever it wants (e.g. from
        ///     its own <c>Update()</c>). The value is HYBRID — parts exist mainly to slice a big
        ///     file under Cloudflare's 100 MB per-request cap, so it blends finished-part bytes
        ///     with the live byte-level <c>uploadProgress</c> of the in-flight part, i.e.
        ///     <c>(finishedBytes + currentPartFraction*currentPartBytes) / totalBytes</c> — a
        ///     smooth bar even with only a few large parts. Read it on the Unity main thread (the
        ///     getter touches the live UnityWebRequest).</para>
        /// </summary>
        public static async Task<LelwareResult> UploadAssetAsync(
            this LelwareClient client, string name, byte[] data,
            string contentType = null, int partSizeBytes = DefaultPartSizeBytes, bool force = false,
            LelwareTransferProgress progress = null, CancellationToken ct = default)
        {
            if (data == null || data.Length == 0)
            {
                return new LelwareResult { Error = true, Code = 0, Message = "No data to upload." };
            }

            // The total is known up front, so the pollable handle can report a real byte fraction.
            progress?.Begin(data.Length);

            if (partSizeBytes < 5 * 1024 * 1024)
            {
                // Below S3's 5 MiB minimum a multi-part upload would be rejected on complete.
                partSizeBytes = 5 * 1024 * 1024;
            }

            var partCount = (data.Length + partSizeBytes - 1) / partSizeBytes;

            // 1. Initiate — get an upload id + a presigned PUT per part.
            var initBody = JsonConvert.SerializeObject(new { name, contentType, partCount, force });
            var init = await client.SendAsync<InitUploadResponse>(
                UnityWebRequest.kHttpVerbPOST, "Storage/InitiateUpload", null, initBody, ct);
            if (init.Error)
            {
                return init;
            }

            // The portal refuses to overwrite an existing asset: when one already exists under
            // this name it starts no upload and flags AlreadyExists. Treat that as a no-op
            // success (the bytes are already stored) — and crucially check it BEFORE the
            // "no parts" guard below, since an already-exists init legitimately carries no parts.
            if (init.Data != null && init.Data.AlreadyExists)
            {
                // Nothing transferred, but the bytes are present — report the handle as done.
                progress?.Complete();
                return new LelwareResult { Error = false, Code = init.Code };
            }

            if (init.Data?.Parts == null || init.Data.Parts.Count == 0)
            {
                return new LelwareResult { Error = true, Code = init.Code, Message = "Server returned no upload parts." };
            }

            // 2. Upload each part directly to storage, collecting the ETags.
            var completed = new List<CompletedPartDto>();
            foreach (var part in init.Data.Parts)
            {
                var offset = (part.PartNumber - 1) * (long)partSizeBytes;
                var len = (int)Math.Min(partSizeBytes, data.Length - offset);
                var chunk = new byte[len];
                Array.Copy(data, offset, chunk, 0, len);

                // The handle tracks this part live while it uploads; the byte accounting is
                // promoted to "finished" only after the part succeeds (below). There is no await
                // between PutAsync returning and AddCompleted, so a poller never sees a gap.
                var (etag, err) = await PutAsync(client, part.Url, chunk, progress, ct);
                if (err != null)
                {
                    // Best-effort cleanup so we don't leave dangling staged parts.
                    await AbortAsync(client, name, init.Data.UploadId, ct);
                    return new LelwareResult { Error = true, Code = 0, Message = err };
                }

                progress?.AddCompleted(len);
                completed.Add(new CompletedPartDto { PartNumber = part.PartNumber, ETag = etag });
            }

            // 3. Complete — stitch the parts into the final object.
            var completeBody = JsonConvert.SerializeObject(new { name, uploadId = init.Data.UploadId, parts = completed });
            var result = await client.SendAsync(
                UnityWebRequest.kHttpVerbPOST, "Storage/CompleteUpload", null, completeBody, ct);
            if (!result.Error)
            {
                progress?.Complete();
            }

            return result;
        }

        /// <summary>
        ///     Download an asset's raw bytes (resolves a presigned GET URL, then fetches it).
        ///
        ///     <para>Pass a <see cref="LelwareTransferProgress" /> to poll the download: the SDK
        ///     points the handle at the in-flight GET and the caller reads
        ///     <see cref="LelwareTransferProgress.Fraction" /> on demand. The total size isn't
        ///     known until the response headers arrive, so until then <c>Fraction</c> falls back
        ///     to Unity's own <c>downloadProgress</c> (which uses the upstream Content-Length).</para>
        /// </summary>
        public static async Task<LelwareResult<byte[]>> DownloadAssetAsync(
            this LelwareClient client, string name, LelwareTransferProgress progress = null, CancellationToken ct = default)
        {
            var urlRes = await client.SendAsync<UrlResponse>(
                UnityWebRequest.kHttpVerbGET, "Storage/DownloadUrl?name=" + Uri.EscapeDataString(name), null, null, ct);
            if (urlRes.Error)
            {
                return new LelwareResult<byte[]> { Error = true, Code = urlRes.Code, Message = urlRes.Message, RawBody = urlRes.RawBody };
            }

            var (bytes, code, err) = await GetBytesAsync(client, urlRes.Data?.Url, progress, ct);
            if (err == null)
            {
                progress?.Complete();
            }

            return new LelwareResult<byte[]>
            {
                Error = err != null,
                Code = code,
                Message = err,
                Data = bytes
            };
        }

        /// <summary>List the player's stored objects (one page; pass the returned token for more).</summary>
        public static Task<LelwareResult<ListAssetsResponse>> ListAssetsAsync(
            this LelwareClient client, string continuationToken = null, CancellationToken ct = default)
        {
            var action = "Storage/ListAssets";
            if (!string.IsNullOrEmpty(continuationToken))
            {
                action += "?continuationToken=" + Uri.EscapeDataString(continuationToken);
            }

            return client.SendAsync<ListAssetsResponse>(UnityWebRequest.kHttpVerbGET, action, null, null, ct);
        }

        /// <summary>Delete one of the player's assets by name.</summary>
        public static Task<LelwareResult> DeleteAssetAsync(this LelwareClient client, string name, CancellationToken ct = default)
        {
            var body = JsonConvert.SerializeObject(new { name });
            return client.SendAsync(UnityWebRequest.kHttpVerbPOST, "Storage/DeleteAsset", null, body, ct);
        }

        // ===== Per-project SHARED storage (read-only) ==========================
        // A SECOND namespace, {projectId}/shared/..., that is GLOBAL to the project rather than
        // isolated per player (the methods above are per-player). It's for assets that are
        // identical for every viewer — a server-prefetched / derived cache. Clients may only
        // READ it: writes happen server-side (e.g. a server-side prefetch/cache job), so
        // there are deliberately no shared Upload/Delete helpers here.

        /// <summary>True when an object exists under <paramref name="name" /> in the project's shared storage.</summary>
        public static async Task<LelwareResult<bool>> SharedAssetExistsAsync(
            this LelwareClient client, string name, CancellationToken ct = default)
        {
            var res = await client.SendAsync<ExistsResponse>(
                UnityWebRequest.kHttpVerbGET, "SharedStorage/Exists?name=" + Uri.EscapeDataString(name), null, null, ct);
            return new LelwareResult<bool>
            {
                Error = res.Error,
                Code = res.Code,
                Message = res.Message,
                RawBody = res.RawBody,
                Data = res.Data?.Exists ?? false
            };
        }

        /// <summary>
        ///     Download a shared object's raw bytes (resolves a presigned GET URL, then fetches it
        ///     directly from storage — same off-portal path as <see cref="DownloadAssetAsync" />).
        ///     Pass a <see cref="LelwareTransferProgress" /> to poll the download.
        /// </summary>
        public static async Task<LelwareResult<byte[]>> DownloadSharedAssetAsync(
            this LelwareClient client, string name, LelwareTransferProgress progress = null, CancellationToken ct = default)
        {
            var urlRes = await client.SendAsync<UrlResponse>(
                UnityWebRequest.kHttpVerbGET, "SharedStorage/DownloadUrl?name=" + Uri.EscapeDataString(name), null, null, ct);
            if (urlRes.Error)
            {
                return new LelwareResult<byte[]> { Error = true, Code = urlRes.Code, Message = urlRes.Message, RawBody = urlRes.RawBody };
            }

            var (bytes, code, err) = await GetBytesAsync(client, urlRes.Data?.Url, progress, ct);
            if (err == null)
            {
                progress?.Complete();
            }

            return new LelwareResult<byte[]>
            {
                Error = err != null,
                Code = code,
                Message = err,
                Data = bytes
            };
        }

        /// <summary>List the project's shared objects (one page; pass the returned token for more).</summary>
        public static Task<LelwareResult<ListAssetsResponse>> ListSharedAssetsAsync(
            this LelwareClient client, string continuationToken = null, CancellationToken ct = default)
        {
            var action = "SharedStorage/ListAssets";
            if (!string.IsNullOrEmpty(continuationToken))
            {
                action += "?continuationToken=" + Uri.EscapeDataString(continuationToken);
            }

            return client.SendAsync<ListAssetsResponse>(UnityWebRequest.kHttpVerbGET, action, null, null, ct);
        }

        // --- raw transport for the presigned (off-portal) URLs -----------------
        // These talk directly to storage, so they carry NO portal headers (Is-Client /
        // Authorization) — the presigned URL is self-authenticating and extra signed
        // headers would only risk breaking the signature.

        private static async Task<(string etag, string error)> PutAsync(LelwareClient client, string url, byte[] body, LelwareTransferProgress progress, CancellationToken ct)
        {
            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT)
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer()
            };

            // Off-portal byte transfer (presigned S3 PUT). Log the part size, never the URL —
            // a presigned URL embeds the signature, so it's treated as a credential.
            client.LogRequest(UnityWebRequest.kHttpVerbPUT, "<presigned storage URL>", $"<{body.Length} bytes>");

            using var registration = ct.CanBeCanceled ? ct.Register(() => request.Abort()) : default;

            // Expose this request to the pollable handle for the duration of the upload, then
            // detach in the finally — the getter must never read a disposed UnityWebRequest.
            progress?.SetInFlight(request, body.Length, uploading: true);
            try
            {
                await request.SendWebRequest();
            }
            finally
            {
                progress?.ClearInFlight();
            }

            if (ct.IsCancellationRequested)
            {
                client.LogResponse(UnityWebRequest.kHttpVerbPUT, "<presigned storage URL>", error: true,
                    code: 0, message: "cancelled", responseBody: null);
                return (null, "Upload was cancelled.");
            }

#if UNITY_2020_2_OR_NEWER
            var ok = request.result == UnityWebRequest.Result.Success;
#else
            var ok = !request.isHttpError && !request.isNetworkError;
#endif
            if (!ok)
            {
                client.LogResponse(UnityWebRequest.kHttpVerbPUT, "<presigned storage URL>", error: true,
                    request.responseCode, request.error, null);
                return (null, $"Part upload failed ({request.responseCode}): {request.error}");
            }

            client.LogResponse(UnityWebRequest.kHttpVerbPUT, "<presigned storage URL>", error: false,
                request.responseCode, null, null);
            // S3 returns the part's ETag in the response header; it's required on complete.
            return (request.GetResponseHeader("ETag"), null);
        }

        private static async Task<(byte[] bytes, long code, string error)> GetBytesAsync(LelwareClient client, string url, LelwareTransferProgress progress, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url))
            {
                return (null, 0, "No download URL.");
            }

            using var request = UnityWebRequest.Get(url);

            // Off-portal byte transfer (presigned S3 GET). URL hidden — it carries the signature.
            client.LogRequest(UnityWebRequest.kHttpVerbGET, "<presigned storage URL>", null);

            using var registration = ct.CanBeCanceled ? ct.Register(() => request.Abort()) : default;

            // A single GET — no total known up front, so the handle reports Unity's own
            // downloadProgress (driven by the upstream Content-Length) until the bytes land.
            progress?.SetInFlight(request, 0, uploading: false);
            try
            {
                await request.SendWebRequest();
            }
            finally
            {
                progress?.ClearInFlight();
            }

            if (ct.IsCancellationRequested)
            {
                client.LogResponse(UnityWebRequest.kHttpVerbGET, "<presigned storage URL>", error: true,
                    code: 0, message: "cancelled", responseBody: null);
                return (null, 0, "Download was cancelled.");
            }

#if UNITY_2020_2_OR_NEWER
            var ok = request.result == UnityWebRequest.Result.Success;
#else
            var ok = !request.isHttpError && !request.isNetworkError;
#endif
            var data = ok ? request.downloadHandler.data : null;
            client.LogResponse(UnityWebRequest.kHttpVerbGET, "<presigned storage URL>", error: !ok,
                request.responseCode, ok ? null : request.error,
                ok ? $"<{data?.Length ?? 0} bytes>" : null);
            return ok
                ? (data, request.responseCode, null)
                : (null, request.responseCode, $"Download failed ({request.responseCode}): {request.error}");
        }

        private static Task<LelwareResult> AbortAsync(LelwareClient client, string name, string uploadId, CancellationToken ct)
        {
            var body = JsonConvert.SerializeObject(new { name, uploadId });
            return client.SendAsync(UnityWebRequest.kHttpVerbPOST, "Storage/AbortUpload", null, body, ct);
        }

        // --- wire DTOs ---------------------------------------------------------

        [Serializable]
        public sealed class InitUploadResponse
        {
            [JsonProperty("key")] public string Key;
            [JsonProperty("uploadId")] public string UploadId;
            [JsonProperty("parts")] public List<PresignedPartDto> Parts;

            /// <summary>True when the asset already existed and was left untouched (no-op success).</summary>
            [JsonProperty("alreadyExists")] public bool AlreadyExists;
        }

        [Serializable]
        public sealed class PresignedPartDto
        {
            [JsonProperty("partNumber")] public int PartNumber;
            [JsonProperty("url")] public string Url;
        }

        [Serializable]
        public sealed class CompletedPartDto
        {
            [JsonProperty("partNumber")] public int PartNumber;
            [JsonProperty("eTag")] public string ETag;
        }

        [Serializable]
        public sealed class UrlResponse
        {
            [JsonProperty("url")] public string Url;
        }

        [Serializable]
        public sealed class ExistsResponse
        {
            [JsonProperty("exists")] public bool Exists;
        }

        [Serializable]
        public sealed class ListAssetsResponse
        {
            [JsonProperty("objects")] public List<StorageObjectDto> Objects;
            [JsonProperty("truncated")] public bool Truncated;
            [JsonProperty("continuationToken")] public string ContinuationToken;
        }

        [Serializable]
        public sealed class StorageObjectDto
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("size")] public long Size;
            [JsonProperty("lastModified")] public DateTime LastModified;
        }
    }

    /// <summary>
    ///     A POLLABLE progress handle for a storage upload/download. The caller creates one,
    ///     passes it to <see cref="StorageEndpoints.UploadAssetAsync" /> /
    ///     <see cref="StorageEndpoints.DownloadAssetAsync" />, and then READS
    ///     <see cref="Fraction" />/<see cref="TransferredBytes" />/<see cref="IsComplete" />
    ///     whenever it likes (typically each frame from its own <c>Update()</c>).
    ///
    ///     <para>By design the SDK pushes NOTHING — no callbacks, no coroutine, no MonoBehaviour.
    ///     It only updates a couple of fields at part boundaries and keeps a pointer to the part
    ///     currently in flight; the live byte fraction is read off that <see cref="UnityWebRequest" />
    ///     lazily, AT THE MOMENT YOU POLL. That keeps the cost zero when nobody's looking.</para>
    ///
    ///     <para><b>Threading:</b> read this on the Unity main thread. The getters touch the live
    ///     UnityWebRequest, and the SDK mutates the handle from the awaiter continuation (which
    ///     also resumes on the main thread), so a main-thread poll never races those writes.</para>
    ///
    ///     <para><b>Reuse:</b> one handle per transfer — don't share a single instance across two
    ///     concurrent uploads. A new transfer resets it via <see cref="Begin" />.</para>
    /// </summary>
    public sealed class LelwareTransferProgress
    {
        // Bytes from parts that have FULLY completed (promoted in AddCompleted). The part in
        // flight is NOT counted here — its contribution is read live from _current below.
        private long _completedBytes;

        // The request currently transferring, or null between parts / once done. Read-only use
        // from the getters; cleared in PutAsync/GetBytesAsync's finally before the request is
        // disposed, so a getter never dereferences a disposed UnityWebRequest.
        private UnityWebRequest _current;
        private long _currentPartBytes; // size of the in-flight part (0 = unknown, e.g. download)
        private bool _uploading;        // pick uploadProgress vs downloadProgress on the live request

        /// <summary>Total bytes of the whole transfer, or 0 when not yet known (download in flight).</summary>
        public long TotalBytes { get; private set; }

        /// <summary>True once the transfer finished successfully (or was a no-op already-exists upload).</summary>
        public bool IsComplete { get; private set; }

        /// <summary>
        ///     Bytes transferred so far = finished-part bytes + the live byte progress of the part
        ///     in flight. For a download with no known total it falls back to the request's own
        ///     <c>downloadedBytes</c>.
        /// </summary>
        public long TransferredBytes
        {
            get
            {
                var req = _current;
                if (req != null)
                {
                    if (_uploading)
                    {
                        var f = Clamp01(req.uploadProgress);
                        var inFlight = (long)(f * _currentPartBytes);
                        var done = _completedBytes + inFlight;
                        return TotalBytes > 0 && done > TotalBytes ? TotalBytes : done;
                    }

                    // Download: Unity tracks the byte count for the single GET.
                    return (long)req.downloadedBytes;
                }

                return IsComplete && TotalBytes > 0 ? TotalBytes : _completedBytes;
            }
        }

        /// <summary>
        ///     Overall progress in <c>[0,1]</c>. Uses the byte total when known; for a download
        ///     before headers arrive it returns the request's own <c>downloadProgress</c> (driven
        ///     by the upstream Content-Length). Returns 1 once <see cref="IsComplete" />.
        /// </summary>
        public float Fraction
        {
            get
            {
                if (IsComplete)
                {
                    return 1f;
                }

                if (TotalBytes > 0)
                {
                    return Clamp01((float)TransferredBytes / TotalBytes);
                }

                // Unknown total (download in flight) — defer to Unity's reported progress.
                var req = _current;
                return req != null ? Clamp01(req.downloadProgress) : 0f;
            }
        }

        // --- SDK-internal mutators (called from StorageEndpoints) ---------------

        /// <summary>Start a fresh transfer with a known total; resets all state.</summary>
        internal void Begin(long totalBytes)
        {
            TotalBytes = totalBytes;
            _completedBytes = 0;
            _current = null;
            _currentPartBytes = 0;
            _uploading = false;
            IsComplete = false;
        }

        /// <summary>Point the handle at the request now transferring so the getters can read it live.</summary>
        internal void SetInFlight(UnityWebRequest request, long partBytes, bool uploading)
        {
            _current = request;
            _currentPartBytes = partBytes;
            _uploading = uploading;
        }

        /// <summary>Detach the in-flight request (before it's disposed). Completed bytes are unchanged.</summary>
        internal void ClearInFlight()
        {
            _current = null;
            _currentPartBytes = 0;
        }

        /// <summary>Promote a finished part's bytes into the completed total.</summary>
        internal void AddCompleted(long bytes)
        {
            _completedBytes += bytes;
        }

        /// <summary>Mark the whole transfer done — Fraction pins to 1.</summary>
        internal void Complete()
        {
            _current = null;
            _currentPartBytes = 0;
            if (TotalBytes > 0)
            {
                _completedBytes = TotalBytes;
            }

            IsComplete = true;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
