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
    /// A runtime connection is never put in the library's plugin list (<see cref="ClientUtils"/>), so the
    /// remembered tool lists (<see cref="PluginCatalogCache"/>) stay the editor's.
    /// </summary>
    public sealed class RoleAwareNoAuthStrategy : IMcpConnectionStrategy
    {
        const string ReplacedReason = "A newer {0} connection has replaced this one (auth=none keeps one editor and one runtime connection).";

        readonly IMcpConnectionStrategy _inner;
        readonly PluginConnectionRegistry _registry;
        readonly ILogger _logger;

        public RoleAwareNoAuthStrategy(IMcpConnectionStrategy inner, PluginConnectionRegistry registry, TimeSpan editorReconnectWindow, ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            EditorReconnectWindow = editorReconnectWindow < TimeSpan.Zero ? TimeSpan.Zero : editorReconnectWindow;
        }

        /// <summary>How long after an editor disconnects a runtime connection is still not used in its place.</summary>
        public TimeSpan EditorReconnectWindow { get; }

        public PluginConnectionRegistry Registry => _registry;

        /// <summary>
        /// Set by the host once the services exist: asks the library to refresh its remembered lists after a
        /// connection turned out to be a runtime (see <see cref="OnHandshake"/>).
        /// </summary>
        public Action<string, string?>? CatalogMove { get; set; }

        public Consts.MCP.Server.AuthOption AuthOption => _inner.AuthOption;

        // Single routing slot from the library's point of view: only editors (and connections of unknown
        // role) are in its plugin list, and a newer one replaces the older.
        public bool AllowMultipleConnections => false;

        public void Validate(DataArguments dataArguments) => _inner.Validate(dataArguments);

        public void ConfigureAuthentication(TokenAuthenticationOptions options, DataArguments dataArguments)
            => _inner.ConfigureAuthentication(options, dataArguments);

        public void OnPluginConnected(Type hubType, string connectionId, string? token, ILogger logger, Action<string, string?> disconnectClient)
        {
            var role = _registry.TakeDeclared(connectionId) ?? PluginRole.Unknown;
            _registry.Add(connectionId, role);
            if (role != PluginRole.Runtime)
                ClientUtils.AddClient(hubType, connectionId, logger, token);

            logger.LogInformation("Plugin connection '{0}' connected (role: {1}).", connectionId, Describe(role));

            if (role != PluginRole.Unknown)
                ReplaceOthers(hubType, connectionId, role, disconnectClient);
        }

        /// <summary>
        /// The plugin finished its version handshake. A connection that did not declare its role gets one
        /// from <paramref name="environment"/> now, and replaces the older connection of that role.
        /// </summary>
        public void OnHandshake(Type hubType, string connectionId, string? environment, Action<string, string?> disconnectClient)
        {
            var role = PluginConnectionRegistry.RoleFromEnvironment(environment);
            if (!_registry.TryClassify(connectionId, role))
                return;

            _logger.LogInformation("Plugin connection '{0}' is {1} (handshake environment '{2}').", connectionId, Describe(role), environment);

            if (role == PluginRole.Runtime)
            {
                // It was registered as a possible editor at connect: take it out of the library's plugin
                // list and give the remembered lists back to the editor.
                ClientUtils.RemoveClient(hubType, connectionId, _logger);
                CatalogMove?.Invoke(connectionId, ResolveEditor());
            }

            ReplaceOthers(hubType, connectionId, role, disconnectClient);
        }

        void ReplaceOthers(Type hubType, string connectionId, PluginRole role, Action<string, string?> disconnectClient)
        {
            foreach (var otherId in _registry.OthersWithRole(connectionId, role))
            {
                _logger.LogInformation("auth=none: disconnecting older {0} connection '{1}' (replaced by '{2}').", Describe(role), otherId, connectionId);
                // Forget it now so routing cannot pick it before its disconnect arrives.
                _registry.Remove(otherId);
                if (role != PluginRole.Runtime)
                    ClientUtils.RemoveClient(hubType, otherId, _logger);
                disconnectClient(otherId, string.Format(ReplacedReason, Describe(role)));
            }
        }

        public void OnPluginDisconnected(Type hubType, string connectionId, ILogger logger)
        {
            var role = _registry.GetRole(connectionId);
            var known = _registry.Contains(connectionId);
            _registry.Remove(connectionId);
            if (known && role != PluginRole.Runtime)
                ClientUtils.RemoveClient(hubType, connectionId, logger);
        }

        public string? ResolveConnectionId(string? token, int retryOffset)
        {
            var targets = RoutingTargets();
            if (targets.Count == 0)
                return null;

            var byToken = ClientUtils.GetConnectionIdByToken(token);
            if (byToken != null && targets.Contains(byToken))
                return byToken;

            return targets[Math.Abs(retryOffset) % targets.Count];
        }

        string? ResolveEditor()
        {
            var editors = _registry.WithRole(PluginRole.Editor);
            if (editors.Count > 0)
                return editors[0];
            var unknown = _registry.WithRole(PluginRole.Unknown);
            return unknown.Count > 0 ? unknown[0] : null;
        }

        /// <summary>Editors, else connections of unknown role, else (no editor recently) runtimes; newest first.</summary>
        IReadOnlyList<string> RoutingTargets()
        {
            var editors = _registry.WithRole(PluginRole.Editor);
            if (editors.Count > 0)
                return editors;
            var unknown = _registry.WithRole(PluginRole.Unknown);
            if (unknown.Count > 0)
                return unknown;
            if (_registry.EditorLeftWithin(EditorReconnectWindow))
                return Array.Empty<string>();
            return _registry.WithRole(PluginRole.Runtime);
        }

        /// <summary>A runtime's list changes are not forwarded to agents while they are routed to an editor.</summary>
        public bool ShouldNotifySession(string pluginConnectionId, string sessionId)
        {
            if (_registry.GetRole(pluginConnectionId) == PluginRole.Runtime && ResolveEditor() != null)
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
