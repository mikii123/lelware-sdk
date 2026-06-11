using System;
using System.Runtime.CompilerServices;
using UnityEngine.Networking;

namespace Lelware.Sdk.Http
{
    /// <summary>
    ///     Makes a <see cref="UnityWebRequestAsyncOperation" /> directly awaitable so the
    ///     whole SDK can be written in <c>async</c>/<c>await</c> rather than coroutines.
    ///
    ///     Unity does NOT ship a built-in awaiter for <see cref="UnityWebRequestAsyncOperation" />
    ///     (only <c>Awaitable</c> in very recent versions), so we provide the thin wrapper
    ///     ourselves. We deliberately stay on <see cref="UnityWebRequest" /> rather than
    ///     <c>System.Net.Http.HttpClient</c> because UnityWebRequest is the only transport
    ///     that works uniformly across every Unity platform — notably WebGL, where the
    ///     managed socket stack HttpClient needs simply isn't available.
    ///
    ///     The continuation is invoked from <see cref="AsyncOperation.completed" />, which
    ///     Unity raises on the main thread — so awaiting an SDK call keeps you on the main
    ///     thread, safe to touch the Unity API right after the <c>await</c>.
    /// </summary>
    public static class UnityWebRequestAwaiterExtensions
    {
        public static UnityWebRequestAwaiter GetAwaiter(this UnityWebRequestAsyncOperation operation)
        {
            return new UnityWebRequestAwaiter(operation);
        }
    }

    /// <summary>Awaiter that bridges Unity's callback-style async op into the C# await pattern.</summary>
    public readonly struct UnityWebRequestAwaiter : INotifyCompletion
    {
        private readonly UnityWebRequestAsyncOperation _operation;

        public UnityWebRequestAwaiter(UnityWebRequestAsyncOperation operation)
        {
            _operation = operation;
        }

        // isDone flips true once Unity has finished the request; the state machine reads
        // this first and skips scheduling a continuation entirely if it's already done
        // (e.g. a cached/instant failure), avoiding an extra frame of latency.
        public bool IsCompleted => _operation.isDone;

        public void OnCompleted(Action continuation)
        {
            // Guard against the race where the op completes between the IsCompleted check
            // and registering the callback: if it's already done, run inline.
            if (_operation.isDone)
            {
                continuation();
                return;
            }

            _operation.completed += _ => continuation();
        }

        // Nothing to return — callers read the result off the UnityWebRequest itself.
        public void GetResult()
        {
        }
    }
}
