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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Utils;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests.Harness
{
    /// <summary>
    /// Server tests that run the real host (<see cref="Program.BuildApp"/>) share the library's static
    /// plugin list (<c>ClientUtils</c>), so they never run in parallel.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class LiveServerCollection
    {
        public const string Name = "live-server";
    }

    /// <summary>The real server on a free loopback port, streamable HTTP, auth=none unless overridden.</summary>
    public sealed class ServerHarness : IAsyncDisposable
    {
        readonly WebApplication _app;

        ServerHarness(WebApplication app, int port)
        {
            _app = app;
            Port = port;
            Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(60) };
        }

        public int Port { get; }
        public HttpClient Http { get; }
        public IServiceProvider Services => _app.Services;

        public static async Task<ServerHarness> StartAsync(params string[] extraArgs)
        {
            var port = FreePort();
            var args = new List<string>
            {
                $"--port={port}",
                "--client-transport=streamableHttp",
                "--authorization=none",
            };
            args.AddRange(extraArgs);
            var argArray = args.ToArray();
            var app = Program.BuildApp(argArray, new DataArguments(argArray), httpUnauthenticatedFallback: false, NLog.LogManager.GetLogger("tests"));
            await app.StartAsync();
            return new ServerHarness(app, port);
        }

        public FakePlugin NewPlugin(string name, string environment, string? declaredRole = null,
            Func<FakePlugin, com.IvanMurzak.McpPlugin.Common.Model.RequestCallTool, Task<com.IvanMurzak.McpPlugin.Common.Model.ResponseData<com.IvanMurzak.McpPlugin.Common.Model.ResponseCallTool>>>? onCall = null)
            => new FakePlugin(Port, name, environment, declaredRole, onCall);

        // ───────────────────────────── MCP over streamable HTTP ─────────────────────────────

        string? _sessionId;
        int _nextId = 1;

        /// <summary>Sends a raw JSON-RPC body (the caller writes it, so it can hold invalid escapes) and returns the response object.</summary>
        public async Task<JsonNode> McpRawAsync(string body, CancellationToken cancellationToken = default)
        {
            await EnsureMcpSessionAsync(cancellationToken);
            var response = await PostMcpAsync(body, cancellationToken);
            return await ReadJsonRpcAsync(response, cancellationToken);
        }

        public Task<JsonNode> CallToolAsync(string name, object arguments, CancellationToken cancellationToken = default)
        {
            var id = Interlocked.Increment(ref _nextId);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method = "tools/call",
                @params = new { name, arguments }
            });
            return McpRawAsync(body, cancellationToken);
        }

        /// <summary>The text of a <c>tools/call</c> result, and whether it is an error.</summary>
        public static (bool IsError, string Text) ToolResult(JsonNode response)
        {
            var result = response["result"] ?? throw new Xunit.Sdk.XunitException("No result: " + response.ToJsonString());
            var isError = result["isError"]?.GetValue<bool>() == true;
            var text = string.Join("\n", (result["content"]?.AsArray() ?? new JsonArray())
                .Select(c => c?["text"]?.GetValue<string>()).Where(t => t != null));
            return (isError, text);
        }

        async Task EnsureMcpSessionAsync(CancellationToken cancellationToken)
        {
            if (_sessionId != null)
                return;
            var init = await PostMcpAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "server-tests", version = "1.0" }
                }
            }), cancellationToken);
            init.EnsureSuccessStatusCode();
            _sessionId = init.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;
            await ReadJsonRpcAsync(init, cancellationToken);
            var initialized = await PostMcpAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", cancellationToken);
            initialized.Dispose();
        }

        async Task<HttpResponseMessage> PostMcpAsync(string body, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
            if (_sessionId != null)
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        static async Task<JsonNode> ReadJsonRpcAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                foreach (var line in text.Split('\n'))
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;
                    var node = JsonNode.Parse(line.Substring(5).Trim());
                    if (node?["id"] != null)
                        return node;
                }
                throw new Xunit.Sdk.XunitException($"No JSON-RPC response in the event stream ({(int)response.StatusCode}): {text}");
            }
            return JsonNode.Parse(text) ?? throw new Xunit.Sdk.XunitException($"Empty response ({(int)response.StatusCode}).");
        }

        // ───────────────────────────── REST ─────────────────────────────

        public async Task<(int Status, JsonNode? Body)> RestCallAsync(string name, string jsonBody, CancellationToken cancellationToken = default)
        {
            using var response = await Http.PostAsync($"/api/tools/{name}", new StringContent(jsonBody, Encoding.UTF8, "application/json"), cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return ((int)response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
        }

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
