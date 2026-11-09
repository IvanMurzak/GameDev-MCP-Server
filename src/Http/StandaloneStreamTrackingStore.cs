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
        readonly ConcurrentDictionary<string, TrackedWriter> _liveWriters = new();

        public StandaloneStreamTrackingStore(ISseEventStreamStore inner) => _inner = inner;

        public bool HasLiveWriter(string sessionId) => _liveWriters.ContainsKey(sessionId);

        public async ValueTask<ISseEventStreamWriter> CreateStreamAsync(SseEventStreamOptions options, CancellationToken cancellationToken = default)
        {
            var writer = await _inner.CreateStreamAsync(options, cancellationToken).ConfigureAwait(false);
            if (options.StreamId != StreamableHttpServerTransport.UnsolicitedMessageStreamId)
                return writer;

            var tracked = new TrackedWriter(writer, _liveWriters, options.SessionId);
            _liveWriters[options.SessionId] = tracked;
            return tracked;
        }

        public ValueTask<ISseEventStreamReader?> GetStreamReaderAsync(string lastEventId, CancellationToken cancellationToken = default)
            => _inner.GetStreamReaderAsync(lastEventId, cancellationToken);

        sealed class TrackedWriter : ISseEventStreamWriter
        {
            readonly ISseEventStreamWriter _inner;
            readonly ConcurrentDictionary<string, TrackedWriter> _liveWriters;
            readonly string _sessionId;

            public TrackedWriter(ISseEventStreamWriter inner, ConcurrentDictionary<string, TrackedWriter> liveWriters, string sessionId)
            {
                _inner = inner;
                _liveWriters = liveWriters;
                _sessionId = sessionId;
            }

            public ValueTask SetModeAsync(SseEventStreamMode mode, CancellationToken cancellationToken = default)
                => _inner.SetModeAsync(mode, cancellationToken);

            public ValueTask<SseItem<JsonRpcMessage?>> WriteEventAsync(SseItem<JsonRpcMessage?> sseItem, CancellationToken cancellationToken = default)
                => _inner.WriteEventAsync(sseItem, cancellationToken);

            public ValueTask DisposeAsync()
            {
                // Removes this writer only: disposing a replaced session's writer must not drop its successor's entry.
                _liveWriters.TryRemove(new KeyValuePair<string, TrackedWriter>(_sessionId, this));
                return _inner.DisposeAsync();
            }
        }
    }
}
