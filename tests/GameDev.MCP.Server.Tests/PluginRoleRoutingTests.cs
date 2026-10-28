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
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Server.Strategy;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// auth=none with an editor and a game (runtime) plugin connected at once (wave-4/5 finding: the game
    /// evicted the editor, and every editor tool call then reached the game). The behaviour comes from
    /// McpPlugin.Server; these tests pin it for this host.
    /// </summary>
    [Collection(LiveServerCollection.Name)]
    public class PluginRoleRoutingTests
    {
        static readonly TimeSpan EvictionWindow = TimeSpan.FromMilliseconds(750);

        /// <summary>True when the server told <paramref name="plugin"/> it was replaced within a short window.</summary>
        static async Task<bool> WasEvicted(FakePlugin plugin)
            => await Task.WhenAny(plugin.ForceDisconnected.Task, Task.Delay(EvictionWindow)) == plugin.ForceDisconnected.Task;

        [Fact]
        public async Task GameFromHandshake_DoesNotEvictEditor_AndToolCallsReachTheEditor()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5.1.stable");
            await editor.StartAsync();
            await using var game = server.NewPlugin("game", "Godot 4.5.1.stable (runtime)");
            await game.StartAsync();

            Assert.False(await WasEvicted(editor), "the game connection disconnected the editor");

            var (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("echo", new { }));
            Assert.False(isError, text);
            Assert.Equal("editor:echo", text);
            Assert.Empty(game.Calls);
        }

        [Fact]
        public async Task DeclaredRuntimeRole_DoesNotEvictEditor()
        {
            // Unity's handshake environment is the same in the editor and a player build: only the declared role tells them apart.
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "6000.0.40f1", declaredRole: "editor");
            await editor.StartAsync();
            await using var game = server.NewPlugin("game", "6000.0.40f1", declaredRole: "runtime");
            await game.StartAsync();

            Assert.False(await WasEvicted(editor));
            var (_, text) = ServerHarness.ToolResult(await server.CallToolAsync("echo", new { }));
            Assert.Equal("editor:echo", text);
        }

        [Fact]
        public async Task DeclaredRole_WinsOverTheHandshakeEnvironment()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5 (runtime)", declaredRole: "editor");
            await editor.StartAsync();
            await using var game = server.NewPlugin("game", "Godot 4.5 (runtime)", declaredRole: "runtime");
            await game.StartAsync();

            Assert.False(await WasEvicted(editor));
            Assert.Equal("editor:echo", ServerHarness.ToolResult(await server.CallToolAsync("echo", new { })).Text);
        }

        [Fact]
        public async Task RefusedHandshake_DoesNotReplaceTheEditor()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5");
            await editor.StartAsync();
            await using var stale = server.NewPlugin("stale", "Godot 4.5", apiVersion: "0.0.0-incompatible");
            await Assert.ThrowsAsync<InvalidOperationException>(stale.StartAsync);

            Assert.False(await WasEvicted(editor));
            Assert.Equal("editor:echo", ServerHarness.ToolResult(await server.CallToolAsync("echo", new { })).Text);
        }

        [Fact]
        public async Task NewerEditor_StillReplacesOlderEditor()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var first = server.NewPlugin("first", "Godot 4.5");
            await first.StartAsync();
            await using var second = server.NewPlugin("second", "Godot 4.5");
            await second.StartAsync();

            await first.ForceDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var (_, text) = ServerHarness.ToolResult(await server.CallToolAsync("echo", new { }));
            Assert.Equal("second:echo", text);
        }

        [Fact]
        public async Task NewerGame_ReplacesOlderGame_NotTheEditor()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5");
            await editor.StartAsync();
            await using var game1 = server.NewPlugin("game1", "Godot 4.5 (runtime)");
            await game1.StartAsync();
            await using var game2 = server.NewPlugin("game2", "Godot 4.5 (runtime)");
            await game2.StartAsync();

            await game1.ForceDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await WasEvicted(editor));
            Assert.Equal("editor:echo", ServerHarness.ToolResult(await server.CallToolAsync("echo", new { })).Text);
            Assert.Empty(game2.Calls);
        }

        [Fact]
        public async Task GameAlone_ServesToolCalls()
        {
            // A standalone game build with no editor: the runtime is the only target.
            await using var server = await ServerHarness.StartAsync();
            await using var game = server.NewPlugin("game", "Godot 4.5 (runtime)");
            await game.StartAsync();

            var (isError, text) = ServerHarness.ToolResult(await server.CallToolAsync("echo", new { }));
            Assert.False(isError, text);
            Assert.Equal("game:echo", text);
        }

        [Fact]
        public async Task CallsDuringEditorReconnect_WaitForTheEditor_NotTheGame()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5");
            await editor.StartAsync();
            await using var game = server.NewPlugin("game", "Godot 4.5 (runtime)");
            await game.StartAsync();
            Assert.Equal("editor:echo", ServerHarness.ToolResult(await server.CallToolAsync("echo", new { })).Text);

            await editor.StopAsync(); // an editor assembly reload
            var registry = Registry(server);
            Assert.True(await PollUntil.TrueAsync(() => registry.WithRole(ClientRole.Editor).Count == 0, TimeSpan.FromSeconds(5)));

            var call = server.CallToolAsync("echo", new { });
            await Task.Delay(500);
            await using var reloaded = server.NewPlugin("reloaded", "Godot 4.5");
            await reloaded.StartAsync();

            var (isError, text) = ServerHarness.ToolResult(await call.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.False(isError, text);
            Assert.Equal("reloaded:echo", text);
            Assert.Empty(game.Calls);
        }

        [Fact]
        public async Task GameConnectingDuringAnEditorReload_DoesNotTakeTheEditorsCalls()
        {
            await using var server = await ServerHarness.StartAsync();
            await using var editor = server.NewPlugin("editor", "Godot 4.5");
            await editor.StartAsync();
            Assert.Equal("editor:echo", ServerHarness.ToolResult(await server.CallToolAsync("echo", new { })).Text);

            await editor.StopAsync();
            var registry = Registry(server);
            Assert.True(await PollUntil.TrueAsync(() => registry.WithRole(ClientRole.Editor).Count == 0, TimeSpan.FromSeconds(5)));

            var call = server.CallToolAsync("echo", new { });
            await using var game = server.NewPlugin("game", "Godot 4.5 (runtime)"); // its role is unknown until its handshake
            await game.StartAsync();
            await Task.Delay(500);
            await using var reloaded = server.NewPlugin("reloaded", "Godot 4.5");
            await reloaded.StartAsync();

            Assert.Equal("reloaded:echo", ServerHarness.ToolResult(await call.WaitAsync(TimeSpan.FromSeconds(30))).Text);
            Assert.Empty(game.Calls);
        }

        static PluginConnectionRegistry Registry(ServerHarness server)
            => ((IPluginRoleStrategy)server.Services.GetService(typeof(IMcpConnectionStrategy))!).Registry;

        [Fact]
        public void EditorLeftWithin_UsesTheRegistryClock()
        {
            var now = DateTimeOffset.UtcNow;
            var registry = new PluginConnectionRegistry(() => now);
            registry.Add("game", ClientRole.Runtime, null);
            registry.Remove("game", out _);
            Assert.False(registry.EditorLeftWithin(TimeSpan.FromSeconds(60))); // a game leaving is not an editor leaving

            registry.Add("editor", ClientRole.Editor, null);
            registry.Remove("editor", out _);
            now += TimeSpan.FromSeconds(59);
            Assert.True(registry.EditorLeftWithin(TimeSpan.FromSeconds(60)));
            now += TimeSpan.FromSeconds(2);
            Assert.False(registry.EditorLeftWithin(TimeSpan.FromSeconds(60)));
        }

        [Fact]
        public void OlderConnectionClassifiedLate_ReplacesItself_NotTheNewerOne()
        {
            var registry = new PluginConnectionRegistry();
            registry.Add("old", null, null);
            Assert.Empty(registry.Add("new", ClientRole.Editor, null));
            Assert.True(registry.TryClassify("old", ClientRole.Editor, out _, out var replaced));
            Assert.Equal(new[] { "old" }, replaced);
            Assert.Equal(new[] { "new" }, registry.WithRole(ClientRole.Editor));
        }

        [Theory]
        [InlineData("Godot 4.5.1.stable (runtime)", ClientRole.Runtime)]
        [InlineData("Godot 4.5.1.stable", ClientRole.Editor)]
        [InlineData("6000.0.40f1", ClientRole.Editor)]
        [InlineData("", ClientRole.Editor)]
        [InlineData(null, ClientRole.Editor)]
        public void RoleFromEnvironment(string? environment, ClientRole expected)
            => Assert.Equal(expected, ClientRoles.FromEnvironment(environment));

        [Theory]
        [InlineData("editor", ClientRole.Editor)]
        [InlineData("Runtime", ClientRole.Runtime)]
        [InlineData("game", ClientRole.Runtime)]
        [InlineData("server", null)]
        [InlineData(null, null)]
        public void ParseRole(string? value, ClientRole? expected)
            => Assert.Equal(expected, ClientRoles.Parse(value));
    }
}
