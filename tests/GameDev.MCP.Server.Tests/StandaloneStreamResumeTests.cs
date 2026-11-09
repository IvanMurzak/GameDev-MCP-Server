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
using System.Net;
using System.Net.Http;
using System.Net.ServerSentEvents;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

#pragma warning disable MCP9006, MCPEXP002

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// A session rehydrated after a restart must get its standalone GET stream back when the client
    /// reconnects with a <c>Last-Event-ID</c>: without that, the SDK only replays the old stream and the
    /// new transport drops every notification.
    /// </summary>
    public class StandaloneStreamResumeTests
    {
        const string Session = "session-1";
        static readonly string Get = StreamableHttpServerTransport.UnsolicitedMessageStreamId;

        static string EventId(string session, string stream, int sequence = 1)
            => $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(session))}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(stream))}:{sequence}";

        static IDistributedCache NewCache()
            => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        static StandaloneStreamTrackingStore NewStore(IDistributedCache cache) => new(
            new DistributedCacheEventStreamStore(Options.Create(new DistributedCacheEventStreamStoreOptions { Cache = cache })));

        static SseEventStreamOptions StreamOptions(string streamId) => new()
        {
            SessionId = Session,
            StreamId = streamId,
            Mode = SseEventStreamMode.Streaming,
        };

        /// <summary>Runs the middleware; true when the request reached the next handler still carrying the header.</summary>
        static async Task<bool> KeepsLastEventId(StandaloneStreamTrackingStore store, string method, string? sessionHeader, string lastEventId)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Headers[ExpiredStreamResumeMiddleware.LastEventIdHeader] = lastEventId;
            if (sessionHeader != null)
                context.Request.Headers["Mcp-Session-Id"] = sessionHeader;

            var reachedNext = false;
            var middleware = new StaleStandaloneStreamResumeMiddleware(
                _ => { reachedNext = true; return Task.CompletedTask; },
                store,
                NullLogger<StaleStandaloneStreamResumeMiddleware>.Instance);
            await middleware.InvokeAsync(context);

            Assert.True(reachedNext);
            return context.Request.Headers.ContainsKey(ExpiredStreamResumeMiddleware.LastEventIdHeader);
        }

        [Fact]
        public async Task StandaloneStreamWithoutLiveWriter_IsAttachedFresh()
            => Assert.False(await KeepsLastEventId(NewStore(NewCache()), "GET", Session, EventId(Session, Get)));

        [Fact]
        public async Task StandaloneStreamWithLiveWriter_StillResumes()
        {
            var store = NewStore(NewCache());
            await using var writer = await store.CreateStreamAsync(StreamOptions(Get));

            Assert.True(await KeepsLastEventId(store, "GET", Session, EventId(Session, Get)));
        }

        [Fact]
        public async Task PostStream_StillResumes()
            => Assert.True(await KeepsLastEventId(NewStore(NewCache()), "GET", Session, EventId(Session, "request-7")));

        [Fact]
        public async Task OtherSessionsEventId_IsLeftForTheSdkToReject()
            => Assert.True(await KeepsLastEventId(NewStore(NewCache()), "GET", Session, EventId("other-session", Get)));

        [Theory]
        [InlineData("not-an-event-id")]
        [InlineData("!!!:!!!:1")]
        public async Task UnparsableEventId_IsLeftForTheSdkToReject(string eventId)
            => Assert.True(await KeepsLastEventId(NewStore(NewCache()), "GET", Session, eventId));

        [Fact]
        public async Task PostRequest_IsNotTouched()
            => Assert.True(await KeepsLastEventId(NewStore(NewCache()), "POST", Session, EventId(Session, Get)));

        [Fact]
        public async Task EventIdFormat_MatchesTheSdkStore()
        {
            // The format is internal to the SDK: this fails on an SDK bump that changes it.
            await using var writer = await NewStore(NewCache()).CreateStreamAsync(StreamOptions(Get));

            var item = await writer.WriteEventAsync(new SseItem<JsonRpcMessage?>(null));

            Assert.True(StaleStandaloneStreamResumeMiddleware.TryParseEventId(item.EventId!, out var session, out var stream));
            Assert.Equal(Session, session);
            Assert.Equal(Get, stream);
        }

        [Fact]
        public async Task LiveWriter_IsTrackedUntilDisposed_AndOnlyForTheStandaloneStream()
        {
            var store = NewStore(NewCache());

            await using (await store.CreateStreamAsync(StreamOptions("request-7")))
                Assert.False(store.HasLiveWriter(Session));

            var writer = await store.CreateStreamAsync(StreamOptions(Get));
            Assert.True(store.HasLiveWriter(Session));

            await writer.DisposeAsync();
            Assert.False(store.HasLiveWriter(Session));
        }

        [Fact]
        public async Task DisposingAReplacedWriter_KeepsItsSuccessorTracked()
        {
            var store = NewStore(NewCache());
            var replaced = await store.CreateStreamAsync(StreamOptions(Get));
            await using var successor = await store.CreateStreamAsync(StreamOptions(Get));

            await replaced.DisposeAsync();

            Assert.True(store.HasLiveWriter(Session));
        }

        /// <summary>The reported bug, end to end: real C# client, real SDK server, restart on the same port.</summary>
        [Fact]
        public async Task ClientOnRehydratedSession_ReceivesNotificationAfterRestart()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = timeout.Token;
            var cache = NewCache();
            var port = FreePort();

            var first = await RestartableServer.StartAsync(cache, port, ct);
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{port}/"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                }),
                cancellationToken: ct);

            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var handler = client.RegisterNotificationHandler(
                NotificationMethods.ToolListChangedNotification,
                (_, _) => { received.TrySetResult(); return ValueTask.CompletedTask; });

            // The client must hold an event id (the priming event) before the restart.
            await first.WaitForLiveWriterAsync(client.SessionId!, ct);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct);
            var rehydrated = await second.WaitForSessionAsync(client.SessionId!, ct);
            await second.WaitForLiveWriterAsync(client.SessionId!, ct);

            await rehydrated.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);

            await received.Task.WaitAsync(ct);
        }

        static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>One server process: the wiring of Program.cs for session persistence, over a cache that outlives it.</summary>
        sealed class RestartableServer : IAsyncDisposable
        {
            readonly WebApplication _app;
            readonly ConcurrentDictionary<string, McpServer> _sessions;

            RestartableServer(WebApplication app, ConcurrentDictionary<string, McpServer> sessions)
            {
                _app = app;
                _sessions = sessions;
            }

            public static async Task<RestartableServer> StartAsync(IDistributedCache cache, int port, CancellationToken ct)
            {
                var sessions = new ConcurrentDictionary<string, McpServer>();
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
                builder.Services.AddSingleton(cache);
                builder.Services.AddSingleton<ISessionMigrationHandler>(sp => new SessionMigrationHandler(
                    cache, NullLogger<SessionMigrationHandler>.Instance));
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
                    })
                    .WithDistributedCacheEventStreamStore(Program.ConfigureEventStreamStore);
                Program.AddStandaloneStreamTracking(builder.Services);

                var app = builder.Build();
                app.UseMiddleware<StaleStandaloneStreamResumeMiddleware>();
                app.MapMcp();
                await app.StartAsync(ct);
                return new RestartableServer(app, sessions);
            }

            public async Task<McpServer> WaitForSessionAsync(string sessionId, CancellationToken ct)
            {
                McpServer? server;
                while (!_sessions.TryGetValue(sessionId, out server))
                    await Task.Delay(50, ct);
                return server;
            }

            public async Task WaitForLiveWriterAsync(string sessionId, CancellationToken ct)
            {
                var streams = _app.Services.GetRequiredService<StandaloneStreamTrackingStore>();
                while (!streams.HasLiveWriter(sessionId))
                    await Task.Delay(50, ct);
            }

            public async ValueTask DisposeAsync()
            {
                await _app.StopAsync(CancellationToken.None);
                await _app.DisposeAsync();
            }
        }
    }
}
