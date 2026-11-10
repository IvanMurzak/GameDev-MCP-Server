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
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.GameDev.MCP.Server.Http
{
    /// <summary>
    /// Answers an MCP POST whose session ended while the request was in flight with a JSON-RPC error,
    /// instead of letting the SDK's cancellation escape to Kestrel as an unhandled exception.
    ///
    /// <para>When a session is disposed (a client <c>DELETE</c>, replace-by-identity, the idle timeout,
    /// shutdown), the MCP SDK cancels every POST still waiting for that session's response and the
    /// <see cref="OperationCanceledException"/> leaves <c>StreamableHttpPostTransport.HandlePostAsync</c>
    /// unhandled (SDK 1.4.0 and 2.4.0 alike). Kestrel then logs a <c>fail</c>, Sentry records an event, and
    /// the client gets either a 500 or, as the SDK usually flushed the SSE headers already, a 200 stream
    /// that ends with no response for its request.</para>
    ///
    /// <para>This catches only that case: a cancellation of a POST whose client is still connected. The
    /// client gets the SDK's own "Session not found" (<c>-32001</c>), the code that tells an MCP client to
    /// start a new session: as a 404 when nothing was sent yet, otherwise as an SSE event carrying the
    /// request's id. A client that went away keeps the default path, and every other exception propagates
    /// untouched.</para>
    /// </summary>
    public sealed class EndedSessionRequestMiddleware
    {
        /// <summary>The SDK's code and text for a session it does not hold.</summary>
        internal const int SessionNotFoundCode = -32001;
        internal const string SessionNotFoundMessage = "Session not found: the session ended while this request was in flight.";

        /// <summary>Larger bodies are not parsed for their id after the fact (the answer then carries <c>id: null</c>).</summary>
        internal const long MaxIdLookupBytes = 1024 * 1024;

        readonly RequestDelegate _next;
        readonly ILogger<EndedSessionRequestMiddleware> _logger;

        public EndedSessionRequestMiddleware(RequestDelegate next, ILogger<EndedSessionRequestMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // MCP endpoints only: not the plugin hub, not the REST tool API.
            if (!HttpMethods.IsPost(context.Request.Method) || !ClientErrorLoggingMiddleware.IsLoggedPath(context.Request.Path))
            {
                await _next(context);
                return;
            }

            // Lets the request id be read back after the SDK consumed the body; small bodies stay in memory.
            context.Request.EnableBuffering();
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "MCP POST {Path} cancelled because its session ended while the request was in flight; answered {Code}.",
                    context.Request.Path, SessionNotFoundCode);
                await AnswerAsync(context);
            }
        }

        static async Task AnswerAsync(HttpContext context)
        {
            var error = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = await ReadRequestIdAsync(context.Request),
                ["error"] = new JsonObject { ["code"] = SessionNotFoundCode, ["message"] = SessionNotFoundMessage },
            }.ToJsonString();

            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(error, context.RequestAborted);
                return;
            }

            // The SSE headers are out: the error can only travel as the stream's event for this request.
            if (context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                await context.Response.WriteAsync($"event: message\ndata: {error}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }

        static async Task<JsonNode?> ReadRequestIdAsync(HttpRequest request)
        {
            try
            {
                if (!request.Body.CanSeek || request.Body.Length > MaxIdLookupBytes)
                    return null;
                request.Body.Position = 0;
                var node = await JsonNode.ParseAsync(request.Body);
                return node is JsonObject message && message.TryGetPropertyValue("id", out var id) ? id?.DeepClone() : null;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                return null;
            }
        }
    }
}
