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
using System.Text.Json;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Utils;
using com.IvanMurzak.McpPlugin.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using NLog;
using NLog.Extensions.Logging;
using StackExchange.Redis;

namespace com.IvanMurzak.GameDev.MCP.Server
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Configure NLog
            LogManager.Setup().LoadConfigurationFromFile("NLog.config");

            // Default the streamableHttp idle-session window to 6 hours for this local server.
            // The plugin's built-in default is 600s (10 min), which is too aggressive for a
            // single-user local editor session and drops the MCP session mid-work. We only seed
            // the env var when it is unset, and DataArguments parses CLI args after env vars, so an
            // explicit MCP_PLUGIN_IDLE_TIMEOUT_SECONDS or --idle-timeout-seconds still overrides this.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Consts.MCP.Server.Env.IdleTimeoutSeconds)))
                Environment.SetEnvironmentVariable(Consts.MCP.Server.Env.IdleTimeoutSeconds, "21600"); // 6 hours

            var dataArguments = new DataArguments(args);

            // In STDIO mode, redirect console logs to stderr to avoid polluting stdout with non-JSON content
            if (dataArguments.ClientTransport == Consts.MCP.Server.TransportMethod.stdio)
            {
                var consoleTarget = LogManager.Configuration?.FindTargetByName("console") as NLog.Targets.ColoredConsoleTarget;
                if (consoleTarget != null)
                {
                    consoleTarget.StdErr = true;
                }
                LogManager.ReconfigExistingLoggers();
            }

            var logger = LogManager.GetCurrentClassLogger();
            try
            {
                var consoleWriteLine = dataArguments.ClientTransport switch
                {
                    Consts.MCP.Server.TransportMethod.stdio => (Action<string>)(message => { /* ignore console output */ }),
                    Consts.MCP.Server.TransportMethod.streamableHttp => (Action<string>)(message => Console.WriteLine(message)),
                    _ => throw new ArgumentException($"Unsupported transport method: {dataArguments.ClientTransport}. " +
                        $"Supported methods are: {Consts.MCP.Server.TransportMethod.stdio}, {Consts.MCP.Server.TransportMethod.streamableHttp}")
                };

                consoleWriteLine("Location: " + Environment.CurrentDirectory);
                consoleWriteLine($"Launch arguments: {string.Join(" ", args)}");
                consoleWriteLine($"Parsed arguments: {JsonSerializer.Serialize(dataArguments, JsonOptions.Pretty)}");

                var builder = WebApplication.CreateBuilder(args);

                // Replace default logging with NLog
                builder.Logging.ClearProviders();
                builder.Logging.AddNLog();

                // Setup MCP Plugin ---------------------------------------------------------------

                var mcpServerBuilder = builder.Services
                    .WithMcpServer(dataArguments, logger)
                    .WithMcpPluginServer(dataArguments);

                // Issue #70 — Option B: when REDIS_URL is set, plug Redis into the SDK's
                // session-migration + SSE event-store hooks so sessions survive a restart.
                // When unset, the server skips the registrations and behaves as before
                // (sessions are lost on restart) — a startup warning is logged below.
                var redisUrl = builder.Configuration["REDIS_URL"];
                var streamableHttp = dataArguments.ClientTransport == Consts.MCP.Server.TransportMethod.streamableHttp;
                var sessionPersistenceEnabled = streamableHttp && !string.IsNullOrWhiteSpace(redisUrl);

                if (sessionPersistenceEnabled)
                {
                    // StackExchange.Redis's ConfigurationOptions.Parse expects "host:port[,opt=val,...]"
                    // form, NOT a URI like "redis://host:port/db". The rest of the stack uses the URI
                    // form (.env.example, docker-compose, the Python backend's redis-py), so normalize
                    // here before parsing — otherwise Parse keeps "redis://host" as the host name and
                    // the connection target becomes "redis://host:port/db:6379", which never resolves.
                    var redisConfigString = NormalizeRedisUrlForStackExchange(redisUrl!);

                    builder.Services.AddStackExchangeRedisCache(options =>
                    {
                        var redisConfig = ConfigurationOptions.Parse(redisConfigString);
                        redisConfig.AbortOnConnectFail = false;
                        redisConfig.ConnectRetry = 3;
                        redisConfig.ConnectTimeout = 2000;
                        redisConfig.SyncTimeout = 2000;
                        options.ConfigurationOptions = redisConfig;
                        // MCP_REDIS_KEY_PREFIX namespaces this server's cache entries in Redis so
                        // multiple stacks (parallel worktrees, blue/green deployments, multi-tenant
                        // shared-Redis hosts) can share one Redis instance without colliding on keys.
                        // Defaults to "mcp-server:" to preserve historical behaviour.
                        options.InstanceName = builder.Configuration["MCP_REDIS_KEY_PREFIX"] ?? "mcp-server:";
                    });

                    // Resolve the migration-record TTL from config (issue #209). The previous
                    // hardcoded 2h sliding window expired recovery records long before the
                    // in-memory idle window (up to 10h in prod) released the session, so any
                    // session older than 2h was lost on restart. ResolveTtl sources an explicit
                    // MCP_SESSION_MIGRATION_TTL_SECONDS override, else falls back to the in-memory
                    // idle window (MCP_PLUGIN_IDLE_TIMEOUT_SECONDS), else a 10h default — always
                    // floored at the idle timeout so the recovery window can never be shorter than
                    // the in-memory session lifetime.
                    var sessionMigrationTtl = SessionMigrationHandler.ResolveTtl(
                        builder.Configuration[SessionMigrationHandler.MigrationTtlEnvVar],
                        builder.Configuration[SessionMigrationHandler.IdleTimeoutEnvVar]);

                    builder.Services.AddSingleton<ISessionMigrationHandler>(sp =>
                        new SessionMigrationHandler(
                            sp.GetRequiredService<IDistributedCache>(),
                            sp.GetRequiredService<ILogger<SessionMigrationHandler>>(),
                            sessionMigrationTtl));
                    mcpServerBuilder.WithDistributedCacheEventStreamStore();
                }

                // builder.WebHost.UseUrls(Consts.Hub.DefaultEndpoint);

                logger.Info($"Start listening on port: {dataArguments.Port}");

                // Bind IPv4 and IPv6 separately to avoid dual-stack socket issues on macOS.
                builder.WebHost.UseKestrelForMcpPlugin(dataArguments.Port);

                var app = builder.Build();

                if (streamableHttp && !sessionPersistenceEnabled)
                {
                    app.Logger.LogWarning(
                        "REDIS_URL is not set — MCP sessions will NOT survive a server restart. "
                        + "Configure Redis to enable Option B (issue #70).");
                }

                // Middleware ----------------------------------------------------------------
                // ---------------------------------------------------------------------------

                // Setup SignalR ----------------------------------------------------
                app.UseMcpPluginServer(dataArguments);

                // Setup MCP client -------------------------------------------------
                if (dataArguments.ClientTransport == Consts.MCP.Server.TransportMethod.streamableHttp)
                {
                    // Add a GET /help endpoint for informational message
                    app.MapGet("/help", () =>
                    {
                        var header =
                            "Author: Ivan Murzak (https://github.com/IvanMurzak)\n" +
                            "Repository: GitHub (https://github.com/IvanMurzak/GameDev-MCP-Server)\n" +
                            "Copyright (c) 2026 Ivan Murzak\n" +
                            "Licensed under the Apache License, Version 2.0.\n" +
                            "See the LICENSE file in the project root for more information.\n" +
                            "\n" +
                            "Use \"/\" endpoint to get connected to MCP server\n";
                        return Results.Text(header, Consts.MimeType.TextPlain);
                    });
                }

                #region Print Logs
                if (logger.IsEnabled(NLog.LogLevel.Debug))
                {
                    var endpointDataSource = app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
                    foreach (var endpoint in endpointDataSource.Endpoints)
                        logger.Debug($"Configured endpoint: {endpoint.DisplayName}");

                    app.Use(async (context, next) =>
                    {
                        logger.Debug($"Request: {context.Request.Method} {context.Request.Path}");
                        try
                        {
                            await next.Invoke();
                            logger.Debug($"Response: {context.Response.StatusCode} ({context.Request.Method} {context.Request.Path})");
                        }
                        catch (OperationCanceledException)
                        {
                            // Optionally log as debug or ignore
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, $"Error occurred while processing request: {context.Request.Method} {context.Request.Path}");
                            return;
                        }
                    });
                }
                #endregion

                await app.RunAsync();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Application stopped due to an exception.");
                throw;
            }
            finally
            {
                LogManager.Shutdown();
            }
        }

        /// <summary>
        /// Convert a redis-URI-style connection string (redis://[user:pass@]host:port[/db])
        /// into the host:port[,password=...,defaultDatabase=N,ssl=True] form that
        /// StackExchange.Redis's ConfigurationOptions.Parse understands. Strings that
        /// already use the host:port form are returned unchanged.
        /// </summary>
        internal static string NormalizeRedisUrlForStackExchange(string redisUrl)
        {
            if (string.IsNullOrWhiteSpace(redisUrl))
                return redisUrl;

            var trimmed = redisUrl.Trim();
            var lower = trimmed.ToLowerInvariant();
            if (!lower.StartsWith("redis://") && !lower.StartsWith("rediss://"))
                return trimmed; // already in StackExchange.Redis form (host:port,...)

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                return trimmed; // fall through; let Parse surface the error

            var host = string.IsNullOrEmpty(uri.Host) ? "localhost" : uri.Host;
            var port = uri.Port > 0 ? uri.Port : 6379;
            var sb = new System.Text.StringBuilder();
            sb.Append(host).Append(':').Append(port);

            // userinfo -> password=...,user=...
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var userInfo = uri.UserInfo;
                var colonIdx = userInfo.IndexOf(':');
                string user = colonIdx >= 0 ? userInfo.Substring(0, colonIdx) : string.Empty;
                string password = colonIdx >= 0 ? userInfo.Substring(colonIdx + 1) : userInfo;
                if (!string.IsNullOrEmpty(password))
                    sb.Append(",password=").Append(Uri.UnescapeDataString(password));
                if (!string.IsNullOrEmpty(user))
                    sb.Append(",user=").Append(Uri.UnescapeDataString(user));
            }

            // path -> /<db>
            var path = uri.AbsolutePath;
            if (!string.IsNullOrEmpty(path) && path.Length > 1)
            {
                var dbStr = path.TrimStart('/');
                if (int.TryParse(dbStr, out var db) && db >= 0)
                    sb.Append(",defaultDatabase=").Append(db);
            }

            // rediss:// -> ssl=True
            if (lower.StartsWith("rediss://"))
                sb.Append(",ssl=True");

            return sb.ToString();
        }
    }
}
