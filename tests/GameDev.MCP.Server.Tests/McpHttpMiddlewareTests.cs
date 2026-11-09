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
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    public class McpHttpMiddlewareTests
    {
        const string ExpiredMetadata =
            "Stream metadata for session 'abc' and stream '__get__' was not found in the cache. The metadata may have expired.";

        static DefaultHttpContext NewContext(string method, string path, bool lastEventId = false)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();
            if (lastEventId)
                context.Request.Headers["Last-Event-ID"] = "evt-1";
            return context;
        }

        static string ReadBody(HttpContext context)
        {
            var body = (MemoryStream)context.Response.Body;
            return Encoding.UTF8.GetString(body.ToArray());
        }

        // ── ExpiredStreamResumeMiddleware ─────────────────────────────────────

        [Fact]
        public async Task ExpiredResume_Answers400WithJsonRpcError()
        {
            var context = NewContext("GET", "/", lastEventId: true);
            var middleware = new ExpiredStreamResumeMiddleware(
                _ =>
                {
                    context.Response.ContentType = "text/event-stream";
                    throw new McpException(ExpiredMetadata);
                },
                NullLogger<ExpiredStreamResumeMiddleware>.Instance);

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
            Assert.StartsWith("application/json", context.Response.ContentType);
            using var json = JsonDocument.Parse(ReadBody(context));
            Assert.Equal(ExpiredStreamResumeMiddleware.ExpiredLastEventIdMessage,
                json.RootElement.GetProperty("error").GetProperty("message").GetString());
        }

        [Fact]
        public async Task ExpiredReplayedEvent_AlsoAnswers400()
        {
            var context = NewContext("GET", "/", lastEventId: true);
            var middleware = new ExpiredStreamResumeMiddleware(
                _ => throw new McpException("SSE event with ID 'x' was not found in the cache. The event may have expired."),
                NullLogger<ExpiredStreamResumeMiddleware>.Instance);

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        }

        [Fact]
        public async Task OtherMcpException_Propagates()
        {
            var context = NewContext("GET", "/", lastEventId: true);
            var middleware = new ExpiredStreamResumeMiddleware(
                _ => throw new McpException("something else"),
                NullLogger<ExpiredStreamResumeMiddleware>.Instance);

            await Assert.ThrowsAsync<McpException>(() => middleware.InvokeAsync(context));
        }

        [Fact]
        public async Task RequestWithoutLastEventId_IsNotIntercepted()
        {
            var context = NewContext("GET", "/");
            var middleware = new ExpiredStreamResumeMiddleware(
                _ => throw new McpException(ExpiredMetadata),
                NullLogger<ExpiredStreamResumeMiddleware>.Instance);

            await Assert.ThrowsAsync<McpException>(() => middleware.InvokeAsync(context));
        }

        // ── ClientErrorLoggingMiddleware ──────────────────────────────────────

        static async Task<List<string>> RunLogging(HttpContext context, RequestDelegate next)
        {
            var logger = new ListLogger<ClientErrorLoggingMiddleware>();
            await new ClientErrorLoggingMiddleware(next, logger).InvokeAsync(context);
            return logger.Messages;
        }

        static RequestDelegate Respond(int status, string body) => async ctx =>
        {
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsync(body);
        };

        [Fact]
        public async Task BadRequest_IsLoggedWithBodyAndPassedThrough()
        {
            var context = NewContext("POST", "/");
            context.Request.Headers["Mcp-Session-Id"] = "s-1";
            const string body = "{\"error\":{\"message\":\"Bad Request: unsupported protocol version\"}}";

            var messages = await RunLogging(context, Respond(400, body));

            Assert.Equal(body, ReadBody(context));
            var line = Assert.Single(messages);
            Assert.Contains("answered 400", line);
            Assert.Contains("unsupported protocol version", line);
            Assert.Contains("session-id: True", line);
            Assert.DoesNotContain("s-1", line); // header values are never logged
        }

        [Fact]
        public async Task CapturedBody_IsCapped()
        {
            var context = NewContext("POST", "/");
            var body = new string('x', ClientErrorLoggingMiddleware.MaxCapturedBytes * 3);

            var messages = await RunLogging(context, Respond(404, body));

            Assert.Equal(body, ReadBody(context)); // the client still gets the whole body
            var line = Assert.Single(messages);
            Assert.Contains(new string('x', ClientErrorLoggingMiddleware.MaxCapturedBytes), line);
            Assert.DoesNotContain(new string('x', ClientErrorLoggingMiddleware.MaxCapturedBytes + 1), line);
        }

        [Theory]
        [InlineData(200, "/")]
        [InlineData(401, "/")]
        [InlineData(500, "/")]
        [InlineData(400, "/hub/mcp-server")]
        [InlineData(400, "/api/tools/script-execute")]
        [InlineData(400, "/p/afb0e853/api/tools/script-execute")]
        public async Task OutOfScopeResponses_AreNotLogged(int status, string path)
        {
            var context = NewContext("POST", path);

            var messages = await RunLogging(context, Respond(status, "body"));

            Assert.Empty(messages);
            Assert.Equal("body", ReadBody(context));
        }

        [Fact]
        public async Task ResponseBody_IsRestoredAfterTheRequest()
        {
            var context = NewContext("POST", "/");
            var original = context.Response.Body;

            await RunLogging(context, Respond(200, "ok"));

            Assert.Same(original, context.Response.Body);
        }

        // ── Event-stream store options ────────────────────────────────────────

        [Fact]
        public void EventStreamStore_DropsOnlyTheMetadataAbsoluteCap()
        {
            #pragma warning disable MCP9006 // Tests the options of the stateful event store the server runs on by design (SEP-2567 back-compat API).
            var options = new DistributedCacheEventStreamStoreOptions();
            var defaults = new DistributedCacheEventStreamStoreOptions();
            #pragma warning restore MCP9006

            Program.ConfigureEventStreamStore(options);

            Assert.Null(options.MetadataAbsoluteExpiration);
            Assert.NotNull(options.MetadataSlidingExpiration);
            Assert.Equal(defaults.MetadataSlidingExpiration, options.MetadataSlidingExpiration);
            Assert.Equal(defaults.EventSlidingExpiration, options.EventSlidingExpiration);
            Assert.Equal(defaults.EventAbsoluteExpiration, options.EventAbsoluteExpiration);
        }

        sealed class ListLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));
        }
    }
}
