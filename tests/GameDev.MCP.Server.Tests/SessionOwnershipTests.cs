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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using Microsoft.AspNetCore.Authentication;
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
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

#pragma warning disable MCP9006, MCPEXP002

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// A persisted session belongs to its creator: after a restart the SDK makes whoever presents the id the
    /// owner of whatever the migration handler returns, so the handler has to tell owner from stranger.
    /// </summary>
    public class SessionOwnershipTests
    {
        const string Id = "session-1";

        static IDistributedCache NewCache() => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        static SessionMigrationHandler Handler(IDistributedCache cache) => new(cache, NullLogger<SessionMigrationHandler>.Instance);

        static InitializeRequestParams Init() => new()
        {
            ProtocolVersion = "2025-11-25",
            Capabilities = new ClientCapabilities(),
            ClientInfo = new Implementation { Name = "owner-client", Version = "1.0.0" },
        };

        static HttpContext As(params Claim[] claims)
            => new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, claims.Length == 0 ? null : "test")) };

        static Claim Sub(string value, string issuer = "as") => new("sub", value, ClaimValueTypes.String, issuer);

        static async Task<InitializeRequestParams?> Roundtrip(HttpContext creator, HttpContext caller)
        {
            var cache = NewCache();
            await Handler(cache).OnSessionInitializedAsync(creator, Id, Init(), CancellationToken.None);
            return await Handler(cache).AllowSessionMigrationAsync(caller, Id, CancellationToken.None);
        }

        [Fact]
        public async Task SameUser_Rehydrates_AndKeepsInitParams()
        {
            var rehydrated = await Roundtrip(As(Sub("alice")), As(Sub("alice")));

            Assert.Equal("owner-client", rehydrated?.ClientInfo?.Name);
            Assert.Equal("2025-11-25", rehydrated?.ProtocolVersion);
        }

        [Fact]
        public async Task OwnerIsBoundLikeTheSdk_NameIdentifierAheadOfSub()
        {
            var both = new[] { new Claim(ClaimTypes.NameIdentifier, "alice", ClaimValueTypes.String, "as"), Sub("alice") };

            Assert.NotNull(await Roundtrip(As(both), As(both)));
            Assert.Null(await Roundtrip(As(both), As(Sub("alice"))));
        }

        [Fact]
        public async Task DifferentUser_IsRefused() => Assert.Null(await Roundtrip(As(Sub("alice")), As(Sub("mallory"))));

        [Fact]
        public async Task SameSubject_FromAnotherIssuer_IsRefused() => Assert.Null(await Roundtrip(As(Sub("alice", "as-1")), As(Sub("alice", "as-2"))));

        [Fact]
        public async Task AnonymousCaller_OfAnOwnedSession_IsRefused() => Assert.Null(await Roundtrip(As(Sub("alice")), As()));

        [Fact]
        public async Task AuthenticatedCaller_OfAnAnonymousSession_IsRefused() => Assert.Null(await Roundtrip(As(), As(Sub("mallory"))));

        [Fact]
        public async Task AnonymousSession_RehydratesForAnonymousCaller() => Assert.NotNull(await Roundtrip(As(), As()));

        [Fact]
        public void SeparatorInAClaim_CannotForgeAnotherIdentity()
            => Assert.NotEqual(
                SessionMigrationHandler.HashOwner(As(Sub("a\u001fb")).User),
                SessionMigrationHandler.HashOwner(As(new Claim("sub", "a", ClaimValueTypes.String, "b\u001fas")).User));

        [Fact]
        public async Task RecordHoldsNoReadableIdentity()
        {
            var cache = NewCache();
            await Handler(cache).OnSessionInitializedAsync(As(Sub("alice@example.com")), Id, Init(), CancellationToken.None);

            var stored = Encoding.UTF8.GetString((await cache.GetAsync("mcp:session:" + Id))!);

            Assert.DoesNotContain("alice", stored);
            Assert.Contains("owner-client", stored); // else this looked at the wrong bytes
        }

        [Theory]
        [InlineData("{\"owner\":42,\"init\":{\"protocolVersion\":\"2025-11-25\"}}")]
        [InlineData("not json")]
        public async Task DamagedRecord_IsRefused(string stored)
        {
            var cache = NewCache();
            await cache.SetAsync("mcp:session:" + Id, Encoding.UTF8.GetBytes(stored), new DistributedCacheEntryOptions());

            Assert.Null(await Handler(cache).AllowSessionMigrationAsync(As(), Id, CancellationToken.None));
        }

        /// <summary>A record written before owners existed: bare init params. Nobody can prove it is theirs.</summary>
        static async Task<IDistributedCache> CacheWithLegacyRecord()
        {
            var cache = NewCache();
            await cache.SetAsync("mcp:session:" + Id, JsonSerializer.SerializeToUtf8Bytes(Init(), McpJsonUtilities.DefaultOptions), new DistributedCacheEntryOptions());
            return cache;
        }

        [Fact]
        public async Task LegacyRecord_IsRefused_WhenTheCallerIsAuthenticated()
            => Assert.Null(await Handler(await CacheWithLegacyRecord()).AllowSessionMigrationAsync(As(Sub("alice")), Id, CancellationToken.None));

        [Fact]
        public async Task LegacyRecord_StillRehydrates_WithoutAuthentication()
        {
            var rehydrated = await Handler(await CacheWithLegacyRecord()).AllowSessionMigrationAsync(As(), Id, CancellationToken.None);

            Assert.Equal("owner-client", rehydrated?.ClientInfo?.Name);
        }

        /// <summary>End to end with the real SDK and a restart: the stranger gets 404 and nothing of the owner's.</summary>
        [Fact]
        public async Task AfterRestart_StrangerCannotTakeTheSession_NorReplayItsEvents_AndOwnerKeepsIt()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = timeout.Token;
            var cache = NewCache();
            var port = ServerHarness.FreePort();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            string sessionId, resultEventId;
            await using (var first = await AuthedServer.StartAsync(cache, port, ct))
            {
                var init = await Post(http, "alice", null, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = Protocol, capabilities = new { }, clientInfo = new { name = "c", version = "1" } } }, ct);
                sessionId = init.Headers.GetValues("Mcp-Session-Id").Single();
                await Post(http, "alice", sessionId, new { jsonrpc = "2.0", method = "notifications/initialized" }, ct);

                var call = await Post(http, "alice", sessionId, new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "secret", arguments = new { } } }, ct);
                var body = await call.Content.ReadAsStringAsync(ct);
                Assert.Contains(AuthedServer.Secret, body); // else the replay below proves nothing
                resultEventId = body.Split('\n').First(l => l.StartsWith("id: ", StringComparison.Ordinal)).Substring(4).Trim();

                // The live session is what the SDK protects itself: same refusal expected before and after.
                Assert.Equal(HttpStatusCode.Forbidden, (await Post(http, "mallory", sessionId, ToolsList, ct)).StatusCode);
            }

            await using var second = await AuthedServer.StartAsync(cache, port, ct);

            Assert.Equal(HttpStatusCode.NotFound, (await Post(http, "mallory", sessionId, ToolsList, ct)).StatusCode);

            using var replay = new HttpRequestMessage(HttpMethod.Get, "");
            replay.Headers.Accept.ParseAdd("text/event-stream");
            replay.Headers.Add("Mcp-Session-Id", sessionId);
            replay.Headers.Add("Mcp-Protocol-Version", Protocol);
            replay.Headers.Add("Last-Event-ID", resultEventId.Substring(0, resultEventId.LastIndexOf(':') + 1) + "0");
            replay.Headers.Add(AuthedServer.UserHeader, "mallory");
            var replayed = await http.SendAsync(replay, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.NotFound, replayed.StatusCode);
            Assert.DoesNotContain(AuthedServer.Secret, await replayed.Content.ReadAsStringAsync(ct));

            // The stranger's attempts left the owner's session intact, and once it is back the SDK guards it.
            Assert.Equal(HttpStatusCode.OK, (await Post(http, "alice", sessionId, ToolsList, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(http, "mallory", sessionId, ToolsList, ct)).StatusCode);
        }

        const string Protocol = "2025-11-25";
        static readonly object ToolsList = new { jsonrpc = "2.0", id = 3, method = "tools/list", @params = new { } };

        static Task<HttpResponseMessage> Post(HttpClient http, string user, string? sessionId, object body, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add(AuthedServer.UserHeader, user);
            request.Headers.Add("Mcp-Protocol-Version", Protocol);
            if (sessionId != null)
                request.Headers.Add("Mcp-Session-Id", sessionId);
            return http.SendAsync(request, ct);
        }

        /// <summary>The persistence wiring of Program.cs behind a real authentication scheme; the user is a request header.</summary>
        static class AuthedServer
        {
            public const string UserHeader = "X-Test-User";
            public const string Secret = "secret-result-of-alice";

            public static async Task<WebApplication> StartAsync(IDistributedCache cache, int port, CancellationToken ct)
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
                builder.Services.AddSingleton(cache);
                builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, HeaderAuth>("test", null);
                builder.Services.AddAuthorization();
                builder.Services.AddSingleton<ISessionMigrationHandler>(_ => new SessionMigrationHandler(cache, NullLogger<SessionMigrationHandler>.Instance));
                builder.Services
                    .AddMcpServer()
                    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateful)
                    .WithTools(new[] { McpServerTool.Create(() => Secret, new McpServerToolCreateOptions { Name = "secret" }) })
                    .WithDistributedCacheEventStreamStore(Program.ConfigureEventStreamStore);

                var app = builder.Build();
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapMcp().RequireAuthorization();
                await app.StartAsync(ct);
                return app;
            }

            sealed class HeaderAuth : AuthenticationHandler<AuthenticationSchemeOptions>
            {
                public HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

                protected override Task<AuthenticateResult> HandleAuthenticateAsync()
                {
                    var user = Request.Headers[UserHeader].ToString();
                    if (string.IsNullOrEmpty(user))
                        return Task.FromResult(AuthenticateResult.NoResult());
                    var identity = new ClaimsIdentity(new[] { new Claim("sub", user) }, "test");
                    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
                }
            }
        }
    }
}
