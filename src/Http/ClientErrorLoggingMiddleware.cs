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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.GameDev.MCP.Server.Http
{
    /// <summary>
    /// Logs every 4xx answer (except 401) on the MCP endpoint with the start of its body.
    ///
    /// <para>The MCP SDK writes its rejections (unsupported protocol version, missing session id,
    /// multiple GET streams, …) straight to the response and logs nothing, so a burst of 400s in the
    /// proxy log is otherwise undiagnosable. The body is the SDK's own error text and carries no
    /// secret; it is capped at <see cref="MaxCapturedBytes"/>. Only the shape of the request is
    /// logged next to it (which MCP headers were present), never header values or the request body.</para>
    ///
    /// <para>401 is skipped (see <see cref="IsLoggedStatus"/>), and so is the SignalR hub (<c>/hub/…</c>):
    /// plugin traffic there is not MCP.</para>
    /// </summary>
    public sealed class ClientErrorLoggingMiddleware
    {
        internal const int MaxCapturedBytes = 512;

        readonly RequestDelegate _next;
        readonly ILogger<ClientErrorLoggingMiddleware> _logger;

        public ClientErrorLoggingMiddleware(RequestDelegate next, ILogger<ClientErrorLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            var originalBody = context.Response.Body;
            var capture = new ClientErrorCaptureStream(originalBody, context.Response);
            context.Response.Body = capture;
            try
            {
                await _next(context);
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            var status = context.Response.StatusCode;
            if (!IsLoggedStatus(status))
                return;

            var headers = context.Request.Headers;
            _logger.LogWarning(
                "MCP {Method} {Path} answered {Status}: {Body} (session-id: {HasSession}, protocol-version: {HasProtocol}, last-event-id: {HasLastEventId}, auth: {HasAuth})",
                context.Request.Method,
                context.Request.Path,
                status,
                capture.CapturedText,
                headers.ContainsKey("Mcp-Session-Id"),
                headers.ContainsKey("MCP-Protocol-Version"),
                headers.ContainsKey("Last-Event-ID"),
                headers.ContainsKey("Authorization"));
        }

        /// <summary>
        /// 4xx except 401: the auth handler already logs every rejected token, and a client looping on an
        /// expired token would otherwise flood the log with identical lines.
        /// </summary>
        internal static bool IsLoggedStatus(int status)
            => status >= 400 && status < 500 && status != StatusCodes.Status401Unauthorized;

        /// <summary>Pass-through stream that keeps the first bytes written once the status is a 4xx.</summary>
        sealed class ClientErrorCaptureStream : Stream
        {
            readonly Stream _inner;
            readonly HttpResponse _response;
            readonly MemoryStream _captured = new();

            public ClientErrorCaptureStream(Stream inner, HttpResponse response)
            {
                _inner = inner;
                _response = response;
            }

            public string CapturedText => _captured.Length == 0
                ? "<empty>"
                : Encoding.UTF8.GetString(_captured.GetBuffer(), 0, (int)_captured.Length);

            void Capture(ReadOnlySpan<byte> data)
            {
                if (!IsLoggedStatus(_response.StatusCode))
                    return;
                var room = MaxCapturedBytes - (int)_captured.Length;
                if (room > 0)
                    _captured.Write(data.Length > room ? data[..room] : data);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Capture(buffer.AsSpan(offset, count));
                _inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Capture(buffer);
                _inner.Write(buffer);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Capture(buffer.AsSpan(offset, count));
                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Capture(buffer.Span);
                return _inner.WriteAsync(buffer, cancellationToken);
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

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
