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
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

#pragma warning disable MCP9006, MCPEXP002

namespace com.IvanMurzak.GameDev.MCP.Server.Tests.Harness
{
    /// <summary>One server process: the wiring of Program.cs for session persistence, over a cache that outlives it.</summary>
    internal sealed class RestartableServer : IAsyncDisposable
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

        public static async Task<RestartableServer> StartAsync(
            IDistributedCache cache, int port, CancellationToken ct, Action<McpServerOptions>? configure = null)
        {
            var sessions = new ConcurrentDictionary<string, McpServer>();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Services.AddSingleton(cache);
            builder.Services.AddSingleton<ISessionMigrationHandler>(sp => new SessionMigrationHandler(
                cache, NullLogger<SessionMigrationHandler>.Instance));
            builder.Services
                .AddMcpServer(configure ?? (o => o.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } }))
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
