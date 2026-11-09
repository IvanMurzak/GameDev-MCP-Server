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
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Http;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

#pragma warning disable MCPEXP002

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>A silent standalone stream must carry bytes, or clients and proxies take it for dead.</summary>
    public class SseKeepAliveTests
    {
        static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(1);

        static string Text(MemoryStream stream) => Encoding.UTF8.GetString(stream.ToArray());

        static async Task WriteAsync(Stream stream, string text) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text));

        static async Task SilentAsync() => await Task.Delay(20);

        [Fact]
        public async Task NoComment_BeforeTheStreamStarted()
        {
            var inner = new MemoryStream();
            using var stream = new SseKeepAliveMiddleware.KeepAliveStream(inner);
            await SilentAsync();

            await stream.WriteCommentIfSilentAsync(Silence, default);

            Assert.Empty(Text(inner));
        }

        [Fact]
        public async Task Comment_AfterTheStreamStartedWithAFlush()
        {
            // What the SDK does without an event store: headers flushed, no event yet.
            var inner = new MemoryStream();
            using var stream = new SseKeepAliveMiddleware.KeepAliveStream(inner);
            await stream.FlushAsync();
            await SilentAsync();

            await stream.WriteCommentIfSilentAsync(Silence, default);

            Assert.Equal(": keep-alive\n\n", Text(inner));
        }

        [Fact]
        public async Task Comment_BetweenEvents()
        {
            var inner = new MemoryStream();
            using var stream = new SseKeepAliveMiddleware.KeepAliveStream(inner);
            await WriteAsync(stream, "data: {}\n\n");
            await SilentAsync();

            await stream.WriteCommentIfSilentAsync(Silence, default);

            Assert.Equal("data: {}\n\n: keep-alive\n\n", Text(inner));
        }

        [Theory]
        [InlineData("data: {\"a\":")] // mid-line
        [InlineData("data: {}\n")] // the event's closing blank line is still to come
        public async Task NoComment_InsideAnEvent(string partial)
        {
            var inner = new MemoryStream();
            using var stream = new SseKeepAliveMiddleware.KeepAliveStream(inner);
            await WriteAsync(stream, partial);
            await SilentAsync();

            await stream.WriteCommentIfSilentAsync(Silence, default);

            Assert.Equal(partial, Text(inner));
        }

        [Fact]
        public async Task EventEndingAcrossTwoWrites_IsABoundary()
        {
            var inner = new MemoryStream();
            using var stream = new SseKeepAliveMiddleware.KeepAliveStream(inner);
            await WriteAsync(stream, "data: {}\n");
            await WriteAsync(stream, "\n");
            await SilentAsync();

            await stream.WriteCommentIfSilentAsync(Silence, default);

            Assert.EndsWith(": keep-alive\n\n", Text(inner));
        }

        [Fact]
        public async Task NoComment_WhenTheStreamWasJustActive()
        {
            var inner = new MemoryStream();
            using var stream = new SseKeepAliveMiddleware.KeepAliveStream(inner);
            await WriteAsync(stream, "data: {}\n\n");

            await stream.WriteCommentIfSilentAsync(TimeSpan.FromMinutes(1), default);

            Assert.Equal("data: {}\n\n", Text(inner));
        }

        [Theory]
        [InlineData(null, 30)]
        [InlineData("", 30)]
        [InlineData("abc", 30)]
        [InlineData("-5", 30)]
        [InlineData("45", 45)]
        [InlineData("0", 0)]
        public void Interval_IsConfigurable(string? raw, int seconds)
            => Assert.Equal(TimeSpan.FromSeconds(seconds), SseKeepAliveMiddleware.ResolveInterval(raw));

        /// <summary>With a real SDK server: comments flow, a real client still gets its notification, and errors stay intact.</summary>
        [Fact]
        public async Task RealServer_SilentStreamCarriesComments_ClientStillReceivesNotifications()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = timeout.Token;
            var port = ServerHarness.FreePort();
            var sessions = new ConcurrentDictionary<string, McpServer>();

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Services
                .AddMcpServer(o => o.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } })
                .WithHttpTransport(o =>
                {
                    o.SessionMode = HttpServerSessionMode.Stateful;
                    o.RunSessionHandler = (_, server, token) =>
                    {
                        sessions[server.SessionId!] = server;
                        return server.RunAsync(token);
                    };
                });
            await using var app = builder.Build();
            app.UseMiddleware<SseKeepAliveMiddleware>(TimeSpan.FromMilliseconds(100));
            app.MapMcp();
            await app.StartAsync(ct);

            var url = $"http://127.0.0.1:{port}/";
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(url), TransportMode = HttpTransportMode.StreamableHttp }),
                cancellationToken: ct);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var handler = client.RegisterNotificationHandler(
                NotificationMethods.ToolListChangedNotification,
                (_, _) => { received.TrySetResult(); return ValueTask.CompletedTask; });

            using var http = new HttpClient();

            // A second raw session, to look at the bytes on the wire.
            using var init = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw\",\"version\":\"1\"}}}", Encoding.UTF8, "application/json"),
            };
            init.Headers.Accept.ParseAdd("application/json");
            init.Headers.Accept.ParseAdd("text/event-stream");
            using var initResponse = await http.SendAsync(init, ct);
            var rawSession = string.Join("", initResponse.Headers.GetValues(McpSessionTokenMiddleware.SessionIdHeader));

            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Accept.ParseAdd("text/event-stream");
            get.Headers.Add(McpSessionTokenMiddleware.SessionIdHeader, rawSession);
            using var stream = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(ct));
            Assert.Equal(": keep-alive", await reader.ReadLineAsync(ct));

            // The client's own stream has carried comments too by now: its parser must not stumble over them.
            await Task.Delay(300, ct);
            await sessions[client.SessionId!].SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);
            await received.Task.WaitAsync(ct);

            // A refused GET keeps its status and its error body.
            using var unknown = new HttpRequestMessage(HttpMethod.Get, url);
            unknown.Headers.Accept.ParseAdd("text/event-stream");
            unknown.Headers.Add(McpSessionTokenMiddleware.SessionIdHeader, "no-such-session");
            using var refused = await http.SendAsync(unknown, ct);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Contains("Session not found", await refused.Content.ReadAsStringAsync(ct));

            await app.StopAsync(CancellationToken.None);
        }
    }
}
