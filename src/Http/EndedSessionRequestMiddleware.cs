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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.GameDev.MCP.Server.Http
{
    /// <summary>
    /// Answers an MCP POST whose session ended while the request was in flight with a JSON-RPC error,
    /// instead of letting the SDK's cancellation escape to Kestrel as an unhandled exception.
    ///
    /// <para>When a session is disposed (a client <c>DELETE</c>, replace-by-identity), the MCP SDK cancels
    /// every POST still waiting for that session's response and the <see cref="OperationCanceledException"/>
    /// leaves <c>StreamableHttpPostTransport.HandlePostAsync</c> unhandled (SDK 1.4.0 and 2.4.0 alike).
    /// Kestrel then logs a <c>fail</c>, Sentry records an event, and the client gets either a 500 or, as the
    /// SDK usually flushed the SSE headers already, a 200 stream that ends with no response for its
    /// request.</para>
    ///
    /// <para>This catches only that case: a cancellation that reached the SDK's transport (the response is
    /// already an event stream) of a POST whose client is still connected, outside shutdown. The client gets
    /// the SDK's own "Session not found" (<c>-32001</c>), the code that tells an MCP client to start a new
    /// session: as a 404 when nothing was sent yet, otherwise as an SSE event carrying the request's id.
    /// Every other cancellation and exception propagates untouched, so a real fault still reaches the logs
    /// and Sentry; on shutdown the default path stays, since a persisted session survives the restart and
    /// must not be abandoned by its client.</para>
    ///
    /// <para>Known, accepted window: if the session ends while the SDK is writing this very request's
    /// response, the client can receive that response and then this error for the same id.</para>
    /// </summary>
    public sealed class EndedSessionRequestMiddleware
    {
        /// <summary>The SDK's code and text for a session it does not hold.</summary>
        internal const int SessionNotFoundCode = -32001;
        internal const string SessionNotFoundMessage = "Session not found: the session ended while this request was in flight.";

        /// <summary>How much of the request body is kept to find its id afterwards; clients write <c>id</c> first or early.</summary>
        internal const int IdPrefixBytes = 8 * 1024;

        const string McpSessionIdHeader = "Mcp-Session-Id";

        readonly RequestDelegate _next;
        readonly ILogger<EndedSessionRequestMiddleware> _logger;
        readonly IHostApplicationLifetime? _lifetime;

        public EndedSessionRequestMiddleware(RequestDelegate next, ILogger<EndedSessionRequestMiddleware> logger,
            IHostApplicationLifetime? lifetime = null)
        {
            _next = next;
            _logger = logger;
            _lifetime = lifetime;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Only a request of an existing session can outlive it, and only on the MCP endpoints (not the plugin
            // hub, not the REST tool API).
            if (!HttpMethods.IsPost(context.Request.Method)
                || string.IsNullOrEmpty(context.Request.Headers[McpSessionIdHeader])
                || !ClientErrorLoggingMiddleware.IsLoggedPath(context.Request.Path))
            {
                await _next(context);
                return;
            }

            // Keeps the first bytes the SDK reads, so the request id can be found afterwards without buffering the body.
            var body = new PrefixRecordingStream(context.Request.Body, IdPrefixBytes);
            context.Request.Body = body;
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (IsEndedSession(context))
            {
                _logger.LogInformation(
                    "MCP POST {Path} cancelled because its session ended while the request was in flight; answered {Code}.",
                    context.Request.Path, SessionNotFoundCode);
                try
                {
                    await AnswerAsync(context, ReadRequestId(body.Prefix));
                }
                catch (Exception ex) when ((ex is OperationCanceledException || ex is IOException) && context.RequestAborted.IsCancellationRequested)
                {
                    // The client left while the answer was being written: nobody is there to receive it.
                }
            }
            finally
            {
                context.Request.Body = body.Inner;
            }
        }

        /// <summary>
        /// A cancellation the SDK's transport raised for a session that ended: the SDK set the event-stream
        /// content type just before handing the POST to the transport, the client is still there, and the
        /// host is not stopping.
        /// </summary>
        bool IsEndedSession(HttpContext context)
            => !context.RequestAborted.IsCancellationRequested
               && _lifetime?.ApplicationStopping.IsCancellationRequested != true
               && context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

        static async Task AnswerAsync(HttpContext context, JsonNode? requestId)
        {
            var error = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
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
            await context.Response.WriteAsync($"event: message\ndata: {error}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        /// <summary>The top-level <c>id</c> of a JSON-RPC message from the start of its body, or null.</summary>
        internal static JsonNode? ReadRequestId(ReadOnlySpan<byte> prefix)
        {
            try
            {
                var reader = new Utf8JsonReader(prefix, isFinalBlock: false, state: default);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    return null;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var isId = reader.ValueTextEquals("id");
                    if (!reader.Read())
                        return null;
                    if (isId)
                        return reader.TokenType switch
                        {
                            JsonTokenType.Number => reader.TryGetInt64(out var n) ? JsonValue.Create(n) : JsonValue.Create(reader.GetDouble()),
                            JsonTokenType.String => JsonValue.Create(reader.GetString()),
                            _ => null,
                        };
                    if (!reader.TrySkip())
                        return null; // the id lies beyond the recorded prefix
                }
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>A read-only pass-through that records the first bytes read from it.</summary>
        sealed class PrefixRecordingStream : Stream
        {
            readonly byte[] _prefix;
            int _recorded;

            public PrefixRecordingStream(Stream inner, int capacity)
            {
                Inner = inner;
                _prefix = new byte[capacity];
            }

            public Stream Inner { get; }
            public ReadOnlySpan<byte> Prefix => _prefix.AsSpan(0, _recorded);

            void Record(ReadOnlySpan<byte> read)
            {
                var take = Math.Min(read.Length, _prefix.Length - _recorded);
                if (take <= 0)
                    return;
                read.Slice(0, take).CopyTo(_prefix.AsSpan(_recorded));
                _recorded += take;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await Inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                Record(buffer.Span.Slice(0, read));
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = Inner.Read(buffer, offset, count);
                Record(buffer.AsSpan(offset, read));
                return read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
