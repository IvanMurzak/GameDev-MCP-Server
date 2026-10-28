/*
┌───────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                      │
│  Repository: GitHub (https://github.com/IvanMurzak/GameDev-MCP-Server)    │
│  Copyright (c) 2026 Ivan Murzak                                           │
│  Licensed under the Apache License, Version 2.0.                          │
│  See the LICENSE file in the project root for more information.           │
└───────────────────────────────────────────────────────────────────────────┘
*/

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin.Common.Model;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// A plugin tool's "Visible" flag (McpPlugin's renamed "Enabled") only decides whether tools/list shows it to an MCP
    /// client; tools/call still runs a hidden tool by name.
    /// </summary>
    [Collection(LiveServerCollection.Name)]
    public class ToolVisibilityTests
    {
        static readonly JsonElement EmptySchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();

        [Fact]
        public async Task HiddenTool_IsLeftOutOfToolsList_ButStillRuns()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5");
            editor.Tools = new[]
            {
                new ResponseListTool { Name = "echo", InputSchema = EmptySchema },
                new ResponseListTool { Name = "hidden-tool", Visible = false, InputSchema = EmptySchema },
            };
            await editor.StartAsync();

            var list = await server.McpRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}");
            var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
            Assert.Contains("echo", names);
            Assert.DoesNotContain("hidden-tool", names);

            var (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("hidden-tool", new { }));
            Assert.False(isError, text);
            Assert.Single(editor.Calls, c => c.Name == "hidden-tool");
        }
    }
}
