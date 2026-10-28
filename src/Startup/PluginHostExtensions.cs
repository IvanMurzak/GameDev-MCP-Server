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
using com.IvanMurzak.GameDev.MCP.Server.Plugins;
using com.IvanMurzak.GameDev.MCP.Server.Tools;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Hub.Client;
using com.IvanMurzak.McpPlugin.Common.Utils;
using com.IvanMurzak.McpPlugin.Server;
using com.IvanMurzak.McpPlugin.Server.Strategy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace com.IvanMurzak.GameDev.MCP.Server.Startup
{
    /// <summary>
    /// Host-side additions to the McpPlugin.Server services. Register them AFTER
    /// <c>WithMcpPluginServer</c>, so these registrations win over the library's.
    /// </summary>
    public static class PluginHostExtensions
    {
        /// <summary>
        /// auth=none only: keeps an editor and a runtime (game) plugin connection side by side and routes
        /// tool calls to the editor (<see cref="RoleAwareNoAuthStrategy"/>). Other modes are unchanged.
        /// </summary>
        public static IServiceCollection AddPluginRoles(this IServiceCollection services, IDataArguments dataArguments)
        {
            if (dataArguments.Authorization != Consts.MCP.Server.AuthOption.none)
                return services;

            services.AddSingleton(new PluginConnectionRegistry());
            services.AddSingleton(sp =>
            {
                var strategy = new RoleAwareNoAuthStrategy(
                    new NoAuthMcpStrategy(),
                    sp.GetRequiredService<PluginConnectionRegistry>(),
                    dataArguments.GetPluginReconnectWait(),
                    sp.GetRequiredService<ILogger<RoleAwareNoAuthStrategy>>());
                // Resolved at use: the catalog itself depends on the strategy.
                strategy.CatalogMove = (runtimeId, editorId) =>
                {
                    var catalog = sp.GetService<PluginCatalogCache>();
                    if (catalog == null)
                        return;
                    catalog.OnPluginDisconnected(runtimeId);
                    if (editorId != null)
                        catalog.OnPluginConnected(editorId);
                };
                return strategy;
            });
            services.AddSingleton<IMcpConnectionStrategy>(sp => sp.GetRequiredService<RoleAwareNoAuthStrategy>());
            services.AddSingleton<PluginRoleHubFilter>();
            services.Configure<HubOptions>(options => options.AddFilter<PluginRoleHubFilter>());
            return services;
        }

        /// <summary>Puts <see cref="GuardedToolHub"/> in front of the library's tool runner for MCP and REST tool calls.</summary>
        public static IMcpServerBuilder AddToolCallGuard(this IMcpServerBuilder builder, IDataArguments dataArguments)
        {
            builder.Services.AddSingleton(sp =>
            {
                var runner = sp.GetRequiredService<RemoteToolRunner>();
                return new GuardedToolHub(
                    runner,
                    runner.RunCallTool,
                    sp.GetRequiredService<IMcpConnectionStrategy>(),
                    TimeSpan.FromMilliseconds(dataArguments.GetToolTimeoutMs()),
                    dataArguments.GetPluginReconnectWait(),
                    sp.GetRequiredService<ILogger<GuardedToolHub>>());
            });
            builder.Services.AddSingleton<IClientToolHub>(sp => sp.GetRequiredService<GuardedToolHub>());

            // A lone UTF-16 surrogate in any request's params fails the SDK's own deserialization with a
            // bare -32603 "An error occurred.": answer -32602 naming the offending field instead.
            builder.WithMessageFilters(filters => filters.AddIncomingFilter(next => (context, cancellationToken) =>
            {
                if (context.JsonRpcMessage is JsonRpcRequest { Params: not null } request
                    && Utf16Validation.Find(request.Params, "params") is { } path)
                    throw new McpProtocolException(Utf16Validation.Describe(path), McpErrorCode.InvalidParams);
                return next(context, cancellationToken);
            }));

            // The MCP caller's token (it also fires on notifications/cancelled) for GuardedToolHub.
            builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
            {
                ToolCallAmbient.Cancellation = cancellationToken;
                return next(context, cancellationToken);
            }));
            return builder;
        }

        /// <summary>The HTTP caller's token for GuardedToolHub (REST tool calls). Add before the MCP endpoints.</summary>
        public static WebApplication UseToolCallCancellation(this WebApplication app)
        {
            app.Use((context, next) =>
            {
                ToolCallAmbient.Cancellation = context.RequestAborted;
                return next(context);
            });
            return app;
        }
    }
}
