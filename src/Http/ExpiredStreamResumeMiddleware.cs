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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace com.IvanMurzak.GameDev.MCP.Server.Http
{
    /// <summary>
    /// Answers an SSE resumption whose cached stream data has expired with a 400 instead of a 500.
    ///
    /// <para>With the distributed-cache event-stream store, a client that resumes a stream with a
    /// <c>Last-Event-ID</c> whose stream metadata (or a replayed event) has expired makes the MCP SDK
    /// (1.4.0) throw <see cref="McpException"/> "… was not found in the cache" from inside
    /// <c>HandleResumedStreamAsync</c>. Nothing catches it, so Kestrel answers 500 with an empty body.
    /// The SDK already answers 400 "Last-Event-ID is either invalid or expired" when the stream is
    /// unknown up front; this gives the expired-mid-lookup case the same, spec-shaped answer. It applies
    /// only before the first byte: once replayed events were flushed, the exception aborts the stream.</para>
    /// </summary>
    public sealed class ExpiredStreamResumeMiddleware
    {
        internal const string LastEventIdHeader = "Last-Event-ID";
        internal const string ExpiredCacheEntryMarker = "was not found in the cache";
        internal const string ExpiredLastEventIdMessage = "Bad Request: The specified Last-Event-ID is either invalid or expired.";

        readonly RequestDelegate _next;
        readonly ILogger<ExpiredStreamResumeMiddleware> _logger;

        public ExpiredStreamResumeMiddleware(RequestDelegate next, ILogger<ExpiredStreamResumeMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (McpException ex) when (context.Request.Headers.ContainsKey(LastEventIdHeader)
                && IsExpiredCacheEntry(ex) && !context.Response.HasStarted)
            {
                _logger.LogWarning("SSE resume for {Method} {Path} referenced expired stream data ({Reason}); answering 400.",
                    context.Request.Method, context.Request.Path, ex.Message);
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32000, message = ExpiredLastEventIdMessage },
                    id = (string?)null
                }, context.RequestAborted);
            }
        }

        internal static bool IsExpiredCacheEntry(McpException ex)
            => ex.Message.Contains(ExpiredCacheEntryMarker, StringComparison.Ordinal);
    }
}
