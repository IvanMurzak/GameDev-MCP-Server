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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Http;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin.Server.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

#pragma warning disable MCP9006, MCPEXP002

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// A catalogue change while a session is not in memory reaches no one, so the first standalone GET
    /// stream of a session rehydrated in this process must say the catalogue may have changed: real SDK
    /// server, restarted over a shared cache, with a raw client and with the C# client.
    /// </summary>
    public class RehydratedSessionListChangedTests
    {
        const string Tools = NotificationMethods.ToolListChangedNotification;
        const string Prompts = NotificationMethods.PromptListChangedNotification;
        const string Resources = NotificationMethods.ResourceListChangedNotification;
        const string Sentinel = "notifications/test/sentinel";
        const string Protocol = "2025-11-25";

        /// <summary>Long enough for a wrongly sent notification to get ahead of the sentinel that ends a count.</summary>
        static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

        static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(60));

        static IDistributedCache NewCache() => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        /// <summary>The catalogue of one process; the tool collection declares tools/list_changed by itself.</summary>
        static Action<McpServerOptions> Catalogue(string toolName) => options =>
        {
            options.ToolCollection = [McpServerTool.Create(() => "ok", new McpServerToolCreateOptions { Name = toolName })];
            options.Capabilities = new ServerCapabilities
            {
                Prompts = new PromptsCapability { ListChanged = true },
                Resources = new ResourcesCapability { ListChanged = true },
            };
        };

        /// <summary>Reads to the sentinel, sent once the expected announcements came and a wrong extra one had time to.</summary>
        static async Task<Dictionary<string, int>> CountUntilSentinelAsync(
            RawStream stream, McpServer server, int expected, CancellationToken ct)
        {
            var counts = new Dictionary<string, int>();
            var read = 0;
            if (expected == 0)
                await SettleThenSendSentinelAsync(server, ct);
            while (true)
            {
                var method = await stream.ReadMethodAsync(ct);
                if (method == Sentinel)
                    return counts;
                counts[method] = counts.GetValueOrDefault(method) + 1;
                if (++read == expected)
                    await SettleThenSendSentinelAsync(server, ct);
            }
        }

        static async Task SettleThenSendSentinelAsync(McpServer server, CancellationToken ct)
        {
            await Task.Delay(Settle, ct);
            await server.SendNotificationAsync(Sentinel, ct);
        }

        /// <summary>
        /// A resume has no response until the sentinel's replay starts it, so a wrongly sent notification
        /// would come after that sentinel: the count has to run on to a second one.
        /// </summary>
        static async Task AssertSilentAfterFirstSentinelAsync(RawStream resumed, McpServer server, CancellationToken ct)
        {
            Assert.Equal(Sentinel, await resumed.ReadMethodAsync(ct));
            Assert.Empty(await CountUntilSentinelAsync(resumed, server, expected: 0, ct));
        }

        static void AssertOneOfEach(Dictionary<string, int> counts)
        {
            Assert.Equal(1, counts.GetValueOrDefault(Tools));
            Assert.Equal(1, counts.GetValueOrDefault(Prompts));
            Assert.Equal(1, counts.GetValueOrDefault(Resources));
            Assert.Equal(3, counts.Count);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task FirstGetOfRehydratedSession_AnnouncesEachCapabilityOnce_AndTheClientSeesTheNewCatalogue(bool withLastEventId)
        {
            using var timeout = Timeout();
            var ct = timeout.Token;
            var cache = NewCache();
            var port = ServerHarness.FreePort();

            await using var first = await RestartableServer.StartAsync(cache, port, ct, Catalogue("old-tool"));
            using var client = new RawClient(port);
            await client.InitializeAsync(ct);
            string lastEventId;
            await using (var stream = await client.OpenStreamAsync(null, ct))
                lastEventId = await stream.ReadPrimingIdAsync(ct);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct, Catalogue("new-tool"));
            await using var reattached = await client.OpenStreamAsync(withLastEventId ? lastEventId : null, ct);
            await reattached.ReadPrimingIdAsync(ct);
            var server = await second.WaitForSessionAsync(client.SessionId, ct);

            AssertOneOfEach(await CountUntilSentinelAsync(reattached, server, expected: 3, ct));

            var tools = await client.ListToolNamesAsync(ct);
            Assert.Contains("new-tool", tools);
            Assert.DoesNotContain("old-tool", tools);
        }

        [Fact]
        public async Task SecondGet_AnnouncesNothingNew()
        {
            using var timeout = Timeout();
            var ct = timeout.Token;
            var cache = NewCache();
            var port = ServerHarness.FreePort();

            await using var first = await RestartableServer.StartAsync(cache, port, ct, Catalogue("old-tool"));
            using var client = new RawClient(port);
            await client.InitializeAsync(ct);
            await using (var stream = await client.OpenStreamAsync(null, ct))
                await stream.ReadPrimingIdAsync(ct);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct, Catalogue("new-tool"));
            await using var firstGet = await client.OpenStreamAsync(null, ct);
            await firstGet.ReadPrimingIdAsync(ct);
            var server = await second.WaitForSessionAsync(client.SessionId, ct);
            AssertOneOfEach(await CountUntilSentinelAsync(firstGet, server, expected: 3, ct));
            var lastId = firstGet.LastEventId;
            await firstGet.DisposeAsync();

            // The resume has nothing to replay yet, so it has no response headers before the sentinel.
            var opening = client.OpenStreamAsync(lastId, ct);
            await SettleThenSendSentinelAsync(server, ct);
            await using var resumed = await opening;

            await AssertSilentAfterFirstSentinelAsync(resumed, server, ct);
        }

        [Fact]
        public async Task SessionRehydratedByAPost_IsAnnouncedOnItsFirstGet()
        {
            using var timeout = Timeout();
            var ct = timeout.Token;
            var cache = NewCache();
            var port = ServerHarness.FreePort();

            await using var first = await RestartableServer.StartAsync(cache, port, ct, Catalogue("old-tool"));
            using var client = new RawClient(port);
            await client.InitializeAsync(ct);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct, Catalogue("new-tool"));
            Assert.Contains("new-tool", await client.ListToolNamesAsync(ct));

            await using var stream = await client.OpenStreamAsync(null, ct);
            await stream.ReadPrimingIdAsync(ct);
            var server = await second.WaitForSessionAsync(client.SessionId, ct);

            AssertOneOfEach(await CountUntilSentinelAsync(stream, server, expected: 3, ct));
        }

        [Fact]
        public async Task GetResumingAPostStream_DoesNotUseUpTheAnnouncement()
        {
            using var timeout = Timeout();
            var ct = timeout.Token;
            var cache = NewCache();
            var port = ServerHarness.FreePort();

            await using var first = await RestartableServer.StartAsync(cache, port, ct, Catalogue("old-tool"));
            using var client = new RawClient(port);
            await client.InitializeAsync(ct);
            var postEventId = await client.PostAndReadFirstEventIdAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\"}", ct);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct, Catalogue("new-tool"));

            // Rehydrates the session on a transport that has no standalone stream, which would drop the announcement.
            await using (var replayed = await client.OpenStreamAsync(postEventId, ct))
                await replayed.ReadToEndAsync(ct);

            await using var stream = await client.OpenStreamAsync(null, ct);
            await stream.ReadPrimingIdAsync(ct);
            var server = await second.WaitForSessionAsync(client.SessionId, ct);

            AssertOneOfEach(await CountUntilSentinelAsync(stream, server, expected: 3, ct));
        }

        [Fact]
        public async Task NewSession_FirstGet_AnnouncesNothing()
        {
            using var timeout = Timeout();
            var ct = timeout.Token;
            var port = ServerHarness.FreePort();

            await using var server = await RestartableServer.StartAsync(NewCache(), port, ct, Catalogue("tool"));
            using var client = new RawClient(port);
            await client.InitializeAsync(ct);
            await using var stream = await client.OpenStreamAsync(null, ct);
            await stream.ReadPrimingIdAsync(ct);

            Assert.Empty(await CountUntilSentinelAsync(stream, await server.WaitForSessionAsync(client.SessionId, ct), expected: 0, ct));
        }

        [Fact]
        public async Task SameProcessResume_AnnouncesNothing()
        {
            using var timeout = Timeout();
            var ct = timeout.Token;
            var port = ServerHarness.FreePort();

            await using var host = await RestartableServer.StartAsync(NewCache(), port, ct, Catalogue("tool"));
            using var client = new RawClient(port);
            await client.InitializeAsync(ct);
            string lastEventId;
            await using (var stream = await client.OpenStreamAsync(null, ct))
                lastEventId = await stream.ReadPrimingIdAsync(ct);
            var server = await host.WaitForSessionAsync(client.SessionId, ct);

            var opening = client.OpenStreamAsync(lastEventId, ct);
            await SettleThenSendSentinelAsync(server, ct);
            await using var resumed = await opening;

            await AssertSilentAfterFirstSentinelAsync(resumed, server, ct);
        }

        [Fact]
        public async Task CapabilityWithoutListChanged_IsNotAnnounced()
        {
            static void ToolsOnly(McpServerOptions options) => options.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
                Prompts = new PromptsCapability(),
            };

            using var timeout = Timeout();
            var ct = timeout.Token;
            var cache = NewCache();
            var port = ServerHarness.FreePort();

            await using var first = await RestartableServer.StartAsync(cache, port, ct, ToolsOnly);
            using var client = new RawClient(port);
            await client.InitializeAsync(ct);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct, ToolsOnly);
            await using var stream = await client.OpenStreamAsync(null, ct);
            await stream.ReadPrimingIdAsync(ct);
            var server = await second.WaitForSessionAsync(client.SessionId, ct);

            var counts = await CountUntilSentinelAsync(stream, server, expected: 1, ct);
            Assert.Equal(1, counts[Tools]);
            Assert.Single(counts);
        }

        [Fact]
        public async Task FailureToSend_LeavesTheStreamAndTheOtherAnnouncements()
        {
            static void FailingToolsAnnouncement(McpServerOptions options)
            {
                Catalogue("tool")(options);
                options.Filters.Message.OutgoingFilters.Add(next => (context, token) =>
                    context.JsonRpcMessage is JsonRpcNotification { Method: Tools }
                        ? throw new InvalidOperationException("planted send failure")
                        : next(context, token));
            }

            using var timeout = Timeout();
            var ct = timeout.Token;
            var cache = NewCache();
            var port = ServerHarness.FreePort();

            await using var first = await RestartableServer.StartAsync(cache, port, ct, Catalogue("tool"));
            using var client = new RawClient(port);
            await client.InitializeAsync(ct);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct, FailingToolsAnnouncement);
            await using var stream = await client.OpenStreamAsync(null, ct);
            await stream.ReadPrimingIdAsync(ct);
            var server = await second.WaitForSessionAsync(client.SessionId, ct);

            var counts = await CountUntilSentinelAsync(stream, server, expected: 2, ct);
            Assert.False(counts.ContainsKey(Tools));
            Assert.Equal(1, counts[Prompts]);
            Assert.Equal(1, counts[Resources]);
        }

        /// <summary>The C# client attached across the restart: it reattaches by itself and has to re-list.</summary>
        [Fact]
        public async Task CSharpClient_AttachedAcrossRestart_IsToldOnce_AndListsTheNewCatalogue()
        {
            using var timeout = Timeout();
            var ct = timeout.Token;
            var cache = NewCache();
            var port = ServerHarness.FreePort();

            await using var first = await RestartableServer.StartAsync(cache, port, ct, Catalogue("old-tool"));
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{port}/"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                }),
                cancellationToken: ct);

            var counts = new ConcurrentDictionary<string, int>();
            var toldOfTools = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask Count(JsonRpcNotification notification, CancellationToken _)
            {
                counts.AddOrUpdate(notification.Method, 1, (_, count) => count + 1);
                if (notification.Method == Tools)
                    toldOfTools.TrySetResult();
                if (notification.Method == Resources)
                    ended.TrySetResult();
                return ValueTask.CompletedTask;
            }
            await using var tools = client.RegisterNotificationHandler(Tools, Count);
            await using var prompts = client.RegisterNotificationHandler(Prompts, Count);
            await using var resources = client.RegisterNotificationHandler(Resources, Count);
            var sawSentinel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var sentinel = client.RegisterNotificationHandler(Sentinel, (_, _) => { sawSentinel.TrySetResult(); return ValueTask.CompletedTask; });

            await first.WaitForLiveWriterAsync(client.SessionId!, ct);
            Assert.Empty(counts);
            await first.DisposeAsync();

            await using var second = await RestartableServer.StartAsync(cache, port, ct, Catalogue("new-tool"));

            // Nothing on the server side sends these: the plugin and the collections did not change in this process.
            await Task.WhenAll(toldOfTools.Task, ended.Task).WaitAsync(ct);
            var server = await second.WaitForSessionAsync(client.SessionId!, ct);
            await SettleThenSendSentinelAsync(server, ct);
            await sawSentinel.Task.WaitAsync(ct);

            Assert.Equal(1, counts[Tools]);
            Assert.Equal(1, counts[Prompts]);
            Assert.Equal(1, counts[Resources]);

            var listed = await client.ListToolsAsync(cancellationToken: ct);
            Assert.Contains(listed, tool => tool.Name == "new-tool");
            Assert.DoesNotContain(listed, tool => tool.Name == "old-tool");
        }

        [Fact]
        public async Task OwnerRefusedCaller_MarksNothing_AndTheOwnerMarksOnce()
        {
            const string id = "session-1";
            static HttpContext As(string subject)
            {
                var context = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", subject, ClaimValueTypes.String, "as") }, "test")),
                };
                context.Response.Headers[McpSessionTokenMiddleware.SessionIdHeader] = id;
                return context;
            }

            var store = new StandaloneStreamTrackingStore(new DistributedCacheEventStreamStore(
                Options.Create(new DistributedCacheEventStreamStoreOptions { Cache = NewCache() })));
            var handler = new SessionMigrationHandler(
                NewCache(), NullLogger<SessionMigrationHandler>.Instance, SessionMigrationHandler.DefaultSessionTtl, store);
            var init = new InitializeRequestParams { ProtocolVersion = Protocol, Capabilities = new ClientCapabilities(), ClientInfo = new Implementation { Name = "c", Version = "1" } };
            await handler.OnSessionInitializedAsync(As("alice"), id, init, CancellationToken.None);

            // Persisting a new session marks nothing either.
            Assert.False(store.TryTakeRehydrated(id));

            Assert.Null(await handler.AllowSessionMigrationAsync(As("mallory"), id, CancellationToken.None));
            Assert.False(store.TryTakeRehydrated(id));

            Assert.NotNull(await handler.AllowSessionMigrationAsync(As("alice"), id, CancellationToken.None));
            Assert.True(store.TryTakeRehydrated(id));
            Assert.False(store.TryTakeRehydrated(id));
        }

        [Fact]
        public void Marks_AreBounded_AndTheOldestGoesFirst()
        {
            var store = new StandaloneStreamTrackingStore(new DistributedCacheEventStreamStore(
                Options.Create(new DistributedCacheEventStreamStoreOptions { Cache = NewCache() })));

            for (var i = 0; i <= StandaloneStreamTrackingStore.MaxPendingRehydrations; i++)
                store.MarkRehydrated("session-" + i);

            Assert.False(store.TryTakeRehydrated("session-0"));
            Assert.True(store.TryTakeRehydrated("session-1"));
            Assert.True(store.TryTakeRehydrated("session-" + StandaloneStreamTrackingStore.MaxPendingRehydrations));
        }

        /// <summary>A plain HTTP client, so the test decides exactly what each GET carries.</summary>
        sealed class RawClient : IDisposable
        {
            readonly HttpClient _http;

            public RawClient(int port) => _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = System.Threading.Timeout.InfiniteTimeSpan };

            public string SessionId { get; private set; } = string.Empty;

            public async Task InitializeAsync(CancellationToken ct)
            {
                using var response = await PostAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + Protocol + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw\",\"version\":\"1\"}}}",
                    ct);
                response.EnsureSuccessStatusCode();
                SessionId = response.Headers.GetValues(McpSessionTokenMiddleware.SessionIdHeader).Single();
                await response.Content.ReadAsStringAsync(ct);

                using var initialized = await PostAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", ct);
                initialized.EnsureSuccessStatusCode();
            }

            public async Task<List<string>> ListToolNamesAsync(CancellationToken ct)
            {
                using var response = await PostAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", ct);
                response.EnsureSuccessStatusCode();
                var data = (await response.Content.ReadAsStringAsync(ct))
                    .Split('\n')
                    .Select(static line => line.Trim())
                    .Last(static line => line.StartsWith("data: {", StringComparison.Ordinal));
                using var json = JsonDocument.Parse(data["data: ".Length..]);
                return json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                    .Select(static tool => tool.GetProperty("name").GetString()!)
                    .ToList();
            }

            public async Task<string> PostAndReadFirstEventIdAsync(string body, CancellationToken ct)
            {
                using var response = await PostAsync(body, ct);
                response.EnsureSuccessStatusCode();
                var id = (await response.Content.ReadAsStringAsync(ct))
                    .Split('\n')
                    .Select(static line => line.Trim())
                    .First(static line => line.StartsWith("id: ", StringComparison.Ordinal));
                return id["id: ".Length..];
            }

            public async Task<RawStream> OpenStreamAsync(string? lastEventId, CancellationToken ct)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/");
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                request.Headers.TryAddWithoutValidation(McpSessionTokenMiddleware.SessionIdHeader, SessionId);
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", Protocol);
                if (lastEventId != null)
                    request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                return new RawStream(response, await response.Content.ReadAsStreamAsync(ct));
            }

            Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                if (SessionId.Length > 0)
                {
                    request.Headers.TryAddWithoutValidation(McpSessionTokenMiddleware.SessionIdHeader, SessionId);
                    request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", Protocol);
                }
                return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }

            public void Dispose() => _http.Dispose();
        }

        sealed class RawStream : IAsyncDisposable
        {
            readonly HttpResponseMessage _response;
            readonly StreamReader _reader;

            public RawStream(HttpResponseMessage response, Stream body)
            {
                _response = response;
                _reader = new StreamReader(body);
            }

            public string? LastEventId { get; private set; }

            /// <summary>The next event as its id and data, null at the end of the response.</summary>
            async Task<(string? Id, string Data)?> ReadEventAsync(CancellationToken ct)
            {
                string? id = null;
                var data = new StringBuilder();
                var any = false;
                while (await _reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (any)
                        {
                            if (id != null)
                                LastEventId = id;
                            return (id, data.ToString());
                        }
                        continue;
                    }
                    any = true;
                    if (line.StartsWith("id: ", StringComparison.Ordinal))
                        id = line["id: ".Length..];
                    else if (line.StartsWith("data: ", StringComparison.Ordinal))
                        data.Append(line["data: ".Length..]);
                }
                return null;
            }

            /// <summary>The first event of a new stream, which carries no message, only an id.</summary>
            public async Task<string> ReadPrimingIdAsync(CancellationToken ct)
            {
                var item = await ReadEventAsync(ct) ?? throw new InvalidOperationException("The stream ended before its priming event.");
                return item.Id ?? throw new InvalidOperationException("The priming event has no id.");
            }

            /// <summary>The method of the next notification.</summary>
            public async Task<string> ReadMethodAsync(CancellationToken ct)
            {
                while (true)
                {
                    var item = await ReadEventAsync(ct) ?? throw new InvalidOperationException("The stream ended before the next notification.");
                    if (string.IsNullOrWhiteSpace(item.Data))
                        continue;
                    using var json = JsonDocument.Parse(item.Data);
                    return json.RootElement.GetProperty("method").GetString()!;
                }
            }

            public async Task ReadToEndAsync(CancellationToken ct)
            {
                while (await ReadEventAsync(ct) is not null)
                {
                }
            }

            public ValueTask DisposeAsync()
            {
                _reader.Dispose();
                _response.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
