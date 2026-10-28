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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin.Common.Model;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>Tool-call arguments, deadline and plugin reloads (wave-4/5 findings 2-5).</summary>
    [Collection(LiveServerCollection.Name)]
    public class ToolCallGuardTests
    {
        // ───────────────────────────── arguments ─────────────────────────────

        [Fact]
        public async Task MissingRequiredArgument_IsRejectedBeforeThePlugin()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5");
            await editor.StartAsync();

            var (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("rql-query", new { }));
            Assert.True(isError);
            Assert.Equal("[invalid_params] Invalid params for tool 'rql-query': missing required argument 'query' (a string).", text);

            var (status, body) = await server.RestCallAsync("rql-query", "{}");
            Assert.Equal(400, status);
            Assert.Contains("missing required argument 'query'", body!["error"]!.GetValue<string>());

            Assert.DoesNotContain(editor.Calls, c => c.Name == "rql-query");
        }

        [Fact]
        public async Task WrongArgumentTypes_AreRejected_IntegerAndStringThroughOnePath()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5");
            await editor.StartAsync();

            var (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("rql-query", new { query = 123 }));
            Assert.True(isError);
            Assert.Contains("argument 'query' must be a string, got a number (123)", text);

            (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("rql-query", new { query = "x", max_targets = "many" }));
            Assert.True(isError);
            Assert.Contains("argument 'max_targets' must be an integer, got a string", text);

            (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("rql-query", new { query = "x", max_targets = 3 }));
            Assert.False(isError, text);
            Assert.Single(editor.Calls, c => c.Name == "rql-query");
        }

        [Fact]
        public async Task LoneSurrogate_GetsAClearError()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5");
            await editor.StartAsync();

            var response = await server.McpRawAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":{\"name\":\"rql-query\",\"arguments\":{\"query\":\"a\\ud800b\"}}}");
            Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
            var message = response["error"]!["message"]!.GetValue<string>();
            Assert.Contains("[invalid_utf16]", message);
            Assert.Contains("params.arguments.query", message);

            var (status, body) = await server.RestCallAsync("rql-query", "{\"query\":\"a\\udc00\"}");
            Assert.Equal(400, status);
            Assert.Contains("arguments.query", body!["error"]!.GetValue<string>());
            Assert.Empty(editor.Calls);
        }

        // ───────────────────────────── deadline ─────────────────────────────

        [Fact]
        public async Task HungTool_ReturnsATimeoutErrorAtTheToolTimeout()
        {
            await using var server = await ServerHarness.StartAsync("--tool-timeout=1500");
            await using var editor = server.NewPlugin("editor", "Godot 4.5", onCall: async (_, request) =>
            {
                await Task.Delay(Timeout.Infinite); // a blocked engine main thread
                return FakePlugin.Success(request, "never");
            });
            await editor.StartAsync();

            var watch = Stopwatch.StartNew();
            var (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("echo", new { }));
            Assert.True(isError);
            Assert.StartsWith("[tool_timeout] Tool 'echo' did not finish within 1500 ms", text);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        }

        [Fact]
        public async Task DeferredCompletionThatNeverArrives_EndsAtTheSameDeadline()
        {
            await using var server = await ServerHarness.StartAsync("--tool-timeout=1500");
            await using var editor = server.NewPlugin("editor", "Godot 4.5", onCall: (_, request)
                => Task.FromResult(ResponseCallTool.Processing("queued").SetRequestID(request.RequestID).Pack(request.RequestID)));
            await editor.StartAsync();

            var watch = Stopwatch.StartNew();
            var (status, body) = await server.RestCallAsync("echo", "{}");
            Assert.Equal(504, status);
            Assert.StartsWith("[tool_timeout]", body!["error"]!.GetValue<string>());
            // One deadline for the whole call, not one for the send and another for the deferred completion.
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(2900), $"took {watch.Elapsed}");
        }

        // ───────────────────────────── plugin reloads ─────────────────────────────

        const string GodotPendingRefusal =
            "Godot is about to reload the project's C# assembly (the built DLL changed), so this call was not run. " +
            "Retry in a few seconds, once the plugin has reconnected.";

        [Fact]
        public async Task CallRefusedWithoutRunning_IsSentAgainOnceThePluginIsBack()
        {
            await using var server = await ServerHarness.StartAsync();
            FakePlugin? reloaded = null;
            await using var editor = server.NewPlugin("editor", "Godot 4.5", onCall: (plugin, request) =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    await plugin.StopAsync(); // the reload
                    await Task.Delay(300);
                    reloaded = server.NewPlugin("reloaded", "Godot 4.5");
                    await reloaded.StartAsync();
                });
                return Task.FromResult(FakePlugin.Error(request, GodotPendingRefusal, ResponseErrorKind.Unavailable));
            });
            await editor.StartAsync();

            try
            {
                var (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("echo", new { }).WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.False(isError, text);
                Assert.Equal("reloaded:echo", text);
                Assert.Single(editor.Calls);
                Assert.Single(reloaded!.Calls);
            }
            finally
            {
                if (reloaded != null)
                    await reloaded.DisposeAsync();
            }
        }

        [Fact]
        public async Task CallDroppedAfterSending_IsNotResent_AndSaysThePluginIsBack()
        {
            await using var server = await ServerHarness.StartAsync();
            FakePlugin? reloaded = null;
            await using var editor = server.NewPlugin("editor", "Godot 4.5", onCall: async (plugin, request) =>
            {
                _ = Task.Run(async () =>
                {
                    await plugin.StopAsync(); // reload starts while the call runs
                    await Task.Delay(300);
                    reloaded = server.NewPlugin("reloaded", "Godot 4.5");
                    await reloaded.StartAsync();
                });
                await Task.Delay(Timeout.Infinite);
                return FakePlugin.Success(request, "never");
            });
            await editor.StartAsync();

            try
            {
                var (status, body) = await server.RestCallAsync("echo", "{}").WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(502, status);
                Assert.StartsWith("[plugin_reconnected]", body!["error"]!.GetValue<string>());
                Assert.Single(editor.Calls);
                Assert.NotNull(reloaded);
                Assert.Empty(reloaded!.Calls); // never re-run a call that may have run
            }
            finally
            {
                if (reloaded != null)
                    await reloaded.DisposeAsync();
            }
        }
    }
}
