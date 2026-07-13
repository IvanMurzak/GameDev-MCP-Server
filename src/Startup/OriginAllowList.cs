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
using com.IvanMurzak.McpPlugin.Common.Utils;      // DataArguments
using com.IvanMurzak.McpPlugin.Server.Auth.OAuth; // UrlNormalization
using com.IvanMurzak.McpPlugin.Server.Security;    // OriginValidationOptions

namespace com.IvanMurzak.GameDev.MCP.Server.Startup
{
    /// <summary>
    /// Host surface for an <b>additional, configurable Origin allow-list</b> (carry-forward from b2 /
    /// design 02 §"Origin validation"). The shared library's default admits only loopback origins plus
    /// the <c>--public-url</c> origin (<c>OriginValidationOptions.FromArguments</c>) — correct for the
    /// hosted API and same-origin browser clients, but a hosted deployment that serves a browser client
    /// from a DIFFERENT origin (e.g. a first-party web console) needs to allow that origin WITHOUT
    /// weakening the guard to a blanket <c>*</c> on credentialed paths.
    /// <para>
    /// The host reads <c>MCP_ALLOWED_ORIGINS</c> / <c>--allowed-origins</c> (comma- or
    /// semicolon-separated) and, when set, registers a replacement <see cref="OriginValidationOptions"/>
    /// that keeps loopback + the public-url origin and ADDS the configured origins. Origins are
    /// normalized with the library's own <see cref="UrlNormalization.NormalizeOrigin"/> so they match
    /// the middleware's ordinal comparison exactly. When unset, the library default is left untouched.
    /// </para>
    /// </summary>
    public static class OriginAllowList
    {
        public const string EnvVar = "MCP_ALLOWED_ORIGINS";
        public const string ArgName = "allowed-origins";

        /// <summary>
        /// Parse a comma/semicolon-separated origin list into the normalized, de-duplicated set the
        /// middleware compares against. Malformed entries are dropped (they could never match anyway).
        /// </summary>
        public static IReadOnlyList<string> ParseOrigins(string? raw)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = UrlNormalization.NormalizeOrigin(part);
                if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized))
                    result.Add(normalized);
            }

            return result;
        }

        /// <summary>
        /// The merged allow-list = normalized public-url origin (if any) + the configured extra origins.
        /// Loopback is handled separately by <see cref="OriginValidationOptions.AllowLoopback"/>.
        /// </summary>
        public static IReadOnlyCollection<string> BuildAllowedOrigins(string? publicUrl, string? rawAllowedOrigins)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);

            var publicOrigin = UrlNormalization.NormalizeOrigin(publicUrl);
            if (!string.IsNullOrEmpty(publicOrigin))
                set.Add(publicOrigin);

            foreach (var origin in ParseOrigins(rawAllowedOrigins))
                set.Add(origin);

            return set;
        }

        /// <summary>
        /// Build a replacement <see cref="OriginValidationOptions"/> when extra origins are configured;
        /// returns false (and null) when <paramref name="rawAllowedOrigins"/> yields nothing, so the
        /// caller can leave the library's default registration in place.
        /// </summary>
        public static bool TryBuildOptions(DataArguments dataArguments, string? rawAllowedOrigins, out OriginValidationOptions? options)
        {
            options = null;
            if (ParseOrigins(rawAllowedOrigins).Count == 0)
                return false;

            options = new OriginValidationOptions(
                BuildAllowedOrigins(dataArguments.PublicUrl, rawAllowedOrigins),
                allowLoopback: true);
            return true;
        }
    }
}
