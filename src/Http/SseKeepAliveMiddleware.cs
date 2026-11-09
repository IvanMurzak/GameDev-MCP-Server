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
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Server.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.GameDev.MCP.Server.Http
{
    /// <summary>
    /// Sends an SSE comment on a silent standalone GET stream. Between catalogue changes that stream carries no
    /// bytes for hours, the MCP SDK sends no keep-alive of its own, and clients and proxies read that as a dead
    /// connection: the default fetch of the TS SDK gives up after 300 s without a body byte, and without an
    /// event store its reconnect then gets a 400 for good, so the client stops hearing <c>list_changed</c>.
    ///
    /// <para>A comment is not an event: SSE parsers skip it, and it adds nothing to the event store.</para>
    ///
    /// <para>The timer never touches the <see cref="HttpResponse"/>, which is not thread-safe: whether this is an
    /// event stream is decided on the SDK's own thread, when the SDK first writes or flushes. So a comment can
    /// neither race the SDK setting its headers nor commit a response the SDK might still refuse with an error.
    /// A resume with nothing to replay has written nothing yet, and stays without comments until its first event.</para>
    /// </summary>
    public sealed class SseKeepAliveMiddleware
    {
        /// <summary>Seconds of silence before a comment is sent; 0 turns it off.</summary>
        public const string IntervalEnvVar = "MCP_SSE_KEEPALIVE_SECONDS";

        /// <summary>Well under the shortest idle limit known here (300 s, TS SDK) and common proxy defaults (60 s).</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

        readonly RequestDelegate _next;
        readonly TimeSpan _interval;
        readonly ILogger<SseKeepAliveMiddleware> _logger;

        public SseKeepAliveMiddleware(RequestDelegate next, ILogger<SseKeepAliveMiddleware> logger, TimeSpan interval)
        {
            _next = next;
            _logger = logger;
            _interval = interval;
        }

        /// <summary>The configured interval, at most a day; a malformed or negative value falls back to the default.</summary>
        public static TimeSpan ResolveInterval(string? raw)
            => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(Math.Min(seconds, 86400))
                : DefaultInterval;

        public async Task InvokeAsync(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method)
                || !context.Request.Headers.ContainsKey(McpSessionTokenMiddleware.SessionIdHeader))
            {
                await _next(context);
                return;
            }

            var original = context.Response.Body;
            var response = context.Response;
            var body = new KeepAliveStream(original, () => response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
            using var stop = new CancellationTokenSource();
            context.Response.Body = body;
            var pump = PumpAsync(body, stop.Token);
            try
            {
                await _next(context);
            }
            finally
            {
                context.Response.Body = original;
                stop.Cancel();
                await pump;
            }
        }

        async Task PumpAsync(KeepAliveStream body, CancellationToken cancellationToken)
        {
            try
            {
                using var timer = new PeriodicTimer(_interval);
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await body.WriteCommentAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // The request ended.
            }
            catch (IOException)
            {
                // The client is gone: the SDK's own request-aborted path ends the stream.
            }
            catch (Exception ex)
            {
                // Awaited in a finally: anything escaping would replace the request's real outcome.
                _logger.LogWarning(ex, "SSE keep-alive stopped for this stream.");
            }
        }

        /// <summary>The response body with comments added: only between events, never inside one.</summary>
        internal sealed class KeepAliveStream : Stream
        {
            static readonly byte[] Comment = ": keep-alive\n\n"u8.ToArray();

            readonly Stream _inner;
            readonly Func<bool> _isEventStream;
            readonly SemaphoreSlim _gate = new(1, 1);
            bool _eventStreamStarted;
            bool _betweenEvents = true;

            public KeepAliveStream(Stream inner, Func<bool> isEventStream)
            {
                _inner = inner;
                _isEventStream = isEventStream;
            }

            public async Task WriteCommentAsync(CancellationToken cancellationToken)
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (!_eventStreamStarted || !_betweenEvents)
                        return;
                    await _inner.WriteAsync(Comment, cancellationToken);
                    await _inner.FlushAsync(cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    _eventStreamStarted = _eventStreamStarted || _isEventStream(); // on the SDK's thread, see the class comment
                    await _inner.WriteAsync(buffer, cancellationToken);
                    if (!buffer.IsEmpty)
                        _betweenEvents = buffer.Span.EndsWith("\n\n"u8); // an event split across writes only skips a comment
                }
                finally
                {
                    _gate.Release();
                }
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async Task FlushAsync(CancellationToken cancellationToken)
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    _eventStreamStarted = _eventStreamStarted || _isEventStream();
                    await _inner.FlushAsync(cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }
            }

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The SSE response is written asynchronously.");
            public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
