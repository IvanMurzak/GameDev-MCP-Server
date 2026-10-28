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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.McpPlugin.Common.Utils;
using com.IvanMurzak.McpPlugin.Server;
using com.IvanMurzak.McpPlugin.Server.Auth;
using com.IvanMurzak.McpPlugin.Server.Strategy;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.GameDev.MCP.Server.Plugins
{
    /// <summary>
    /// The <c>auth=none</c> connection strategy with plugin roles (replaces the library's
    /// <see cref="NoAuthMcpStrategy"/>, which keeps only the newest plugin connection).
    /// <list type="bullet">
    ///   <item>One editor and one runtime (game) connection are kept side by side. A new connection
    ///   replaces only an older connection of the SAME role, so a game that starts never disconnects the
    ///   editor, and an editor that reconnects after a reload still replaces its stale connection.</item>
    ///   <item>Tool calls, lists, prompts and resources go to the editor. A runtime connection is used only
    ///   when no editor is connected and none disconnected within <see cref="EditorReconnectWindow"/>
    ///   (a standalone game build), so calls made during an editor reload wait for the editor instead of
    ///   reaching the game.</item>
    ///   <item>A connection's role is known at connect when the plugin sends
    ///   <see cref="PluginConnectionRegistry.RoleQueryKey"/>; otherwise it is decided at the version
    ///   handshake (<see cref="OnHandshake"/>), and the replacement waits until then.</item>
    /// </list>
    /// Only editors enter the library's plugin list (<see cref="ClientUtils"/>), a connection of unknown role
    /// only once its handshake shows it is an editor, so the remembered tool lists
    /// (<see cref="PluginCatalogCache"/>) stay the editor's.
    /// </summary>
    public sealed class RoleAwareNoAuthStrategy : IMcpConnectionStrategy
    {
        const string ReplacedReason = "A newer {0} connection has replaced this one (auth=none keeps one editor and one runtime connection).";

        static readonly Type HubType = typeof(McpServerHub);

        readonly IMcpConnectionStrategy _inner;
        readonly Func<PluginCatalogCache?> _catalog;
        readonly ILogger _logger;
        int _runtimeServed;

        /// <param name="catalog">The library's remembered lists, resolved at use (the catalog itself depends on the strategy).</param>
        public RoleAwareNoAuthStrategy(IMcpConnectionStrategy inner, PluginConnectionRegistry registry, TimeSpan editorReconnectWindow,
            Func<PluginCatalogCache?> catalog, ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            EditorReconnectWindow = editorReconnectWindow < TimeSpan.Zero ? TimeSpan.Zero : editorReconnectWindow;
        }

        /// <summary>How long after an editor disconnects a runtime connection is still not used in its place.</summary>
        public TimeSpan EditorReconnectWindow { get; }

        public PluginConnectionRegistry Registry { get; }

        public Consts.MCP.Server.AuthOption AuthOption => _inner.AuthOption;

        // Single routing slot from the library's point of view: only editors are in its plugin list, and a
        // newer one replaces the older.
        public bool AllowMultipleConnections => false;

        public void Validate(DataArguments dataArguments) => _inner.Validate(dataArguments);

        public void ConfigureAuthentication(TokenAuthenticationOptions options, DataArguments dataArguments)
            => _inner.ConfigureAuthentication(options, dataArguments);

        public void OnPluginConnected(Type hubType, string connectionId, string? token, ILogger logger, Action<string, string?> disconnectClient)
        {
            var role = Registry.TakeDeclared(connectionId) ?? PluginRole.Unknown;
            var replaced = Registry.Add(connectionId, role, token);
            logger.LogInformation("Plugin connection '{0}' connected (role: {1}).", connectionId, Describe(role));

            if (role == PluginRole.Editor && !replaced.Contains(connectionId))
                ClientUtils.AddClient(HubType, connectionId, logger, token);
            Disconnect(replaced, connectionId, role, disconnectClient);
        }

        /// <summary>
        /// The plugin finished its version handshake. A connection that did not declare its role gets one
        /// from <paramref name="environment"/> now, and replaces the older connection of that role.
        /// </summary>
        public void OnHandshake(string connectionId, string? environment, Action<string, string?> disconnectClient)
        {
            var role = PluginConnectionRegistry.RoleFromEnvironment(environment);
            if (!Registry.TryClassify(connectionId, role, out var token, out var replaced))
                return;

            _logger.LogInformation("Plugin connection '{0}' is {1} (handshake environment '{2}').", connectionId, Describe(role), environment);
            if (role == PluginRole.Editor && !replaced.Contains(connectionId))
            {
                // Now known to be the editor: into the library's plugin list, and its remembered lists come back.
                ClientUtils.AddClient(HubType, connectionId, _logger, token);
                var catalog = _catalog();
                // Agents may have listed the game's tools while it stood in for the editor: forward the editor's next tool change.
                if (Interlocked.Exchange(ref _runtimeServed, 0) == 1)
                    catalog?.NoteToolListMissed();
                catalog?.OnPluginConnected(connectionId);
            }
            Disconnect(replaced, connectionId, role, disconnectClient);
        }

        void Disconnect(IReadOnlyList<string> replaced, string connectionId, PluginRole role, Action<string, string?> disconnectClient)
        {
            foreach (var id in replaced)
            {
                _logger.LogInformation("auth=none: disconnecting {0} connection '{1}' (replaced by a newer one).", Describe(role), id);
                if (role == PluginRole.Editor)
                    ClientUtils.RemoveClient(HubType, id, _logger);
                disconnectClient(id, string.Format(ReplacedReason, Describe(role)));
            }
        }

        public void OnPluginDisconnected(Type hubType, string connectionId, ILogger logger)
        {
            // Not only for a known editor: a connection replaced while it was being added may still be listed.
            if (Registry.Remove(connectionId) != PluginRole.Runtime && ClientUtils.GetAllConnectionIds(HubType).Contains(connectionId))
                ClientUtils.RemoveClient(HubType, connectionId, logger);
        }

        public string? ResolveConnectionId(string? token, int retryOffset)
        {
            var targets = RoutingTargets(out var runtime);
            if (targets.Count == 0)
                return null;
            if (runtime)
                Interlocked.Exchange(ref _runtimeServed, 1);

            var byToken = ClientUtils.GetConnectionIdByToken(token);
            if (byToken != null && targets.Contains(byToken))
                return byToken;

            return targets[Math.Abs(retryOffset) % targets.Count];
        }

        /// <summary>
        /// The editors, newest first; else, unless an editor left within <see cref="EditorReconnectWindow"/>,
        /// the runtimes. A connection whose role is not known yet (before its handshake) is never a target:
        /// it may be a game, and the handshake follows within milliseconds.
        /// </summary>
        IReadOnlyList<string> RoutingTargets(out bool runtime)
        {
            runtime = false;
            var editors = Registry.WithRole(PluginRole.Editor);
            if (editors.Count > 0 || Registry.EditorLeftWithin(EditorReconnectWindow))
                return editors;
            var runtimes = Registry.WithRole(PluginRole.Runtime);
            runtime = runtimes.Count > 0;
            return runtimes;
        }

        /// <summary>A runtime's list changes are not forwarded to agents while they are routed to an editor.</summary>
        public bool ShouldNotifySession(string pluginConnectionId, string sessionId)
        {
            if (Registry.GetRole(pluginConnectionId) == PluginRole.Runtime && Registry.WithRole(PluginRole.Editor).Count > 0)
                return false;
            return _inner.ShouldNotifySession(pluginConnectionId, sessionId);
        }

        public NotificationTarget ResolveNotificationTarget(string? routingToken) => _inner.ResolveNotificationTarget(routingToken);

        public McpClientData GetClientData(string? connectionId, IMcpSessionTracker sessionTracker)
            => _inner.GetClientData(connectionId, sessionTracker);

        public McpClientData[] GetAllClientData(string? connectionId, IMcpSessionTracker sessionTracker)
            => _inner.GetAllClientData(connectionId, sessionTracker);

        public McpServerData GetServerData(string? connectionId, IMcpSessionTracker sessionTracker)
            => _inner.GetServerData(connectionId, sessionTracker);

        static string Describe(PluginRole role) => role switch
        {
            PluginRole.Editor => "editor",
            PluginRole.Runtime => "runtime",
            _ => "unknown-role",
        };
    }
}
