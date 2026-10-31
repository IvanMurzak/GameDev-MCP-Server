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
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// A tool body that throws, through this host's direct endpoint <c>POST /api/tools/{name}</c>: the response is
    /// built by a real McpPlugin <see cref="RunTool"/> and sent over a real SignalR connection. A tool rejecting what
    /// the agent sent (Unity's script-execute throws <c>Exception("Compilation failed...")</c>) is HTTP 422 with the
    /// tool's message; a fault in the plugin stays 500. The MCP channel shows the same text as an error result.
    /// </summary>
    [Collection(LiveServerCollection.Name)]
    public class ToolBodyFailureStatusTests
    {
        const string CompileError = "Compilation failed: CS1002 ; expected";

        [Fact]
        public async Task ToolRejectingTheRequest_Is422WithTheToolsMessage()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Unity 6", onCall: RealTool(nameof(Tools.CompileFails)));
            await editor.StartAsync();

            var (status, body) = await server.RestCallAsync("echo", "{}");
            Assert.Equal(422, status);
            Assert.Equal("Tool execution failed for 'Execute script': " + CompileError, body!["error"]!.GetValue<string>());

            var (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("echo", new { }));
            Assert.True(isError);
            Assert.Equal("Tool execution failed for 'Execute script': " + CompileError, text);
        }

        [Fact]
        public async Task PluginFault_StaysInternalServerError()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Unity 6", onCall: RealTool(nameof(Tools.Bug)));
            await editor.StartAsync();

            var (status, body) = await server.RestCallAsync("echo", "{}");
            Assert.Equal(500, status);
            Assert.Equal("Tool execution failed for 'Execute script': bug", body!["error"]!.GetValue<string>());
        }

        /// <summary>Answers every call by running <paramref name="method"/> through a real McpPlugin tool manager.</summary>
        static Func<FakePlugin, RequestCallTool, Task<ResponseData<ResponseCallTool>>> RealTool(string method)
        {
            var reflector = new Reflector();
            var tools = new ToolRunnerCollection(reflector, NullLogger.Instance);
            tools["echo"] = RunTool.CreateFromStaticMethod(reflector, NullLogger.Instance, "echo",
                typeof(Tools).GetMethod(method)!, title: "Execute script");
            var manager = new McpToolManager(NullLogger<McpToolManager>.Instance, reflector, tools);
            return (_, request) => manager.RunCallTool(request);
        }

        public static class Tools
        {
            public static string CompileFails() => throw new Exception(CompileError);

            public static string Bug() => throw new NullReferenceException("bug");
        }
    }
}
