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
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Utils;

namespace com.IvanMurzak.GameDev.MCP.Server.Startup
{
    /// <summary>
    /// Host-level policy for the transport-conditional authentication default introduced with the
    /// mcp-authorize redesign (design 02 §"GameDev-MCP-Server modes"): <b>stdio defaults to
    /// <c>none</c></b> (offline / local dev / CI) and <b>streamableHttp defaults to <c>oauth</c></b>
    /// (the authenticated resource-server path).
    /// <para>
    /// The shared <see cref="DataArguments"/> parser defaults <see cref="DataArguments.Authorization"/>
    /// to <see cref="Consts.MCP.Server.AuthOption.none"/> for BOTH transports (it cannot know the
    /// host's default policy), so this class decides whether the host should upgrade an unset http auth
    /// to <c>oauth</c>. It is a pure decision so the policy is unit-testable without booting Kestrel.
    /// </para>
    /// <para>
    /// Guard rail: <c>oauth</c> mode requires <c>--auth-issuer</c> AND <c>--public-url</c> — the shared
    /// library's <c>AccountMcpStrategy.Validate</c> throws at startup when either is missing. So the
    /// host defaults http to <c>oauth</c> ONLY when both are present; otherwise it leaves http on
    /// <c>none</c> and emits a loud warning rather than crashing the documented bare-run quickstart
    /// (<c>docker run … aigamedeveloper/mcp-server</c> / <c>gamedev-mcp-server --port 8080</c>). An
    /// explicit <c>--auth</c>/<c>--authorization</c> (or <c>MCP_AUTH</c>/<c>MCP_AUTHORIZATION</c>) always
    /// wins and is left untouched.
    /// </para>
    /// </summary>
    public static class HostAuthDefaults
    {
        public enum Decision
        {
            /// <summary>Explicit auth was given, or transport is stdio — leave DataArguments' value as-is.</summary>
            LeaveAsConfigured,

            /// <summary>Http with issuer + public-url present — seed the http default of <c>oauth</c>.</summary>
            DefaultHttpToOauth,

            /// <summary>Http but oauth is not configurable — stay on <c>none</c> and warn the operator.</summary>
            DefaultHttpToNoneWithWarning
        }

        /// <summary>
        /// True when the operator explicitly chose an auth mode via env (<c>MCP_AUTH</c> /
        /// <c>MCP_AUTHORIZATION</c>) or CLI (<c>--auth</c> / <c>--authorization</c>). The library
        /// arg parser (<see cref="ArgsUtils.ParseLineArguments"/>) is reused so key detection matches
        /// exactly how <see cref="DataArguments"/> itself reads the args.
        /// </summary>
        public static bool IsAuthExplicitlySet(string[] args)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Consts.MCP.Server.Env.Auth)))
                return true;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Consts.MCP.Server.Env.Authorization)))
                return true;

            var parsed = ArgsUtils.ParseLineArguments(args ?? Array.Empty<string>());
            return parsed.ContainsKey(Consts.MCP.Server.Args.Auth)
                || parsed.ContainsKey(Consts.MCP.Server.Args.Authorization);
        }

        /// <summary>
        /// Decide the host's auth default for the resolved transport. See the class remarks for the
        /// full policy. Pure — no side effects.
        /// </summary>
        public static Decision Decide(
            Consts.MCP.Server.TransportMethod transport,
            bool authExplicitlySet,
            bool hasAuthIssuer,
            bool hasPublicUrl)
        {
            if (authExplicitlySet)
                return Decision.LeaveAsConfigured;

            // stdio (and the defensive unknown case) keep DataArguments' `none` default.
            if (transport != Consts.MCP.Server.TransportMethod.streamableHttp)
                return Decision.LeaveAsConfigured;

            return (hasAuthIssuer && hasPublicUrl)
                ? Decision.DefaultHttpToOauth
                : Decision.DefaultHttpToNoneWithWarning;
        }
    }
}
