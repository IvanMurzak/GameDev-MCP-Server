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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Http;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    [Collection(LiveServerCollection.Name)]
    public class EndedSessionRequestTests
    {
        const string ToolsCall = "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":{\"name\":\"x\",\"arguments\":{}}}";

        sealed class ResponseFeature : HttpResponseFeature
        {
            public bool Started;
            public override bool HasStarted => Started;
        }

        static (DefaultHttpContext Context, ResponseFeature Response) NewPost(string path = "/", string body = ToolsCall)
        {
            var context = new DefaultHttpContext();
            var response = new ResponseFeature { Body = new MemoryStream() };
            context.Features.Set<IHttpResponseFeature>(response);
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(response.Body));
            context.Request.Method = "POST";
            context.Request.Path = path;
            context.Request.Headers["Mcp-Session-Id"] = "s-1";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return (context, response);
        }

        static EndedSessionRequestMiddleware Middleware(RequestDelegate next)
            => new EndedSessionRequestMiddleware(next, NullLogger<EndedSessionRequestMiddleware>.Instance);

        /// <summary>The SDK's shape: read the whole body, then wait for a response the disposed session never sends.</summary>
        static RequestDelegate ConsumeBodyThenCancel(Action<HttpContext>? beforeCancel = null) => async ctx =>
        {
            await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            beforeCancel?.Invoke(ctx);
            throw new TaskCanceledException("A task was canceled.");
        };

        static string Body(HttpContext context)
            => Encoding.UTF8.GetString(((MemoryStream)context.Features.Get<IHttpResponseFeature>()!.Body).ToArray());

        [Fact]
        public async Task CancelledBeforeAnyResponse_Answers404WithTheRequestId()
        {
            var (context, _) = NewPost();

            await Middleware(ConsumeBodyThenCancel()).InvokeAsync(context);

            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
            AssertSessionNotFound(JsonNode.Parse(Body(context)), 42);
        }

        [Fact]
        public async Task CancelledAfterTheSseHeaders_EndsTheStreamWithAnErrorEvent()
        {
            var (context, response) = NewPost();

            await Middleware(ConsumeBodyThenCancel(ctx =>
            {
                ctx.Response.ContentType = "text/event-stream";
                response.Started = true;
            })).InvokeAsync(context);

            var body = Body(context);
            Assert.StartsWith("event: message\ndata: ", body);
            AssertSessionNotFound(JsonNode.Parse(body.Split("data: ")[1].Trim()), 42);
        }

        [Fact]
        public async Task ClientGone_KeepsTheDefaultPath()
        {
            var (context, _) = NewPost();
            using var aborted = new CancellationTokenSource();
            aborted.Cancel();
            context.RequestAborted = aborted.Token;

            await Assert.ThrowsAsync<TaskCanceledException>(() => Middleware(ConsumeBodyThenCancel()).InvokeAsync(context));
        }

        [Fact]
        public async Task RealException_Propagates()
        {
            var (context, _) = NewPost();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Middleware(_ => throw new InvalidOperationException("boom")).InvokeAsync(context));
        }

        [Theory]
        [InlineData("/api/tools/script-execute")]
        [InlineData("/hub/mcp-server")]
        public async Task NonMcpPaths_AreNotIntercepted(string path)
        {
            var (context, _) = NewPost(path);

            await Assert.ThrowsAsync<TaskCanceledException>(() => Middleware(ConsumeBodyThenCancel()).InvokeAsync(context));
        }

        /// <summary>
        /// The real host: a request waits for a plugin that is not there, and its session is deleted meanwhile.
        /// The SDK cancels the POST; the client must get a JSON-RPC error for its own id, not a dropped stream.
        /// </summary>
        [Fact]
        public async Task SessionDeletedWhileARequestIsInFlight_AnswersTheRequest()
        {
            await using var server = await ServerHarness.StartAsync();
            var http = server.Http;

            using var init = await Post(http, null,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}");
            init.EnsureSuccessStatusCode();
            var sessionId = init.Headers.GetValues("Mcp-Session-Id").Single();
            await init.Content.ReadAsStringAsync();
            using (await Post(http, sessionId, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")) { }

            // No plugin is connected, so the call waits (--plugin-reconnect-wait-seconds=60) until the session ends.
            var inFlight = Post(http, sessionId, ToolsCall);
            await Task.Delay(1000);
            using (var delete = new HttpRequestMessage(HttpMethod.Delete, "/"))
            {
                delete.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
                delete.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
                using var deleted = await http.SendAsync(delete);
                Assert.True(deleted.IsSuccessStatusCode, $"DELETE answered {(int)deleted.StatusCode}");
            }

            using var response = await inFlight.WaitAsync(TimeSpan.FromSeconds(30));
            var text = await response.Content.ReadAsStringAsync();
            var json = text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => JsonNode.Parse(l.Substring(5).Trim())).LastOrDefault(n => n?["id"] != null)
                ?? JsonNode.Parse(text);
            AssertSessionNotFound(json, 42);
        }

        static void AssertSessionNotFound(JsonNode? message, int id)
        {
            Assert.Equal(id, message!["id"]!.GetValue<int>());
            Assert.Equal(EndedSessionRequestMiddleware.SessionNotFoundCode, message["error"]!["code"]!.GetValue<int>());
        }

        [Theory]
        [InlineData("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}", "7")]
        [InlineData("{\"method\":\"tools/call\",\"params\":{\"id\":1,\"x\":[1,2]},\"id\":\"abc\"}", "\"abc\"")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", null)]
        [InlineData("{\"params\":{\"big\":\"", null)] // the id lies beyond the recorded prefix
        public void RequestId_IsReadFromTheTopLevelOnly(string prefix, string? expected)
            => Assert.Equal(expected, EndedSessionRequestMiddleware.ReadRequestId(Encoding.UTF8.GetBytes(prefix))?.ToJsonString());

        static Task<HttpResponseMessage> Post(HttpClient http, string? sessionId, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
            if (sessionId != null)
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
    }
}
