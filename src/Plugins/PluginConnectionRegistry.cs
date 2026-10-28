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

namespace com.IvanMurzak.GameDev.MCP.Server.Plugins
{
    /// <summary>What an engine plugin connection is: the editor, or a running game (runtime build / play mode).</summary>
    public enum PluginRole
    {
        /// <summary>Not known yet: the connection has not sent <c>client_role</c> and has not finished its handshake.</summary>
        Unknown = 0,
        Editor = 1,
        Runtime = 2,
    }

    /// <summary>
    /// The plugin connections the server knows, with their role. Thread-safe; one per server.
    /// <para>
    /// A role comes from the hub-connection query parameter <see cref="RoleQueryKey"/>
    /// (<c>editor</c> | <c>runtime</c>) when the plugin sends it, otherwise from the version handshake:
    /// an <c>environment</c> ending in <c>(runtime)</c> (what the Godot runtime sends today) is a runtime,
    /// anything else an editor.
    /// </para>
    /// </summary>
    public sealed class PluginConnectionRegistry
    {
        /// <summary>Hub-connection query parameter a plugin sends to declare its role.</summary>
        public const string RoleQueryKey = "client_role";

        /// <summary>Handshake <c>environment</c> suffix that marks a runtime (game) connection.</summary>
        public const string RuntimeEnvironmentSuffix = "(runtime)";

        readonly object _gate = new object();
        readonly Dictionary<string, Entry> _connections = new Dictionary<string, Entry>(StringComparer.Ordinal);
        readonly Dictionary<string, PluginRole> _declared = new Dictionary<string, PluginRole>(StringComparer.Ordinal);
        readonly Func<DateTimeOffset> _clock;
        long _sequence;
        DateTimeOffset? _lastEditorDisconnect;

        public PluginConnectionRegistry(Func<DateTimeOffset>? clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>Parses a declared role; null for an absent or unrecognised value.</summary>
        public static PluginRole? ParseRole(string? value)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "editor": return PluginRole.Editor;
                case "runtime":
                case "game":
                case "player": return PluginRole.Runtime;
                default: return null;
            }
        }

        /// <summary>The role a version-handshake <c>environment</c> string implies.</summary>
        public static PluginRole RoleFromEnvironment(string? environment)
            => environment != null && environment.TrimEnd().EndsWith(RuntimeEnvironmentSuffix, StringComparison.OrdinalIgnoreCase)
                ? PluginRole.Runtime
                : PluginRole.Editor;

        /// <summary>
        /// Records the role a connecting plugin declared, before the connection is accepted. Taken by
        /// <see cref="TakeDeclared"/> when it is; dropped by <see cref="Remove"/> when it is not.
        /// </summary>
        public void Declare(string connectionId, PluginRole role)
        {
            lock (_gate)
                _declared[connectionId] = role;
        }

        /// <summary>The role declared for <paramref name="connectionId"/>, removing it; null when none was declared.</summary>
        public PluginRole? TakeDeclared(string connectionId)
        {
            lock (_gate)
                return _declared.Remove(connectionId, out var role) ? role : null;
        }

        /// <summary>Registers a new connection with its role (or <see cref="PluginRole.Unknown"/>) and its plugin token.</summary>
        public void Add(string connectionId, PluginRole role, string? token)
        {
            lock (_gate)
                _connections[connectionId] = new Entry(role, token, ++_sequence);
        }

        /// <summary>The connection's role; <see cref="PluginRole.Unknown"/> for an unknown connection.</summary>
        public PluginRole GetRole(string connectionId)
        {
            lock (_gate)
                return _connections.TryGetValue(connectionId, out var entry) ? entry.Role : PluginRole.Unknown;
        }

        /// <summary>
        /// Sets the role of a connection whose role was <see cref="PluginRole.Unknown"/> and returns its token.
        /// False (and nothing changes) when the connection is gone or already has a role: a declared role wins
        /// over the handshake guess.
        /// </summary>
        public bool TryClassify(string connectionId, PluginRole role, out string? token)
        {
            token = null;
            if (role == PluginRole.Unknown)
                return false;
            lock (_gate)
            {
                if (!_connections.TryGetValue(connectionId, out var entry) || entry.Role != PluginRole.Unknown)
                    return false;
                entry.Role = role;
                token = entry.Token;
                return true;
            }
        }

        /// <summary>
        /// Forgets a connection and returns the role it had (null when it was not registered). Remembers when
        /// the last editor left (see <see cref="EditorLeftWithin"/>).
        /// </summary>
        public PluginRole? Remove(string connectionId)
        {
            lock (_gate)
            {
                _declared.Remove(connectionId);
                if (!_connections.Remove(connectionId, out var entry))
                    return null;
                if (entry.Role != PluginRole.Runtime)
                    _lastEditorDisconnect = _clock();
                return entry.Role;
            }
        }

        /// <summary>The connections with <paramref name="role"/>, newest first.</summary>
        public IReadOnlyList<string> WithRole(PluginRole role)
        {
            lock (_gate)
                return _connections
                    .Where(pair => pair.Value.Role == role)
                    .OrderByDescending(pair => pair.Value.Sequence)
                    .Select(pair => pair.Key)
                    .ToList();
        }

        /// <summary>True when an editor (or a connection of unknown role) disconnected less than <paramref name="window"/> ago.</summary>
        public bool EditorLeftWithin(TimeSpan window)
        {
            lock (_gate)
                return _lastEditorDisconnect.HasValue && _clock() - _lastEditorDisconnect.Value < window;
        }

        sealed class Entry
        {
            public Entry(PluginRole role, string? token, long sequence)
            {
                Role = role;
                Token = token;
                Sequence = sequence;
            }

            public PluginRole Role { get; set; }
            public string? Token { get; }
            public long Sequence { get; }
        }
    }
}
