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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Http;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin.Server.Auth;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
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

        /// <summary>The catalogue of one process, with every capability declaring list_changed.</summary>
        static Action<McpServerOptions> Catalogue(string toolName) => options =>
        {
            options.ToolCollection = [McpServerTool.Create(() => "ok", new McpServerToolCreateOptions { Name = toolName })];
            options.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
                Prompts = new PromptsCapability { ListChanged = true },
                Resources = new ResourcesCapability { ListChanged = true },
            };
        };

        /// <summary>Reads to the sentinel, sent once the expected announcements came and a wrong extra one had time to.</summary>
        static async Task<Dictionary<string, int>> CountUntilSentinelAsync(
            RawStream stream, McpServer server, int expected, CancellationToken ct)
        {
            var counts = new Dictionary<string, int>();
            for (var read = 0; ; read++)
            {
                if (read == expected)
                    await SettleThenSendSentinelAsync(server, ct);
                var method = await stream.ReadMethodAsync();
                if (method == Sentinel)
                    return counts;
                counts[method] = counts.GetValueOrDefault(method) + 1;
            }
        }

        static async Task SettleThenSendSentinelAsync(McpServer server, CancellationToken ct)
        {
            await Task.Delay(Settle, ct);
            await server.SendNotificationAsync(Sentinel, ct);
        }

        static void AssertOneOfEach(IReadOnlyDictionary<string, int> counts)
        {
            Assert.Equal(1, counts.GetValueOrDefault(Tools));
            Assert.Equal(1, counts.GetValueOrDefault(Prompts));
            Assert.Equal(1, counts.GetValueOrDefault(Resources));
            Assert.Equal(3, counts.Count);
        }

        /// <summary>
        /// A resume has no response until the sentinel's replay starts it, so a wrongly sent notification
        /// would come after that sentinel: the count has to run on to a second one.
        /// </summary>
        static async Task AssertResumeIsSilentAsync(Scenario scenario, McpServer server, string? lastEventId)
        {
            var opening = scenario.Client.OpenStreamAsync(lastEventId, scenario.Ct);
            await SettleThenSendSentinelAsync(server, scenario.Ct);
            await using var resumed = await opening;

            Assert.Equal(Sentinel, await resumed.ReadMethodAsync());
            Assert.Empty(await CountUntilSentinelAsync(resumed, server, expected: 0, scenario.Ct));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task FirstGetOfRehydratedSession_AnnouncesEachCapabilityOnce_AndTheClientSeesTheNewCatalogue(bool withLastEventId)
        {
            await using var scenario = await Scenario.StartAsync(Catalogue("old-tool"));
            string lastEventId;
            await using (var stream = await scenario.Client.OpenStreamAsync(null, scenario.Ct))
                lastEventId = await stream.ReadPrimingIdAsync();

            await scenario.RestartAsync(Catalogue("new-tool"));
            var (reattached, server) = await scenario.AttachAsync(withLastEventId ? lastEventId : null);
            await using var _ = reattached;

            AssertOneOfEach(await CountUntilSentinelAsync(reattached, server, expected: 3, scenario.Ct));

            var tools = await scenario.Client.ListToolNamesAsync(scenario.Ct);
            Assert.Contains("new-tool", tools);
            Assert.DoesNotContain("old-tool", tools);
        }

        [Fact]
        public async Task ResumeAfterTheAnnouncement_AnnouncesNothingNew()
        {
            await using var scenario = await Scenario.StartAsync(Catalogue("old-tool"));

            await scenario.RestartAsync(Catalogue("new-tool"));
            var (firstGet, server) = await scenario.AttachAsync(null);
            AssertOneOfEach(await CountUntilSentinelAsync(firstGet, server, expected: 3, scenario.Ct));
            var lastId = firstGet.LastEventId;
            await firstGet.DisposeAsync();

            await AssertResumeIsSilentAsync(scenario, server, lastId);
        }

        [Fact]
        public async Task RefusedSecondGet_AnnouncesNothingNew()
        {
            await using var scenario = await Scenario.StartAsync(Catalogue("old-tool"));

            await scenario.RestartAsync(Catalogue("new-tool"));
            var (firstGet, server) = await scenario.AttachAsync(null);
            await using var _ = firstGet;
            AssertOneOfEach(await CountUntilSentinelAsync(firstGet, server, expected: 3, scenario.Ct));

            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => scenario.Client.OpenStreamAsync(null, scenario.Ct));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            Assert.Empty(await CountUntilSentinelAsync(firstGet, server, expected: 0, scenario.Ct));
        }

        [Fact]
        public async Task SessionRehydratedByAPost_IsAnnouncedOnItsFirstGet()
        {
            await using var scenario = await Scenario.StartAsync(Catalogue("old-tool"));

            await scenario.RestartAsync(Catalogue("new-tool"));
            Assert.Contains("new-tool", await scenario.Client.ListToolNamesAsync(scenario.Ct));

            var (stream, server) = await scenario.AttachAsync(null);
            await using var _ = stream;

            AssertOneOfEach(await CountUntilSentinelAsync(stream, server, expected: 3, scenario.Ct));
        }

        [Fact]
        public async Task GetResumingAPostStream_DoesNotUseUpTheAnnouncement()
        {
            await using var scenario = await Scenario.StartAsync(Catalogue("old-tool"));
            var postEventId = await scenario.Client.PostAndReadFirstEventIdAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\"}", scenario.Ct);

            await scenario.RestartAsync(Catalogue("new-tool"));

            // Rehydrates the session on a transport that has no standalone stream, which would drop the announcement.
            await using (var replayed = await scenario.Client.OpenStreamAsync(postEventId, scenario.Ct))
                await replayed.ReadToEndAsync();

            var (stream, server) = await scenario.AttachAsync(null);
            await using var _ = stream;

            AssertOneOfEach(await CountUntilSentinelAsync(stream, server, expected: 3, scenario.Ct));
        }

        [Fact]
        public async Task NewSession_FirstGet_AnnouncesNothing()
        {
            await using var scenario = await Scenario.StartAsync(Catalogue("tool"));

            var (stream, server) = await scenario.AttachAsync(null);
            await using var _ = stream;

            Assert.Empty(await CountUntilSentinelAsync(stream, server, expected: 0, scenario.Ct));
        }

        [Fact]
        public async Task SameProcessResume_AnnouncesNothing()
        {
            await using var scenario = await Scenario.StartAsync(Catalogue("tool"));
            var (stream, server) = await scenario.AttachAsync(null);
            var lastEventId = stream.LastEventId;
            await stream.DisposeAsync();

            await AssertResumeIsSilentAsync(scenario, server, lastEventId);
        }

        [Fact]
        public async Task CapabilityWithoutListChanged_IsNotAnnounced()
        {
            static void ToolsOnly(McpServerOptions options) => options.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
                Prompts = new PromptsCapability(),
            };

            await using var scenario = await Scenario.StartAsync(ToolsOnly);

            await scenario.RestartAsync(ToolsOnly);
            var (stream, server) = await scenario.AttachAsync(null);
            await using var _ = stream;

            var counts = await CountUntilSentinelAsync(stream, server, expected: 1, scenario.Ct);
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

            await using var scenario = await Scenario.StartAsync(Catalogue("tool"));

            await scenario.RestartAsync(FailingToolsAnnouncement);
            var (stream, server) = await scenario.AttachAsync(null);
            await using var _ = stream;

            var counts = await CountUntilSentinelAsync(stream, server, expected: 2, scenario.Ct);
            Assert.False(counts.ContainsKey(Tools));
            Assert.Equal(1, counts[Prompts]);
            Assert.Equal(1, counts[Resources]);
        }

        /// <summary>The C# client attached across the restart: it reattaches by itself and has to re-list.</summary>
        [Fact]
        public async Task CSharpClient_AttachedAcrossRestart_IsToldOnce_AndListsTheNewCatalogue()
        {
            await using var scenario = await Scenario.StartAsync(Catalogue("old-tool"));
            var ct = scenario.Ct;
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{scenario.Port}/"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                }),
                cancellationToken: ct);

            var counts = new ConcurrentDictionary<string, int>();
            var allSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask Count(JsonRpcNotification notification, CancellationToken _)
            {
                counts.AddOrUpdate(notification.Method, 1, (_, count) => count + 1);
                if (counts.Count == 3)
                    allSeen.TrySetResult();
                return ValueTask.CompletedTask;
            }
            await using var tools = client.RegisterNotificationHandler(Tools, Count);
            await using var prompts = client.RegisterNotificationHandler(Prompts, Count);
            await using var resources = client.RegisterNotificationHandler(Resources, Count);
            var sawSentinel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var sentinel = client.RegisterNotificationHandler(Sentinel, (_, _) => { sawSentinel.TrySetResult(); return ValueTask.CompletedTask; });

            await scenario.Host.WaitForLiveWriterAsync(client.SessionId!, ct);
            Assert.Empty(counts);

            await scenario.RestartAsync(Catalogue("new-tool"));

            // Nothing else sends these: no collection changed in this process, and no plugin is connected.
            await allSeen.Task.WaitAsync(ct);
            var server = await scenario.Host.WaitForSessionAsync(client.SessionId!, ct);
            await SettleThenSendSentinelAsync(server, ct);
            await sawSentinel.Task.WaitAsync(ct);

            AssertOneOfEach(counts);

            var listed = await client.ListToolsAsync(cancellationToken: ct);
            Assert.Contains(listed, tool => tool.Name == "new-tool");
            Assert.DoesNotContain(listed, tool => tool.Name == "old-tool");
        }

        /// <summary>One initialized raw client on a server that can be restarted over the same cache and port.</summary>
        sealed class Scenario : IAsyncDisposable
        {
            readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
            readonly IDistributedCache _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

            Scenario(int port)
            {
                Port = port;
                Client = new RawClient(port);
            }

            public int Port { get; }
            public RawClient Client { get; }
            public RestartableServer Host { get; private set; } = null!;
            public CancellationToken Ct => _timeout.Token;

            public static async Task<Scenario> StartAsync(Action<McpServerOptions> catalogue)
            {
                var scenario = new Scenario(ServerHarness.FreePort());
                scenario.Host = await RestartableServer.StartAsync(scenario._cache, scenario.Port, scenario.Ct, catalogue);
                await scenario.Client.InitializeAsync(scenario.Ct);
                return scenario;
            }

            public async Task RestartAsync(Action<McpServerOptions> catalogue)
            {
                await Host.DisposeAsync();
                Host = await RestartableServer.StartAsync(_cache, Port, Ct, catalogue);
            }

            /// <summary>Opens a standalone stream, up to its priming event, and finds the session on the server.</summary>
            public async Task<(RawStream Stream, McpServer Server)> AttachAsync(string? lastEventId)
            {
                var stream = await Client.OpenStreamAsync(lastEventId, Ct);
                await stream.ReadPrimingIdAsync();
                return (stream, await Host.WaitForSessionAsync(Client.SessionId, Ct));
            }

            public async ValueTask DisposeAsync()
            {
                await Host.DisposeAsync();
                Client.Dispose();
                _timeout.Dispose();
            }
        }

        /// <summary>A plain HTTP client, so the test decides exactly what each GET carries.</summary>
        sealed class RawClient : IDisposable
        {
            readonly HttpClient _http;

            public RawClient(int port) => _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };

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
                var events = await PostAndReadEventsAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", ct);
                using var json = JsonDocument.Parse(events.Last(static e => e.Data.Length > 0).Data);
                return json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                    .Select(static tool => tool.GetProperty("name").GetString()!)
                    .ToList();
            }

            public async Task<string> PostAndReadFirstEventIdAsync(string body, CancellationToken ct)
                => (await PostAndReadEventsAsync(body, ct)).First(static e => e.EventId != null).EventId!;

            async Task<List<SseItem<string>>> PostAndReadEventsAsync(string body, CancellationToken ct)
            {
                using var response = await PostAsync(body, ct);
                response.EnsureSuccessStatusCode();
                var events = new List<SseItem<string>>();
                await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync(ct)).EnumerateAsync(ct))
                    events.Add(item);
                return events;
            }

            public async Task<RawStream> OpenStreamAsync(string? lastEventId, CancellationToken ct)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/");
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                request.Headers.TryAddWithoutValidation(McpSessionTokenMiddleware.SessionIdHeader, SessionId);
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", Protocol);
                if (lastEventId != null)
                    request.Headers.TryAddWithoutValidation(ExpiredStreamResumeMiddleware.LastEventIdHeader, lastEventId);

                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                try
                {
                    response.EnsureSuccessStatusCode();
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
                return new RawStream(response, SseParser.Create(await response.Content.ReadAsStreamAsync(ct)).EnumerateAsync(ct).GetAsyncEnumerator(ct));
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
            readonly IAsyncEnumerator<SseItem<string>> _events;

            public RawStream(HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> events)
            {
                _response = response;
                _events = events;
            }

            public string? LastEventId { get; private set; }

            async Task<SseItem<string>> ReadEventAsync()
            {
                if (!await _events.MoveNextAsync())
                    throw new InvalidOperationException("The stream ended before the next event.");
                LastEventId = _events.Current.EventId ?? LastEventId;
                return _events.Current;
            }

            /// <summary>The first event of a new stream, which carries no message, only an id.</summary>
            public async Task<string> ReadPrimingIdAsync()
                => (await ReadEventAsync()).EventId ?? throw new InvalidOperationException("The priming event has no id.");

            /// <summary>The method of the next notification.</summary>
            public async Task<string> ReadMethodAsync()
            {
                while (true)
                {
                    var item = await ReadEventAsync();
                    if (string.IsNullOrWhiteSpace(item.Data))
                        continue;
                    using var json = JsonDocument.Parse(item.Data);
                    return json.RootElement.GetProperty("method").GetString()!;
                }
            }

            public async Task ReadToEndAsync()
            {
                while (await _events.MoveNextAsync())
                {
                }
            }

            public async ValueTask DisposeAsync()
            {
                _response.Dispose();
                try
                {
                    await _events.DisposeAsync();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or NotSupportedException or OperationCanceledException)
                {
                    // Only a read still in flight can object, and this stream is being abandoned.
                }
            }
        }
    }
}
