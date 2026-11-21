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
using System.IO;

namespace com.IvanMurzak.GameDev.MCP.Server.Startup
{
    /// <summary>
    /// The MCP <c>instructions</c> the server returns from <c>initialize</c>: text clients show the model at session start,
    /// even when they load the tools themselves on demand (Claude Code's tool search). This host has no engine-specific
    /// text of its own, so the engine plugin that starts it names a UTF-8 file with <c>--instructions-file</c> or the
    /// <c>MCP_INSTRUCTIONS_FILE</c> environment variable (inherited by the server it spawns). A missing, empty or
    /// unreadable file means no instructions; text past <see cref="MaxChars"/> is cut.
    /// </summary>
    public static class ServerInstructions
    {
        public const string ArgName = "instructions-file";
        public const string EnvVar = "MCP_INSTRUCTIONS_FILE";

        /// <summary>Upper bound kept in the handshake (clients cut long instructions anyway: Claude Code at 4096 characters).</summary>
        public const int MaxChars = 8192;

        /// <summary>The file named by the argument (wins, also when empty) or the environment variable; null when there is none.</summary>
        public static string? ResolvePath(IReadOnlyDictionary<string, string> parsedArgs, Func<string, string?> getEnv)
        {
            if (parsedArgs.TryGetValue(ArgName, out var arg))
                return string.IsNullOrWhiteSpace(arg) ? null : arg.Trim(); // an explicit empty argument turns them off
            var env = getEnv(EnvVar);
            return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
        }

        /// <summary>The instructions text from <paramref name="path"/>, or null (with <paramref name="error"/> set when the file could not be read).</summary>
        public static string? Load(string? path, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length == 0)
                    return null;
                return text.Length > MaxChars ? text.Substring(0, MaxChars) : text;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                error = e.Message;
                return null;
            }
        }
    }
}
