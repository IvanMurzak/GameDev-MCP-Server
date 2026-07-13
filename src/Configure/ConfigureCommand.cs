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
using System.Linq;
using System.Reflection;
using com.IvanMurzak.McpPlugin.AgentConfig;  // AiAgentConfigurator(Registry|Settings), AiAgentConfig, ConnectionMode, ProjectIdentity, ProjectMarker
using com.IvanMurzak.McpPlugin.Common;       // Consts (+ nested Consts.MCP.Server.AuthOption)
using com.IvanMurzak.McpPlugin.Common.Utils; // ArgsUtils

namespace com.IvanMurzak.GameDev.MCP.Server.Configure
{
    /// <summary>
    /// Terminal subcommand: <c>gamedev-mcp-server configure --agent &lt;id&gt; [--url &lt;url&gt;]
    /// [--transport stdio|http] [--project &lt;path&gt;]</c>.
    /// <para>
    /// Exposes the shared AI-agent configurator registry
    /// (<see cref="AiAgentConfiguratorRegistry"/>) from the terminal (design 06 §"Engine CLIs become
    /// full installers", D12) so agent-first users can write any of the shipped client configs with one
    /// command without touching the editor UI. Every written config is <b>project-scoped and pinned</b>:
    /// the URL carries the D14 <c>/p/&lt;pin&gt;</c> path segment and the D15 deterministic per-project
    /// port, both derived by the shared <see cref="ProjectIdentity"/> from the (normalized) project root
    /// plus the committable <see cref="ProjectMarker"/> (<c>.ai-game-dev/project.json</c>). Credentials
    /// are never written into the config (URL-only default, D11).
    /// </para>
    /// The subcommand resolves and writes the config, then exits WITHOUT starting the server.
    /// </summary>
    public static class ConfigureCommand
    {
        public const string CommandName = "configure";

        private const string DefaultLocalHost = "http://localhost";
        private const string ServerExecutableName = "gamedev-mcp-server";
        private const string DockerImage = "aigamedeveloper/mcp-server";
        private const int DefaultPluginTimeoutMs = 10000;

        /// <summary>True when the process was launched as <c>gamedev-mcp-server configure …</c>.</summary>
        public static bool IsInvocation(string[]? args)
            => args is { Length: > 0 }
               && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The resolved, not-yet-written plan for one <c>configure</c> invocation. Splitting the
        /// resolution from the on-disk write keeps the interesting logic (agent lookup, transport,
        /// connection-mode, pinned URL) unit-testable without mutating any real config file.
        /// </summary>
        public sealed class Plan
        {
            public AiAgentConfigurator Configurator { get; }
            public AgentConfiguratorSettings Settings { get; }
            public AiAgentConfig Config { get; }
            public Consts.MCP.Server.TransportMethod Transport { get; }

            public Plan(
                AiAgentConfigurator configurator,
                AgentConfiguratorSettings settings,
                AiAgentConfig config,
                Consts.MCP.Server.TransportMethod transport)
            {
                Configurator = configurator;
                Settings = settings;
                Config = config;
                Transport = transport;
            }

            public string AgentId => Configurator.AgentId;
            public string AgentName => Configurator.AgentName;
            public string ProjectPin => Settings.ProjectPin;
            public int ResolvedPort => Settings.ResolvedPort;
            public string ConfigPath => Config.ConfigPath;
            public string ExpectedFileContent => Config.ExpectedFileContent;
        }

        /// <summary>Thrown for user-facing configuration errors (unknown agent, bad transport, …).</summary>
        public sealed class ConfigureException : Exception
        {
            public ConfigureException(string message) : base(message) { }
        }

        /// <summary>
        /// Resolve a <see cref="Plan"/> from parsed <c>configure</c> options. Reads the project marker
        /// from disk (for a port override / server target) but writes nothing.
        /// </summary>
        public static Plan BuildPlan(IReadOnlyDictionary<string, string> options, string executableFullPath, string serverVersion)
        {
            var agentId = Get(options, "agent")?.Trim();
            if (string.IsNullOrEmpty(agentId))
                throw new ConfigureException(
                    "configure requires --agent <id>. Available agents: " + string.Join(", ", AiAgentConfiguratorRegistry.GetAgentIds()));

            var configurator = AiAgentConfiguratorRegistry.GetByAgentId(agentId);
            if (configurator == null)
                throw new ConfigureException(
                    $"Unknown --agent '{agentId}'. Available agents: " + string.Join(", ", AiAgentConfiguratorRegistry.GetAgentIds()));

            var transport = ResolveTransport(Get(options, "transport"));

            var projectRoot = ResolveProjectRoot(Get(options, "project"));

            var url = Get(options, "url")?.Trim();
            var host = string.IsNullOrEmpty(url) ? DefaultLocalHost : url;
            var connectionMode = ResolveConnectionMode(host);

            // URL-only default (D11): the golden path never writes a credential into a project file.
            // Local => none (offline), Cloud => oauth (native authorize). An explicit --auth overrides.
            var authOption = ResolveAuthOption(Get(options, "auth"), connectionMode);

            // The deterministic per-project port (D15) comes from ProjectIdentity, sourced from the
            // committable project marker's optional portOverride. Pass it as `port` so the settings'
            // Port matches its derived ResolvedPort/pin exactly.
            var identity = ProjectIdentity.Derive(projectRoot, ProjectMarker.Read(projectRoot));

            var settings = AgentConfiguratorSettings.CreateForHost(
                projectRootPath: projectRoot,
                executableFullPath: executableFullPath,
                port: identity.Port,
                timeoutMs: DefaultPluginTimeoutMs,
                host: host,
                token: null,
                connectionMode: connectionMode,
                authOption: authOption,
                serverExecutableName: ServerExecutableName,
                serverVersion: serverVersion,
                dockerImage: DockerImage);

            var config = transport == Consts.MCP.Server.TransportMethod.stdio
                ? configurator.GetStdioConfig(settings)
                : configurator.GetHttpConfig(settings);

            return new Plan(configurator, settings, config, transport);
        }

        /// <summary>Entry point: parse args, build the plan, write the config, print a summary.</summary>
        public static int Run(string[] args)
        {
            try
            {
                // Drop the leading "configure" token; parse the rest with the same parser DataArguments uses.
                var rest = args.Skip(1).ToArray();
                var options = ArgsUtils.ParseLineArguments(rest);

                if (options.ContainsKey("help") || options.ContainsKey("h"))
                {
                    PrintUsage();
                    return 0;
                }

                var plan = BuildPlan(options, ResolveExecutablePath(), ResolveServerVersion());

                var ok = plan.Config.Configure();
                if (!ok)
                {
                    Console.Error.WriteLine($"Failed to write {plan.AgentName} configuration to {plan.ConfigPath}.");
                    return 1;
                }

                Console.WriteLine($"Configured {plan.AgentName} ({plan.AgentId}).");
                Console.WriteLine($"  transport : {plan.Transport}");
                Console.WriteLine($"  project   : {plan.Settings.ProjectRootPath}");
                Console.WriteLine($"  pin       : {plan.ProjectPin}");
                if (plan.Settings.ConnectionMode == ConnectionMode.Local)
                    Console.WriteLine($"  port      : {plan.ResolvedPort}");
                Console.WriteLine($"  config    : {plan.ConfigPath}");
                return 0;
            }
            catch (ConfigureException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("configure failed: " + ex.Message);
                return 1;
            }
        }

        // --- helpers ------------------------------------------------------------------------------

        private static string? Get(IReadOnlyDictionary<string, string> options, string key)
            => options.TryGetValue(key, out var value) ? value : null;

        internal static Consts.MCP.Server.TransportMethod ResolveTransport(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Consts.MCP.Server.TransportMethod.streamableHttp; // http default

            var trimmed = raw.Trim();
            if (string.Equals(trimmed, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "streamableHttp", StringComparison.OrdinalIgnoreCase))
                return Consts.MCP.Server.TransportMethod.streamableHttp;
            if (string.Equals(trimmed, "stdio", StringComparison.OrdinalIgnoreCase))
                return Consts.MCP.Server.TransportMethod.stdio;

            throw new ConfigureException($"Invalid --transport '{raw}'. Use 'http' or 'stdio'.");
        }

        internal static ConnectionMode ResolveConnectionMode(string host)
        {
            if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !uri.IsLoopback)
                return ConnectionMode.Cloud;
            return ConnectionMode.Local;
        }

        internal static Consts.MCP.Server.AuthOption ResolveAuthOption(string? rawAuth, ConnectionMode connectionMode)
        {
            if (!string.IsNullOrWhiteSpace(rawAuth)
                && Enum.TryParse<Consts.MCP.Server.AuthOption>(rawAuth.Trim(), ignoreCase: true, out var explicitOption)
                && explicitOption != Consts.MCP.Server.AuthOption.unknown)
                return explicitOption;

            return connectionMode == ConnectionMode.Cloud
                ? Consts.MCP.Server.AuthOption.oauth
                : Consts.MCP.Server.AuthOption.none;
        }

        private static string ResolveProjectRoot(string? raw)
        {
            var root = string.IsNullOrWhiteSpace(raw) ? Environment.CurrentDirectory : raw.Trim();
            return Path.GetFullPath(root);
        }

        private static string ResolveExecutablePath()
            => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ServerExecutableName);

        private static string ResolveServerVersion()
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                // Strip any "+<git-sha>" build-metadata suffix.
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "8.0.0";
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: gamedev-mcp-server configure --agent <id> [--url <url>] [--transport stdio|http] [--project <path>]");
            Console.WriteLine();
            Console.WriteLine("Writes a project-scoped, pinned MCP client config for the chosen AI agent.");
            Console.WriteLine("Credentials are never written into the config (URL-only; native authorize).");
            Console.WriteLine();
            Console.WriteLine("Available agents:");
            foreach (var configurator in AiAgentConfiguratorRegistry.All)
                Console.WriteLine($"  {configurator.AgentId,-20} {configurator.AgentName}");
        }
    }
}
