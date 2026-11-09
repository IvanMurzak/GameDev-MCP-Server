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

namespace com.IvanMurzak.GameDev.MCP.Server.Http
{
    /// <summary>
    /// Sends an SSE comment on a silent standalone GET stream. Between catalogue changes that stream carries no
    /// bytes for hours, the MCP SDK sends no keep-alive of its own, and clients and proxies read that as a dead
    /// connection: the default fetch of the TS SDK gives up after 300 s without a body byte, and without an
    /// event store its reconnect then gets a 400 for good, so the client stops hearing <c>list_changed</c>.
    ///
    /// <para>A comment is not an event: SSE parsers skip it, and it adds nothing to the event store.</para>
    /// </summary>
    public sealed class SseKeepAliveMiddleware
    {
        /// <summary>Seconds of silence before a comment is sent; 0 turns it off.</summary>
        public const string IntervalEnvVar = "MCP_SSE_KEEPALIVE_SECONDS";

        /// <summary>Well under the shortest idle limit known here (300 s, TS SDK) and common proxy defaults (60 s).</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

        readonly RequestDelegate _next;
        readonly TimeSpan _interval;

        public SseKeepAliveMiddleware(RequestDelegate next, TimeSpan interval)
        {
            _next = next;
            _interval = interval;
        }

        /// <summary>The configured interval; a malformed or negative value falls back to the default.</summary>
        public static TimeSpan ResolveInterval(string? raw)
            => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : DefaultInterval;

        public async Task InvokeAsync(HttpContext context)
        {
            if (_interval <= TimeSpan.Zero
                || !HttpMethods.IsGet(context.Request.Method)
                || !context.Request.Headers.ContainsKey(McpSessionTokenMiddleware.SessionIdHeader))
            {
                await _next(context);
                return;
            }

            var original = context.Response.Body;
            using var body = new KeepAliveStream(original);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            context.Response.Body = body;
            var pump = PumpAsync(context.Response, body, stop.Token);
            try
            {
                await _next(context);
            }
            finally
            {
                stop.Cancel();
                await pump;
                context.Response.Body = original;
            }
        }

        async Task PumpAsync(HttpResponse response, KeepAliveStream body, CancellationToken cancellationToken)
        {
            try
            {
                using var timer = new PeriodicTimer(_interval);
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    if (response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                        await body.WriteCommentIfSilentAsync(_interval, cancellationToken);
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
        }

        /// <summary>The response body with comments added: only between events, never inside one, and never as a stream's first bytes.</summary>
        internal sealed class KeepAliveStream : Stream
        {
            static readonly byte[] Comment = ": keep-alive\n\n"u8.ToArray();

            readonly Stream _inner;
            readonly SemaphoreSlim _gate = new(1, 1);
            bool _started;
            byte _last = (byte)'\n', _beforeLast = (byte)'\n'; // nothing written yet is a boundary too
            long _lastActivity = Environment.TickCount64;

            public KeepAliveStream(Stream inner) => _inner = inner;

            public async Task WriteCommentIfSilentAsync(TimeSpan silence, CancellationToken cancellationToken)
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (!_started || _last != '\n' || _beforeLast != '\n' || Environment.TickCount64 - _lastActivity < silence.TotalMilliseconds)
                        return;
                    await _inner.WriteAsync(Comment, cancellationToken);
                    await _inner.FlushAsync(cancellationToken);
                    _lastActivity = Environment.TickCount64;
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
                    await _inner.WriteAsync(buffer, cancellationToken);
                    Note(buffer.Span);
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
                    await _inner.FlushAsync(cancellationToken);
                    _started = true;
                }
                finally
                {
                    _gate.Release();
                }
            }

            void Note(ReadOnlySpan<byte> written)
            {
                if (written.IsEmpty)
                    return;
                _beforeLast = written.Length > 1 ? written[^2] : _last;
                _last = written[^1];
                _started = true;
                _lastActivity = Environment.TickCount64;
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

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _gate.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
