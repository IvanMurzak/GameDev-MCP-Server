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
using System.Text.Json;
using System.Text.Json.Nodes;
using com.IvanMurzak.McpPlugin.Common.Utils;

namespace com.IvanMurzak.GameDev.MCP.Server.Startup
{
    /// <summary>
    /// The startup lines that show how the server was launched. Container logs outlive the process and are
    /// read and shipped by more people than the secrets are meant for, so anything named like a secret is
    /// masked: by name, so that a secret option added later is covered too.
    /// </summary>
    internal static class LaunchLog
    {
        const string Mask = "***";

        // "redis" and "dsn": their values are connection strings that can carry a password.
        static readonly string[] SecretNameParts = { "token", "secret", "password", "pwd", "key", "credential", "redis", "dsn" };

        static bool IsSecretName(string name)
            => SecretNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

        public static void Write(Action<string> writeLine, string[] args, DataArguments dataArguments)
        {
            writeLine($"Launch arguments: {string.Join(" ", RedactArguments(args))}");
            writeLine($"Parsed arguments: {RedactParsed(dataArguments)}");
        }

        /// <summary>Masks the value in both accepted spellings: <c>--name=value</c> and <c>--name value</c>.</summary>
        internal static string[] RedactArguments(string[] args)
        {
            var redacted = (string[])args.Clone();
            for (var i = 0; i < redacted.Length; i++)
            {
                var equals = redacted[i].IndexOf('=');
                if (equals > 0)
                {
                    if (IsSecretName(redacted[i].Substring(0, equals)))
                        redacted[i] = redacted[i].Substring(0, equals + 1) + Mask;
                }
                else if (redacted[i].StartsWith('-') && IsSecretName(redacted[i]) && i + 1 < redacted.Length && !redacted[i + 1].StartsWith('-'))
                {
                    redacted[++i] = Mask;
                }
            }
            return redacted;
        }

        internal static string RedactParsed(DataArguments dataArguments)
        {
            var parsed = JsonSerializer.SerializeToNode(dataArguments, JsonOptions.Pretty)!.AsObject();
            foreach (var name in parsed.Where(p => p.Value != null && IsSecretName(p.Key)).Select(p => p.Key).ToList())
                parsed[name] = Mask;
            return parsed.ToJsonString(JsonOptions.Pretty);
        }
    }
}
