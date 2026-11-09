/*
┌───────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                      │
│  Repository: GitHub (https://github.com/IvanMurzak/GameDev-MCP-Server)    │
│  Copyright (c) 2026 Ivan Murzak                                           │
│  Licensed under the Apache License, Version 2.0.                          │
│  See the LICENSE file in the project root for more information.           │
└───────────────────────────────────────────────────────────────────────────┘
*/

using System.Text.Json;
using com.IvanMurzak.McpPlugin.Common.Utils;

namespace com.IvanMurzak.GameDev.MCP.Server.Startup
{
    /// <summary>
    /// What the server tells about its launch. Container logs outlive the process and are read and shipped by
    /// more people than the secrets are meant for, so this is an allow-list: raw arguments are never printed
    /// (a secret can be spelled many ways, or sit inside a URL), and an option added later stays out until it
    /// is added here. Tokens are reported by presence only.
    /// </summary>
    internal static class LaunchLog
    {
        public static string Describe(DataArguments arguments) => "Parsed arguments: " + JsonSerializer.Serialize(
            new
            {
                arguments.Port,
                arguments.ClientTransport,
                arguments.Authorization,
                arguments.Bind,
                arguments.PublicUrl,
                arguments.AuthIssuer,
                arguments.PluginTimeoutMs,
                arguments.ToolTimeoutMs,
                arguments.IdleTimeoutSeconds,
                arguments.MaxIdleSessionCount,
                arguments.PluginReconnectGraceSeconds,
                arguments.PluginReconnectWaitSeconds,
                TokenSet = !string.IsNullOrEmpty(arguments.Token),
                WebhookTokenSet = !string.IsNullOrEmpty(arguments.WebhookToken),
            },
            JsonOptions.Pretty);
    }
}
