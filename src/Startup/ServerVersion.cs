/*
┌───────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                      │
│  Repository: GitHub (https://github.com/IvanMurzak/GameDev-MCP-Server)    │
│  Copyright (c) 2026 Ivan Murzak                                           │
│  Licensed under the Apache License, Version 2.0.                          │
│  See the LICENSE file in the project root for more information.           │
└───────────────────────────────────────────────────────────────────────────┘
*/

using System.Reflection;

namespace com.IvanMurzak.GameDev.MCP.Server.Startup
{
    /// <summary>The server's own version, as written by <c>configure</c> and reported to error tracking.</summary>
    internal static class ServerVersion
    {
        internal static string Resolve()
        {
            var assembly = typeof(ServerVersion).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                // Strip any "+<git-sha>" build-metadata suffix.
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }
            return assembly.GetName().Version?.ToString(3) ?? "8.0.0";
        }
    }
}
