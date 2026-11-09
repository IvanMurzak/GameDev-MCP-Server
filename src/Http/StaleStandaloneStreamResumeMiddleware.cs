/*
┌───────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                      │
│  Repository: GitHub (https://github.com/IvanMurzak/GameDev-MCP-Server)    │
│  Copyright (c) 2026 Ivan Murzak                                           │
│  Licensed under the Apache License, Version 2.0.                          │
│  See the LICENSE file in the project root for more information.           │
└───────────────────────────────────────────────────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Server.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace com.IvanMurzak.GameDev.MCP.Server.Http
{
    /// <summary>
    /// Keeps server-to-client notifications flowing to a session rehydrated after a process restart.
    ///
    /// <para>A client that was on the standalone GET stream reconnects with its <c>Last-Event-ID</c>.
    /// The MCP SDK (2.4.0) answers such a GET only by replaying the stored stream: it never starts the GET
    /// on the rehydrated session's new transport, which therefore drops every notification ("no GET SSE
    /// stream has been opened"). Worse, the client is told nothing: the old stream replays nothing and
    /// ends with a 200, or stays open and silent if the previous process was killed, and the C# client
    /// repeats that GET for the life of the session.</para>
    ///
    /// <para>So when the resumed stream is this session's standalone stream and no writer for it exists
    /// in this process, the header is removed and the SDK serves an ordinary GET, which attaches the
    /// stream. Events stored but undelivered by the previous process are not replayed. Resumes of POST
    /// streams (which carry a pending response) and of a stream this process still owns are untouched.</para>
    ///
    /// <para>A client can also miss the new stream's first event and retry with its old id. A new writer
    /// restarts its sequence, so the SDK would wait silently for a sequence far ahead; such a resume is
    /// moved to the writer's first event instead, so it still gets everything the new stream sent.</para>
    ///
    /// <para>A rehydrated session had no subscriber while it was not in memory, so a catalogue change in
    /// that time is lost for good. So the first standalone stream of a session rehydrated in this process
    /// gets one <c>list_changed</c> per capability that declares it, and the client lists again.</para>
    ///
    /// <para>Liveness is per process, so this assumes one server process per Redis key prefix.</para>
    /// </summary>
    public sealed class StaleStandaloneStreamResumeMiddleware
    {
        readonly RequestDelegate _next;
        readonly StandaloneStreamTrackingStore _streams;
        readonly ILogger<StaleStandaloneStreamResumeMiddleware> _logger;

        public StaleStandaloneStreamResumeMiddleware(
            RequestDelegate next,
            StandaloneStreamTrackingStore streams,
            ILogger<StaleStandaloneStreamResumeMiddleware> logger)
        {
            _next = next;
            _streams = streams;
            _logger = logger;
        }

        public Task InvokeAsync(HttpContext context)
        {
            var request = context.Request;
            if (!HttpMethods.IsGet(request.Method))
                return _next(context);

            var sessionId = request.Headers[McpSessionTokenMiddleware.SessionIdHeader].ToString();
            if (sessionId.Length > 0)
                context.Response.OnStarting(() => AnnounceIfRehydrated(context, sessionId));

            if (request.Headers.TryGetValue(ExpiredStreamResumeMiddleware.LastEventIdHeader, out var lastEventId)
                && TryParseEventId(lastEventId.ToString(), out var eventSessionId, out var eventStreamId, out var sequence)
                && eventStreamId == StreamableHttpServerTransport.UnsolicitedMessageStreamId
                && eventSessionId == sessionId)
            {
                if (!_streams.HasLiveWriter(sessionId))
                {
                    // Debug: this runs ahead of auth and the session lookup, so nothing is attached yet.
                    _logger.LogDebug("Resume of a standalone GET stream from a previous process. SessionId={SessionId}", sessionId);
                    request.Headers.Remove(ExpiredStreamResumeMiddleware.LastEventIdHeader);
                }
                else if (_streams.LastEventId(sessionId) is { } writerEventId
                    && TryParseEventId(writerEventId, out _, out _, out var writerSequence)
                    && sequence > writerSequence)
                {
                    request.Headers[ExpiredStreamResumeMiddleware.LastEventIdHeader] = _streams.FirstEventId(sessionId);
                }
            }

            return _next(context);
        }

        /// <summary>
        /// Runs when the SDK starts the response, which is after it migrated the session (inside the SDK, so
        /// after this middleware ran). For a standalone stream that is the priming event's write: the SDK
        /// holds the transport's send lock there, and every send takes that lock, so the sends must not be
        /// awaited on this call stack. Task.Run lets them wait for the lock, and by then the transport has
        /// the stream they are written to.
        ///
        /// <para>The mark stays for any other response. No <see cref="McpServer"/> feature: the SDK refused
        /// the request. No live writer: the GET resumed a POST stream, and the transport would drop the
        /// notifications because it has no standalone stream yet.</para>
        /// </summary>
        Task AnnounceIfRehydrated(HttpContext context, string sessionId)
        {
            if (context.Features.Get<McpServer>() is { } server
                && _streams.HasLiveWriter(sessionId)
                && _streams.TryTakeRehydrated(sessionId))
            {
                _ = Task.Run(() => SendListChangedAsync(server, sessionId));
            }
            return Task.CompletedTask;
        }

        async Task SendListChangedAsync(McpServer server, string sessionId)
        {
            foreach (var method in DeclaredListChanged(server.ServerOptions))
            {
                try
                {
                    // Not the request's token: if the client drops now, the writer still stores the event for its resume.
                    await server.SendNotificationAsync(method, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Only a hint is lost: the stream is not ours to break, and the next capability may still get through.
                    _logger.LogWarning(ex, "Could not announce {Method} to a rehydrated session. SessionId={SessionId}", method, sessionId);
                }
            }
        }

        /// <summary>
        /// The list_changed notifications the server declares. The SDK's effective capabilities are not
        /// public, so this mirrors how it derives them: the option, or a collection, which forces the flag on.
        /// </summary>
        static IEnumerable<string> DeclaredListChanged(McpServerOptions options)
        {
            if (options.Capabilities?.Tools?.ListChanged == true || options.ToolCollection is not null)
                yield return NotificationMethods.ToolListChangedNotification;
            if (options.Capabilities?.Prompts?.ListChanged == true || options.PromptCollection is not null)
                yield return NotificationMethods.PromptListChangedNotification;
            if (options.Capabilities?.Resources?.ListChanged == true || options.ResourceCollection is not null)
                yield return NotificationMethods.ResourceListChangedNotification;
        }

        /// <summary>
        /// Reads an event id of the SDK's distributed-cache store, <c>base64(session):base64(stream):sequence</c>.
        /// The format is internal to the SDK (a test pins it against the real store). Parsing, rather than
        /// asking the store, also covers a stream whose metadata has expired: that is what a client idle
        /// for over an hour resumes after a restart, and the store would only answer 400 for it.
        /// </summary>
        internal static bool TryParseEventId(string eventId, out string sessionId, out string streamId, out long sequence)
        {
            sessionId = streamId = string.Empty;
            sequence = 0;
            var parts = eventId.Split(':');
            if (parts.Length != 3 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
                return false;
            try
            {
                sessionId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                streamId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
