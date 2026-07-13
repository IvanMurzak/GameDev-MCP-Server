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
using com.IvanMurzak.GameDev.MCP.Server.Configure;
using com.IvanMurzak.McpPlugin.AgentConfig;
using com.IvanMurzak.McpPlugin.Common;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// Covers the `configure --agent &lt;id&gt;` subcommand (design 06 D12): it resolves a configurator
    /// from the shared registry, derives the D14 pin + D15 port, and writes a project-scoped, pinned,
    /// URL-only config. Claude Code (<c>.mcp.json</c>) and Codex (<c>.codex/config.toml</c>) are both
    /// project-scoped, so the write tests are sandboxed to a temp project root.
    /// </summary>
    public class ConfigureCommandTests
    {
        private const string FakeExe = "/opt/gamedev-mcp-server/gamedev-mcp-server";
        private const string FakeVersion = "9.0.0";

        [Fact]
        public void IsInvocation_ConfigureFirst_True()
        {
            Assert.True(ConfigureCommand.IsInvocation(new[] { "configure", "--agent", "claude-code" }));
            Assert.True(ConfigureCommand.IsInvocation(new[] { "CONFIGURE" }));
        }

        [Fact]
        public void IsInvocation_NotConfigure_False()
        {
            Assert.False(ConfigureCommand.IsInvocation(Array.Empty<string>()));
            Assert.False(ConfigureCommand.IsInvocation(new[] { "--port", "8080" }));
        }

        [Fact]
        public void BuildPlan_ClaudeCodeHttpLocal_WritesPinnedProjectScopedConfig()
        {
            RunInTempProject(projectRoot =>
            {
                var plan = ConfigureCommand.BuildPlan(
                    new Dictionary<string, string> { ["agent"] = "claude-code", ["project"] = projectRoot },
                    FakeExe, FakeVersion);

                Assert.Equal("claude-code", plan.AgentId);
                Assert.Equal(Consts.MCP.Server.TransportMethod.streamableHttp, plan.Transport);
                Assert.Equal(ConnectionMode.Local, plan.Settings.ConnectionMode);

                // D14 pin = 8 hex chars; D15 port in the deterministic 20000-29999 range.
                Assert.Equal(8, plan.ProjectPin.Length);
                Assert.InRange(plan.ResolvedPort, ProjectIdentity.MinPort, ProjectIdentity.MaxPort);

                // Config is project-scoped (.mcp.json under the project root) and carries the pinned URL.
                Assert.Equal(Path.Combine(projectRoot, ".mcp.json"), plan.ConfigPath);
                Assert.Contains("/p/" + plan.ProjectPin, plan.ExpectedFileContent);

                // The write lands in the temp project root (safe) — assert it actually happened.
                Assert.True(plan.Config.Configure());
                Assert.True(File.Exists(plan.ConfigPath));
                Assert.Contains(plan.ProjectPin, File.ReadAllText(plan.ConfigPath));
            });
        }

        [Fact]
        public void BuildPlan_CodexHttpLocal_UsesPinnedTomlConfig()
        {
            RunInTempProject(projectRoot =>
            {
                var plan = ConfigureCommand.BuildPlan(
                    new Dictionary<string, string> { ["agent"] = "codex", ["project"] = projectRoot },
                    FakeExe, FakeVersion);

                Assert.Equal("codex", plan.AgentId);
                Assert.Equal(Path.Combine(projectRoot, ".codex", "config.toml"), plan.ConfigPath);
                Assert.Contains("/p/" + plan.ProjectPin, plan.ExpectedFileContent);

                Assert.True(plan.Config.Configure());
                Assert.True(File.Exists(plan.ConfigPath));
            });
        }

        [Fact]
        public void BuildPlan_RemoteUrl_UsesCloudModeAndPinnedHostedUrl()
        {
            RunInTempProject(projectRoot =>
            {
                var plan = ConfigureCommand.BuildPlan(
                    new Dictionary<string, string> { ["agent"] = "claude-code", ["url"] = "https://ai-game.dev/mcp", ["project"] = projectRoot },
                    FakeExe, FakeVersion);

                Assert.Equal(ConnectionMode.Cloud, plan.Settings.ConnectionMode);
                Assert.Contains("ai-game.dev/mcp/p/" + plan.ProjectPin, plan.ExpectedFileContent);
            });
        }

        [Fact]
        public void BuildPlan_StdioTransport_UsesStdioConfig()
        {
            RunInTempProject(projectRoot =>
            {
                var plan = ConfigureCommand.BuildPlan(
                    new Dictionary<string, string> { ["agent"] = "claude-code", ["transport"] = "stdio", ["project"] = projectRoot },
                    FakeExe, FakeVersion);

                Assert.Equal(Consts.MCP.Server.TransportMethod.stdio, plan.Transport);
            });
        }

        [Fact]
        public void BuildPlan_UnknownAgent_Throws()
        {
            var ex = Assert.Throws<ConfigureCommand.ConfigureException>(() =>
                ConfigureCommand.BuildPlan(
                    new Dictionary<string, string> { ["agent"] = "definitely-not-an-agent" },
                    FakeExe, FakeVersion));
            Assert.Contains("Unknown --agent", ex.Message);
        }

        [Fact]
        public void BuildPlan_MissingAgent_Throws()
        {
            Assert.Throws<ConfigureCommand.ConfigureException>(() =>
                ConfigureCommand.BuildPlan(new Dictionary<string, string>(), FakeExe, FakeVersion));
        }

        // ── pure resolver units ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null, Consts.MCP.Server.TransportMethod.streamableHttp)]
        [InlineData("http", Consts.MCP.Server.TransportMethod.streamableHttp)]
        [InlineData("streamableHttp", Consts.MCP.Server.TransportMethod.streamableHttp)]
        [InlineData("stdio", Consts.MCP.Server.TransportMethod.stdio)]
        public void ResolveTransport_Valid(string? raw, Consts.MCP.Server.TransportMethod expected)
        {
            Assert.Equal(expected, ConfigureCommand.ResolveTransport(raw));
        }

        [Fact]
        public void ResolveTransport_Invalid_Throws()
        {
            Assert.Throws<ConfigureCommand.ConfigureException>(() => ConfigureCommand.ResolveTransport("carrier-pigeon"));
        }

        [Theory]
        [InlineData("http://localhost", ConnectionMode.Local)]
        [InlineData("http://127.0.0.1:8080", ConnectionMode.Local)]
        [InlineData("https://ai-game.dev/mcp", ConnectionMode.Cloud)]
        public void ResolveConnectionMode_Works(string host, ConnectionMode expected)
        {
            Assert.Equal(expected, ConfigureCommand.ResolveConnectionMode(host));
        }

        [Fact]
        public void ResolveAuthOption_DefaultsByMode_ExplicitWins()
        {
            Assert.Equal(Consts.MCP.Server.AuthOption.oauth, ConfigureCommand.ResolveAuthOption(null, ConnectionMode.Cloud));
            Assert.Equal(Consts.MCP.Server.AuthOption.none, ConfigureCommand.ResolveAuthOption(null, ConnectionMode.Local));
            Assert.Equal(Consts.MCP.Server.AuthOption.none, ConfigureCommand.ResolveAuthOption("none", ConnectionMode.Cloud));
        }

        // ── helper: sandbox the process cwd + writes to a throwaway project dir ────────────

        private static void RunInTempProject(Action<string> body)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gdmcp-configure-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(projectRoot);
            try
            {
                body(projectRoot);
            }
            finally
            {
                try { Directory.Delete(projectRoot, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
