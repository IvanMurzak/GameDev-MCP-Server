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
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests.Harness
{
    /// <summary>
    /// A minimal engine plugin over a real SignalR connection: the version handshake, a tool list and
    /// scripted tool calls. What an editor or a game build sends is set by <c>environment</c> and the
    /// optional declared <c>client_role</c>.
    /// </summary>
    public sealed class FakePlugin : IAsyncDisposable
    {
        public const string RqlSchema = """
            {"type":"object","properties":{"query":{"type":"string"},"max_targets":{"type":"integer"},"dry_run":{"type":["boolean","null"]}},"required":["query"]}
            """;

        readonly int _port;
        readonly string _environment;
        readonly string? _declaredRole;
        readonly Func<FakePlugin, RequestCallTool, Task<ResponseData<ResponseCallTool>>>? _onCall;
        readonly string _apiVersion;
        readonly CancellationTokenSource _disposed = new CancellationTokenSource();
        HubConnection? _connection;

        public FakePlugin(int port, string name, string environment, string? declaredRole,
            Func<FakePlugin, RequestCallTool, Task<ResponseData<ResponseCallTool>>>? onCall, string? apiVersion = null)
        {
            _apiVersion = apiVersion ?? Consts.ApiVersion;
            _port = port;
            Name = name;
            _environment = environment;
            _declaredRole = declaredRole;
            _onCall = onCall;
        }

        public string Name { get; }

        /// <summary>Cancelled when the plugin is disposed: a handler that "hangs" waits on it, so it never outlives the test.</summary>
        public CancellationToken Disposed => _disposed.Token;
        public ConcurrentQueue<RequestCallTool> Calls { get; } = new ConcurrentQueue<RequestCallTool>();
        public TaskCompletionSource<string?> ForceDisconnected { get; } = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ConnectionId => _connection?.ConnectionId;
        public bool IsConnected => _connection?.State == HubConnectionState.Connected;

        public ResponseListTool[] Tools { get; set; } = new[]
        {
            new ResponseListTool { Name = "echo", InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone() },
            new ResponseListTool { Name = "rql-query", InputSchema = JsonDocument.Parse(RqlSchema).RootElement.Clone() },
        };

        public async Task StartAsync()
        {
            var url = $"http://127.0.0.1:{_port}{Consts.Hub.RemoteApp}";
            if (_declaredRole != null)
                url += "?client_role=" + Uri.EscapeDataString(_declaredRole);

            _connection = new HubConnectionBuilder()
                .WithUrl(url)
                .AddJsonProtocol(options => SignalR_JsonConfiguration.ConfigureJsonSerializer(new Reflector(), options))
                .Build();

            _connection.On<RequestCallTool, ResponseData<ResponseCallTool>>("RunCallTool", async request =>
            {
                Calls.Enqueue(request);
                if (_onCall != null)
                    return await _onCall(this, request);
                return Success(request, $"{Name}:{request.Name}");
            });
            _connection.On<RequestListTool, ResponseData<ResponseListTool[]>>("RunListTool",
                request => ResponseData<ResponseListTool[]>.Success(request.RequestID).SetData(Tools));
            _connection.On<string?>("ForceDisconnect", reason => ForceDisconnected.TrySetResult(reason));
            _connection.On<McpClientData[]>("OnInitialClientData", _ => { });

            await _connection.StartAsync();
            var handshake = await _connection.InvokeAsync<VersionHandshakeResponse>("PerformVersionHandshake", new RequestVersionHandshake
            {
                RequestID = Guid.NewGuid().ToString(),
                ApiVersion = _apiVersion,
                PluginVersion = "0.0.0-test",
                Environment = _environment,
            });
            if (!handshake.Compatible)
                throw new InvalidOperationException("Handshake refused: " + handshake.Message);
        }

        /// <summary>Drops the connection the way a plugin does before a reload.</summary>
        public Task StopAsync() => _connection?.StopAsync() ?? Task.CompletedTask;

        public static ResponseData<ResponseCallTool> Success(RequestCallTool request, string text)
            => ResponseCallTool.Success(text).SetRequestID(request.RequestID).Pack(request.RequestID);

        public static ResponseData<ResponseCallTool> Error(RequestCallTool request, string text, ResponseErrorKind kind, int? status = null)
            => ResponseCallTool.Error(text, kind, status).SetRequestID(request.RequestID).Pack(request.RequestID);

        public async ValueTask DisposeAsync()
        {
            _disposed.Cancel();
            if (_connection != null)
                await _connection.DisposeAsync();
            _disposed.Dispose();
        }
    }

    public static class PollUntil
    {
        public static async Task<bool> TrueAsync(Func<bool> condition, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (condition())
                    return true;
                await Task.Delay(50);
            }
            return condition();
        }
    }
}
