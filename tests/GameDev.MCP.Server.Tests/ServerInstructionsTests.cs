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
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server.Startup;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    public class ServerInstructionsTests
    {
        internal static string TempFile(string text)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mcp-instructions-{Guid.NewGuid():N}.md");
            File.WriteAllText(path, text);
            return path;
        }

        [Fact]
        public void The_argument_wins_over_the_environment()
        {
            var args = new Dictionary<string, string> { [ServerInstructions.ArgName] = " /from/arg.md " };
            Assert.Equal("/from/arg.md", ServerInstructions.ResolvePath(args, _ => "/from/env.md"));
            Assert.Equal("/from/env.md", ServerInstructions.ResolvePath(new Dictionary<string, string>(), _ => "/from/env.md"));
            Assert.Null(ServerInstructions.ResolvePath(new Dictionary<string, string>(), _ => " "));
        }

        [Fact]
        public void Loads_trimmed_text_and_cuts_it_at_the_cap()
        {
            var path = TempFile("  Use the editor tools.\n");
            try
            {
                Assert.Equal("Use the editor tools.", ServerInstructions.Load(path, out var error));
                Assert.Null(error);
                File.WriteAllText(path, new string('x', ServerInstructions.MaxChars + 10));
                Assert.Equal(ServerInstructions.MaxChars, ServerInstructions.Load(path, out _)!.Length);
                File.WriteAllText(path, "   ");
                Assert.Null(ServerInstructions.Load(path, out error));
                Assert.Null(error);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_missing_file_is_no_instructions_with_an_error()
        {
            Assert.Null(ServerInstructions.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.md"), out var error));
            Assert.NotNull(error);
            Assert.Null(ServerInstructions.Load(null, out error));
            Assert.Null(error);
        }
    }

    [Collection(LiveServerCollection.Name)]
    public class ServerInstructionsLiveTests
    {
        static async Task<JsonNode?> Initialize(ServerHarness server)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\","
                    + "\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}", Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            using var response = await server.Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                    return JsonNode.Parse(line.Substring(5));
            }
            return JsonNode.Parse(text);
        }

        [Fact]
        public async Task Initialize_returns_the_instructions_of_the_file()
        {
            var path = ServerInstructionsTests.TempFile("Play-test inside the editor.");
            try
            {
                await using var server = await ServerHarness.StartAsync($"--{ServerInstructions.ArgName}={path}");
                var init = await Initialize(server);
                Assert.Equal("Play-test inside the editor.", init?["result"]?["instructions"]?.GetValue<string>());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task Initialize_has_no_instructions_without_a_file()
        {
            await using var server = await ServerHarness.StartAsync($"--{ServerInstructions.ArgName}=");
            var init = await Initialize(server);
            Assert.NotNull(init?["result"]);
            Assert.Null(init?["result"]?["instructions"]);
        }
    }
}
