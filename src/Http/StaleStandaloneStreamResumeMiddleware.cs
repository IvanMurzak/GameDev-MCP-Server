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
using System.Text;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Server.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
    /// repeats that GET every second for the life of the session.</para>
    ///
    /// <para>So when the resumed stream is this session's standalone stream and no writer for it exists
    /// in this process, the header is removed and the SDK serves an ordinary GET, which attaches the
    /// stream. Events stored but undelivered by the previous process are not replayed. Resumes of POST
    /// streams (which carry a pending response) and of a stream this process still owns are untouched.</para>
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
            if (HttpMethods.IsGet(request.Method)
                && request.Headers.TryGetValue(ExpiredStreamResumeMiddleware.LastEventIdHeader, out var lastEventId)
                && request.Headers[McpSessionTokenMiddleware.SessionIdHeader].ToString() is var sessionId
                && !_streams.HasLiveWriter(sessionId)
                && TryParseEventId(lastEventId.ToString(), out var eventSessionId, out var eventStreamId)
                && eventStreamId == StreamableHttpServerTransport.UnsolicitedMessageStreamId
                && eventSessionId == sessionId)
            {
                _logger.LogInformation(
                    "Attaching a new standalone GET stream for a resume from a previous process. SessionId={SessionId}",
                    sessionId);
                request.Headers.Remove(ExpiredStreamResumeMiddleware.LastEventIdHeader);
            }

            return _next(context);
        }

        /// <summary>
        /// Reads an event id of the SDK's distributed-cache store, <c>base64(session):base64(stream):sequence</c>.
        /// The format is internal to the SDK (a test pins it against the real store). Parsing, rather than
        /// asking the store, also covers a stream whose metadata has expired: that is what a client idle
        /// for over an hour resumes after a restart, and the store would only answer 400 for it.
        /// </summary>
        internal static bool TryParseEventId(string eventId, out string sessionId, out string streamId)
        {
            sessionId = streamId = string.Empty;
            var parts = eventId.Split(':');
            if (parts.Length != 3)
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
