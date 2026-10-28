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
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.McpPlugin.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.GameDev.MCP.Server.Plugins
{
    /// <summary>
    /// Feeds plugin roles to <see cref="RoleAwareNoAuthStrategy"/> from the plugin hub: the declared
    /// <c>client_role</c> query parameter (or <c>X-Client-Role</c> header) at connect, and the version
    /// handshake's <c>environment</c> for a plugin that declared nothing.
    /// </summary>
    public sealed class PluginRoleHubFilter : IHubFilter
    {
        /// <summary>Header alternative to the <see cref="PluginConnectionRegistry.RoleQueryKey"/> query parameter.</summary>
        public const string RoleHeader = "X-Client-Role";

        readonly RoleAwareNoAuthStrategy _strategy;
        readonly ILogger<PluginRoleHubFilter> _logger;

        public PluginRoleHubFilter(RoleAwareNoAuthStrategy strategy, ILogger<PluginRoleHubFilter> logger)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            if (context.Hub is McpServerHub)
            {
                var request = context.Context.GetHttpContext()?.Request;
                var declared = request == null
                    ? null
                    : PluginConnectionRegistry.ParseRole(request.Query[PluginConnectionRegistry.RoleQueryKey].FirstOrDefault()
                        ?? request.Headers[RoleHeader].FirstOrDefault());
                if (declared.HasValue)
                    _strategy.Registry.Declare(context.Context.ConnectionId, declared.Value);
            }
            return next(context);
        }

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            var result = await next(invocationContext);
            // Classified only after an accepted handshake: a refused plugin must not replace a working one.
            if (invocationContext.Hub is McpServerHub hub
                && invocationContext.HubMethodName == nameof(McpServerHub.PerformVersionHandshake)
                && invocationContext.HubMethodArguments.FirstOrDefault() is RequestVersionHandshake handshake
                && result is VersionHandshakeResponse { Compatible: true })
            {
                try
                {
                    var clients = hub.Clients;
                    _strategy.OnHandshake(invocationContext.Context.ConnectionId, handshake.Environment,
                        (id, reason) => _ = clients.Client(id).ForceDisconnect(reason));
                }
                catch (Exception ex)
                {
                    // Never fail a handshake over role bookkeeping.
                    _logger.LogWarning(ex, "Could not classify plugin connection '{connectionId}'.", invocationContext.Context.ConnectionId);
                }
            }
            return result;
        }

        public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
        {
            await next(context, exception);
            // Also clears a declared role of a connection that was rejected before it was registered.
            if (context.Hub is McpServerHub)
                _strategy.Registry.Remove(context.Context.ConnectionId);
        }
    }
}
