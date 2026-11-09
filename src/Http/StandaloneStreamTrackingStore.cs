/*
┌───────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                      │
│  Repository: GitHub (https://github.com/IvanMurzak/GameDev-MCP-Server)    │
│  Copyright (c) 2026 Ivan Murzak                                           │
│  Licensed under the Apache License, Version 2.0.                          │
│  See the LICENSE file in the project root for more information.           │
└───────────────────────────────────────────────────────────────────────────┘
*/

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.ServerSentEvents;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

#pragma warning disable MCP9006 // The stateful Streamable HTTP event store is the API this server runs on.

namespace com.IvanMurzak.GameDev.MCP.Server.Http
{
    /// <summary>
    /// Records which sessions have a standalone GET stream writer in THIS process, so
    /// <see cref="StaleStandaloneStreamResumeMiddleware"/> can tell a resume the session can serve from
    /// one left over from a previous process. The SDK disposes the writer only with the transport, not
    /// when a GET ends, so an entry lives exactly as long as the session can still deliver notifications.
    /// </summary>
    public sealed class StandaloneStreamTrackingStore : ISseEventStreamStore
    {
        readonly ISseEventStreamStore _inner;
        readonly ConcurrentDictionary<string, object> _liveWriters = new();

        public StandaloneStreamTrackingStore(ISseEventStreamStore inner) => _inner = inner;

        public bool HasLiveWriter(string sessionId) => _liveWriters.ContainsKey(sessionId);

        public async ValueTask<ISseEventStreamWriter> CreateStreamAsync(SseEventStreamOptions options, CancellationToken cancellationToken = default)
        {
            var writer = await _inner.CreateStreamAsync(options, cancellationToken).ConfigureAwait(false);
            if (options.StreamId != StreamableHttpServerTransport.UnsolicitedMessageStreamId)
                return writer;

            // Keyed removal: disposing a replaced session's writer must not drop its successor's entry.
            var entry = new KeyValuePair<string, object>(options.SessionId, new object());
            _liveWriters[entry.Key] = entry.Value;
            return new TrackedWriter(writer, () => _liveWriters.TryRemove(entry));
        }

        public ValueTask<ISseEventStreamReader?> GetStreamReaderAsync(string lastEventId, CancellationToken cancellationToken = default)
            => _inner.GetStreamReaderAsync(lastEventId, cancellationToken);

        sealed class TrackedWriter : ISseEventStreamWriter
        {
            readonly ISseEventStreamWriter _inner;
            readonly System.Action _onDisposed;

            public TrackedWriter(ISseEventStreamWriter inner, System.Action onDisposed)
            {
                _inner = inner;
                _onDisposed = onDisposed;
            }

            public ValueTask SetModeAsync(SseEventStreamMode mode, CancellationToken cancellationToken = default)
                => _inner.SetModeAsync(mode, cancellationToken);

            public ValueTask<SseItem<JsonRpcMessage?>> WriteEventAsync(SseItem<JsonRpcMessage?> sseItem, CancellationToken cancellationToken = default)
                => _inner.WriteEventAsync(sseItem, cancellationToken);

            public ValueTask DisposeAsync()
            {
                _onDisposed();
                return _inner.DisposeAsync();
            }
        }
    }
}
