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
using System.Net.ServerSentEvents;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Http;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin.Server.Auth;
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

        static string EventId(string session, string stream)
            => $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(session))}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(stream))}:1";

        static IDistributedCache NewCache()
            => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        static StandaloneStreamTrackingStore NewStore() => new(
            new DistributedCacheEventStreamStore(Options.Create(new DistributedCacheEventStreamStoreOptions { Cache = NewCache() })));

        static SseEventStreamOptions StreamOptions(string streamId) => new()
        {
            SessionId = Session,
            StreamId = streamId,
            Mode = SseEventStreamMode.Streaming,
        };

        /// <summary>Runs the middleware; true when the request reached the next handler with the header unchanged.</summary>
        static async Task<bool> KeepsLastEventId(StandaloneStreamTrackingStore store, string method, string lastEventId)
            => await ResumedEventId(store, method, lastEventId) == lastEventId;

        /// <summary>Runs the middleware; the Last-Event-ID the next handler sees, null when it was removed.</summary>
        static async Task<string?> ResumedEventId(StandaloneStreamTrackingStore store, string method, string lastEventId)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Headers[ExpiredStreamResumeMiddleware.LastEventIdHeader] = lastEventId;
            context.Request.Headers[McpSessionTokenMiddleware.SessionIdHeader] = Session;

            var reachedNext = false;
            var middleware = new StaleStandaloneStreamResumeMiddleware(
                _ => { reachedNext = true; return Task.CompletedTask; },
                store,
                NullLogger<StaleStandaloneStreamResumeMiddleware>.Instance);
            await middleware.InvokeAsync(context);

            Assert.True(reachedNext);
            return context.Request.Headers.TryGetValue(ExpiredStreamResumeMiddleware.LastEventIdHeader, out var value)
                ? value.ToString()
                : null;
        }

        [Fact]
        public async Task StandaloneStreamWithoutLiveWriter_IsAttachedFresh()
            => Assert.False(await KeepsLastEventId(NewStore(), "GET", EventId(Session, Get)));

        [Fact]
        public async Task StandaloneStreamWithLiveWriter_StillResumes()
        {
            var store = NewStore();
            await using var writer = await store.CreateStreamAsync(StreamOptions(Get));

            Assert.True(await KeepsLastEventId(store, "GET", EventId(Session, Get)));
        }

        [Fact]
        public async Task IdAtOrBehindTheLiveWriter_StillResumes()
        {
            var store = NewStore();
            await using var writer = await store.CreateStreamAsync(StreamOptions(Get));
            var written = await writer.WriteEventAsync(new SseItem<JsonRpcMessage?>(null));

            Assert.True(await KeepsLastEventId(store, "GET", written.EventId!));
        }

        [Fact]
        public async Task IdFromAPreviousProcess_AheadOfTheNewWriter_MovesToItsLastEvent()
        {
            var store = NewStore();
            await using var writer = await store.CreateStreamAsync(StreamOptions(Get));
            var written = await writer.WriteEventAsync(new SseItem<JsonRpcMessage?>(null));

            Assert.Equal(written.EventId, await ResumedEventId(store, "GET", EventId(Session, Get) + "000"));
        }

        [Fact]
        public async Task NonNumericSequence_IsLeftForTheSdkToReject()
            => Assert.True(await KeepsLastEventId(NewStore(), "GET", EventId(Session, Get) + "x"));

        [Fact]
        public async Task PostStream_StillResumes()
            => Assert.True(await KeepsLastEventId(NewStore(), "GET", EventId(Session, "request-7")));

        [Fact]
        public async Task OtherSessionsEventId_IsLeftForTheSdkToReject()
            => Assert.True(await KeepsLastEventId(NewStore(), "GET", EventId("other-session", Get)));

        [Theory]
        [InlineData("not-an-event-id")]
        [InlineData("!!!:!!!:1")]
        public async Task UnparsableEventId_IsLeftForTheSdkToReject(string eventId)
            => Assert.True(await KeepsLastEventId(NewStore(), "GET", eventId));

        [Fact]
        public async Task PostRequest_IsNotTouched()
            => Assert.True(await KeepsLastEventId(NewStore(), "POST", EventId(Session, Get)));

        [Fact]
        public async Task EventIdFormat_MatchesTheSdkStore()
        {
            // The format is internal to the SDK: this fails on an SDK bump that changes it.
            await using var writer = await NewStore().CreateStreamAsync(StreamOptions(Get));

            var item = await writer.WriteEventAsync(new SseItem<JsonRpcMessage?>(null));

            Assert.True(StaleStandaloneStreamResumeMiddleware.TryParseEventId(item.EventId!, out var session, out var stream, out var sequence));
            Assert.Equal(Session, session);
            Assert.Equal(Get, stream);
            Assert.Equal(1, sequence);
        }

        [Fact]
        public async Task LiveWriter_IsTrackedUntilDisposed_AndOnlyForTheStandaloneStream()
        {
            var store = NewStore();

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
            var store = NewStore();
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
            var port = ServerHarness.FreePort();

            await using var first = await RestartableServer.StartAsync(cache, port, ct);
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

            await first.WaitForLiveWriterAsync(client.SessionId!, ct);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct);
            var rehydrated = await second.WaitForSessionAsync(client.SessionId!, ct);
            await second.WaitForLiveWriterAsync(client.SessionId!, ct);

            // Else this was a plain GET and the middleware under test did nothing.
            Assert.True(second.SawResume, "The client did not reconnect with a Last-Event-ID.");

            await rehydrated.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);

            await received.Task.WaitAsync(ct);
        }

        /// <summary>One server process: the wiring of Program.cs for session persistence, over a cache that outlives it.</summary>
        sealed class RestartableServer : IAsyncDisposable
        {
            readonly WebApplication _app;
            readonly ConcurrentDictionary<string, McpServer> _sessions;
            bool _disposed;

            RestartableServer(WebApplication app, ConcurrentDictionary<string, McpServer> sessions)
            {
                _app = app;
                _sessions = sessions;
            }

            public bool SawResume { get; private set; }

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
                var server = new RestartableServer(app, sessions);
                app.Use((context, next) =>
                {
                    if (context.Request.Headers.ContainsKey(ExpiredStreamResumeMiddleware.LastEventIdHeader))
                        server.SawResume = true;
                    return next(context);
                });
                app.UseMiddleware<StaleStandaloneStreamResumeMiddleware>();
                app.MapMcp();
                await app.StartAsync(ct);
                return server;
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
                if (_disposed)
                    return;
                _disposed = true;
                await _app.StopAsync(CancellationToken.None);
                await _app.DisposeAsync();
            }
        }
    }

    [Collection(LiveServerCollection.Name)]
    public class StandaloneStreamWiringTests
    {
        [Fact]
        public async Task RealHostWithRedis_WritesThroughTheTrackingStore()
        {
            // Nothing listens on this port: the Redis client connects lazily and no request reaches it here.
            await using var server = await ServerHarness.StartAsync("--REDIS_URL=127.0.0.1:1");

            Assert.IsType<StandaloneStreamTrackingStore>(server.Services.GetRequiredService<ISseEventStreamStore>());
        }
    }
}
