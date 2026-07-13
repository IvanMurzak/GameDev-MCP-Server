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
using com.IvanMurzak.GameDev.MCP.Server.Configure;
using com.IvanMurzak.GameDev.MCP.Server.Startup;
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
        public static async Task<int> Main(string[] args)
        {
            // Subcommand dispatch: `gamedev-mcp-server configure --agent <id> [--url ...]` writes an MCP
            // client config from the terminal using the shared configurator registry, then exits WITHOUT
            // starting the server. Handled first so it never boots Kestrel / NLog server logging.
            if (ConfigureCommand.IsInvocation(args))
                return ConfigureCommand.Run(args);

            // Configure NLog
            LogManager.Setup().LoadConfigurationFromFile("NLog.config");

            // Default the streamableHttp idle-session window to 6 hours for this local server.
            // The plugin's built-in default is 600s (10 min), which is too aggressive for a
            // single-user local editor session and drops the MCP session mid-work. We only seed
            // the env var when it is unset, and DataArguments parses CLI args after env vars, so an
            // explicit MCP_PLUGIN_IDLE_TIMEOUT_SECONDS or --idle-timeout-seconds still overrides this.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Consts.MCP.Server.Env.IdleTimeoutSeconds)))
                Environment.SetEnvironmentVariable(Consts.MCP.Server.Env.IdleTimeoutSeconds, "21600"); // 6 hours

            // Transport-conditional authentication default (mcp-authorize, design 02 §"modes"):
            // stdio => none, streamableHttp => oauth. DataArguments defaults auth to `none` for BOTH
            // transports (it cannot know the host policy), so the host seeds the http default here.
            // Mirroring the idle-timeout seed above, we only SEED the env var — never override an
            // explicit choice — and DataArguments re-parses it below. oauth mode REQUIRES --auth-issuer
            // AND --public-url (the shared AccountMcpStrategy.Validate throws otherwise), so http only
            // defaults to oauth when both are configured; without them it stays on `none` with a loud
            // warning rather than crashing the documented bare-run quickstart. See HostAuthDefaults.
            var authProbe = new DataArguments(args);
            var authDecision = HostAuthDefaults.Decide(
                authProbe.ClientTransport,
                HostAuthDefaults.IsAuthExplicitlySet(args),
                hasAuthIssuer: !string.IsNullOrWhiteSpace(authProbe.AuthIssuer),
                hasPublicUrl: !string.IsNullOrWhiteSpace(authProbe.PublicUrl));

            if (authDecision == HostAuthDefaults.Decision.DefaultHttpToOauth)
                Environment.SetEnvironmentVariable(
                    Consts.MCP.Server.Env.Auth,
                    Consts.MCP.Server.AuthOption.oauth.ToString());

            var httpUnauthenticatedFallback = authDecision == HostAuthDefaults.Decision.DefaultHttpToNoneWithWarning;

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

                // Additional configurable Origin allow-list (b2 carry-forward / design 02 §Origin
                // validation). The library default admits loopback + the --public-url origin only. When
                // MCP_ALLOWED_ORIGINS (or --allowed-origins) is set, REPLACE the default
                // OriginValidationOptions singleton with one that also admits the configured origins, so
                // a hosted browser client on a different origin works without a blanket `*` on
                // credentialed paths. Registered AFTER WithMcpPluginServer so this instance wins on
                // resolution. Unset => the library default (already registered) stands untouched.
                var rawAllowedOrigins = Environment.GetEnvironmentVariable(OriginAllowList.EnvVar);
                var parsedArgs = ArgsUtils.ParseLineArguments(args);
                if (parsedArgs.TryGetValue(OriginAllowList.ArgName, out var argAllowedOrigins)
                    && !string.IsNullOrWhiteSpace(argAllowedOrigins))
                    rawAllowedOrigins = argAllowedOrigins;

                if (OriginAllowList.TryBuildOptions(dataArguments, rawAllowedOrigins, out var originOptions)
                    && originOptions != null)
                {
                    builder.Services.AddSingleton(originOptions);
                    logger.Info($"Origin allow-list extended with {originOptions.AllowedOrigins.Count} configured origin(s) (plus loopback).");
                }

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

                logger.Info($"Start listening on port: {dataArguments.Port} (bind: {dataArguments.Bind ?? "loopback"})");

                // Bind IPv4 and IPv6 separately to avoid dual-stack socket issues on macOS. The bind
                // address (D8: default loopback; --bind/MCP_BIND=any|0.0.0.0|<ip> for LAN/hosted deploys
                // that nginx must reach) is forwarded to the library's bind-aware Kestrel overload.
                builder.WebHost.UseKestrelForMcpPlugin(dataArguments.Port, dataArguments.Bind);

                var app = builder.Build();

                if (streamableHttp && !sessionPersistenceEnabled)
                {
                    app.Logger.LogWarning(
                        "REDIS_URL is not set — MCP sessions will NOT survive a server restart. "
                        + "Configure Redis to enable Option B (issue #70).");
                }

                if (httpUnauthenticatedFallback)
                {
                    app.Logger.LogWarning(
                        "streamableHttp is running WITHOUT authentication (auth=none) because --auth-issuer/--public-url "
                        + "were not configured. For a network-exposed deployment set --auth oauth with --auth-issuer and "
                        + "--public-url (see README § Authentication). Pass --auth none explicitly to silence this warning.");
                }

                app.Logger.LogInformation(
                    "MCP auth mode: {AuthMode} (transport: {Transport}, bind: {Bind}).",
                    dataArguments.Authorization, dataArguments.ClientTransport, dataArguments.Bind ?? "loopback");

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
                return 0;
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
