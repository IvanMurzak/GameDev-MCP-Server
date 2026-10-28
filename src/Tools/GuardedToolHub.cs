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
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Hub.Client;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.McpPlugin.Server.Auth;
using com.IvanMurzak.McpPlugin.Server.Strategy;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.GameDev.MCP.Server.Tools
{
    /// <summary>
    /// Wraps the library's tool runner (MCP <c>tools/call</c> and the REST <c>/api/tools/{name}</c> both
    /// reach it) with the checks the library does not make:
    /// <list type="number">
    ///   <item><b>Arguments.</b> Lone UTF-16 surrogates and arguments that do not fit the tool's input
    ///   schema (<see cref="ToolArgumentValidator"/>) are refused before the call is sent.</item>
    ///   <item><b>One deadline.</b> The whole call, including waiting for a reconnecting plugin and a
    ///   deferred ("processing") completion, ends with a clear error after the tool timeout.</item>
    ///   <item><b>Plugin reloads.</b> A call the plugin refused WITHOUT running it (a reload gate) is sent
    ///   again once the plugin is back, within the reconnect wait. A call whose connection dropped after it
    ///   was sent is never sent again (it may have run); the error says so, and whether the plugin came
    ///   back, so the agent can check and retry.</item>
    /// </list>
    /// </summary>
    public sealed class GuardedToolHub : IClientToolHub
    {
        /// <summary>HTTP status the library gives a tool call that failed after it may have reached the plugin.</summary>
        internal const int MaybeDeliveredStatusCode = 502;

        /// <summary>Text a plugin's reload gate puts in a refusal of a call it did not run (Godot-MCP today).</summary>
        internal const string NotRunMarker = "this call was not run";

        static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>A refused call is sent again at least this often while the plugin stays connected.</summary>
        static readonly TimeSpan RefusalRetryInterval = TimeSpan.FromSeconds(2);

        /// <summary>After a dropped call, a target that is still the same connection this long is taken as never having left.</summary>
        static readonly TimeSpan SameConnectionSettle = TimeSpan.FromSeconds(2);

        readonly IClientToolHub _inner;
        readonly Func<RequestCallTool, CancellationToken, Task<ResponseData<ResponseCallTool>>> _call;
        readonly IMcpConnectionStrategy _strategy;
        readonly ILogger _logger;
        readonly ConcurrentDictionary<string, JsonElement> _schemas = new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal);

        public GuardedToolHub(
            IClientToolHub inner,
            Func<RequestCallTool, CancellationToken, Task<ResponseData<ResponseCallTool>>> call,
            IMcpConnectionStrategy strategy,
            TimeSpan toolTimeout,
            TimeSpan reconnectWait,
            ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _call = call ?? throw new ArgumentNullException(nameof(call));
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ToolTimeout = toolTimeout > TimeSpan.Zero ? toolTimeout : TimeSpan.FromMinutes(5);
            ReconnectWait = reconnectWait < TimeSpan.Zero ? TimeSpan.Zero : reconnectWait;
        }

        /// <summary>The longest a whole tool call may take (the server's tool timeout).</summary>
        public TimeSpan ToolTimeout { get; }

        /// <summary>The longest the hub waits for a reloading plugin to come back.</summary>
        public TimeSpan ReconnectWait { get; }

        public async Task<ResponseData<ResponseListTool[]>> RunListTool(RequestListTool request, CancellationToken cancellationToken = default)
        {
            var response = await _inner.RunListTool(request, cancellationToken);
            Remember(response);
            return response;
        }

        public async Task<ResponseData<ResponseCallTool>> RunCallTool(RequestCallTool request)
        {
            var callerToken = ToolCallAmbient.Cancellation;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            deadline.CancelAfter(ToolTimeout);
            var elapsed = Stopwatch.StartNew();

            try
            {
                var invalid = FindInvalidText(request) ?? await ValidateArgumentsAsync(request, deadline.Token);
                if (invalid != null)
                    return Fail(request, invalid, ResponseErrorKind.BadRequest, 400);

                var reconnectDeadline = elapsed.Elapsed + ReconnectWait;
                while (true)
                {
                    var target = CurrentTarget();
                    var response = await _call(request, deadline.Token);

                    if (IsServerTimeout(response))
                        return TimedOut(request, response.Message);

                    if (IsNotRunRefusal(response))
                    {
                        if (elapsed.Elapsed >= reconnectDeadline)
                            return Fail(request,
                                $"[plugin_reloading] The engine plugin kept refusing '{request.Name}' for {ReconnectWait.TotalSeconds:0} s " +
                                $"because it is reloading; the call was not run. Retry once the editor has finished reloading. Plugin: {response.Message}",
                                ResponseErrorKind.Unavailable, 503);
                        _logger.LogInformation("Tool '{tool}' was refused by the reloading plugin without running; sending it again once the plugin is back.", request.Name);
                        await WaitForReconnectAsync(target, RefusalRetryInterval, deadline.Token);
                        continue;
                    }

                    if (IsMaybeDelivered(response))
                    {
                        var window = ReconnectWait < ToolTimeout - elapsed.Elapsed ? ReconnectWait : ToolTimeout - elapsed.Elapsed;
                        var back = await WaitForReconnectAsync(target, window, deadline.Token);
                        return Fail(request, back
                            ? $"[plugin_reconnected] The engine plugin disconnected while running '{request.Name}' (usually a script or " +
                              "assembly reload) and is connected again. The call was not re-sent because it may already have run: " +
                              "check its effect, then retry it if it did not happen (a read-only call can be retried at once)."
                            : $"[plugin_disconnected] The engine plugin disconnected while running '{request.Name}' and did not reconnect " +
                              $"within {window.TotalSeconds:0} s. The call may already have run. Check that the editor is open and connected, then retry.",
                            ResponseErrorKind.Unavailable, MaybeDeliveredStatusCode);
                    }

                    return response;
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !callerToken.IsCancellationRequested)
            {
                return TimedOut(request, null);
            }
        }

        // ───────────────────────────── arguments ─────────────────────────────

        async Task<string?> ValidateArgumentsAsync(RequestCallTool request, CancellationToken cancellationToken)
        {
            var fresh = false;
            if (!_schemas.TryGetValue(request.Name, out var schema))
            {
                await RefreshSchemasAsync(cancellationToken);
                fresh = true;
                if (!_schemas.TryGetValue(request.Name, out schema))
                    return null; // unknown tool: the plugin answers that itself
            }

            var problem = ToolArgumentValidator.Validate(request.Name, schema, request.Arguments);
            if (problem == null || fresh)
                return problem;

            // A remembered schema may predate a plugin reload: check against the current one before refusing.
            await RefreshSchemasAsync(cancellationToken);
            return _schemas.TryGetValue(request.Name, out schema)
                ? ToolArgumentValidator.Validate(request.Name, schema, request.Arguments)
                : null;
        }

        async Task RefreshSchemasAsync(CancellationToken cancellationToken)
        {
            try
            {
                Remember(await _inner.RunListTool(new RequestListTool(), cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Validation is best effort: without a schema the plugin checks the arguments itself.
                _logger.LogDebug(ex, "Could not list tools to validate arguments.");
            }
        }

        void Remember(ResponseData<ResponseListTool[]>? response)
        {
            if (response?.Status != ResponseStatus.Success || response.Value == null)
                return;
            foreach (var tool in response.Value)
            {
                if (tool?.Name == null)
                    continue;
                if (tool.InputSchema is JsonElement schema && schema.ValueKind == JsonValueKind.Object)
                    _schemas[tool.Name] = schema.Clone();
                else
                    _schemas.TryRemove(tool.Name, out _);
            }
        }

        /// <summary>An error for the first argument (or the tool name) holding a lone UTF-16 surrogate, or null.</summary>
        internal static string? FindInvalidText(RequestCallTool request)
        {
            if (Utf16Validation.HasLoneSurrogate(request.Name))
                return Utf16Validation.Describe("name");
            if (request.Arguments == null)
                return null;
            foreach (var argument in request.Arguments)
            {
                var path = Utf16Validation.HasLoneSurrogate(argument.Key)
                    ? argument.Key
                    : Utf16Validation.Find(argument.Value, argument.Key);
                if (path != null)
                    return Utf16Validation.Describe($"arguments.{path}") + $" (tool '{request.Name}')";
            }
            return null;
        }

        // ───────────────────────────── reloads ─────────────────────────────

        /// <summary>The library's own timeouts (no answer, or no deferred completion); a tool's own timeout error is left alone.</summary>
        static bool IsServerTimeout(ResponseData<ResponseCallTool> response)
            => response.Status == ResponseStatus.Error
                && response.ErrorKind == ResponseErrorKind.Timeout
                && response.Message?.IndexOf("not re-sent", StringComparison.Ordinal) >= 0;

        static bool IsNotRunRefusal(ResponseData<ResponseCallTool> response)
            => response.Status == ResponseStatus.Error
                && response.ErrorKind == ResponseErrorKind.Unavailable
                && response.HttpStatusCode != MaybeDeliveredStatusCode
                && (response.HttpStatusCode == 503
                    || response.Message?.IndexOf(NotRunMarker, StringComparison.OrdinalIgnoreCase) >= 0);

        static bool IsMaybeDelivered(ResponseData<ResponseCallTool> response)
            => response.Status == ResponseStatus.Error
                && response.ErrorKind == ResponseErrorKind.Unavailable
                && response.HttpStatusCode == MaybeDeliveredStatusCode;

        string? CurrentTarget() => _strategy.ResolveConnectionId(McpSessionTokenContext.CurrentToken, 0);

        /// <summary>
        /// Waits up to <paramref name="window"/> for the plugin to be connected through a different connection
        /// than <paramref name="previous"/> (or at all, when there was none). A connection that is still
        /// <paramref name="previous"/> after <see cref="SameConnectionSettle"/> never left: true as well.
        /// </summary>
        async Task<bool> WaitForReconnectAsync(string? previous, TimeSpan window, CancellationToken cancellationToken)
        {
            var waited = Stopwatch.StartNew();
            while (true)
            {
                var current = CurrentTarget();
                if (current != null && (previous == null || !string.Equals(current, previous, StringComparison.Ordinal)))
                    return true;
                if (waited.Elapsed >= window)
                    return false;
                if (current != null && waited.Elapsed >= SameConnectionSettle)
                    return true;
                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        // ───────────────────────────── errors ─────────────────────────────

        ResponseData<ResponseCallTool> TimedOut(RequestCallTool request, string? detail)
        {
            var message = $"[tool_timeout] Tool '{request.Name}' did not finish within {ToolTimeout.TotalMilliseconds:0} ms (the server's tool timeout). " +
                "It was not re-sent; the engine may still be running it, or its main thread may be blocked (a modal dialog, " +
                "an endless loop, a long import). Raise the limit with --tool-timeout=<ms> or MCP_PLUGIN_TOOL_TIMEOUT.";
            if (!string.IsNullOrEmpty(detail))
                message += " Detail: " + detail;
            return Fail(request, message, ResponseErrorKind.Timeout, 504);
        }

        ResponseData<ResponseCallTool> Fail(RequestCallTool request, string message, ResponseErrorKind kind, int httpStatusCode)
        {
            _logger.LogWarning("Tool '{tool}' ({requestId}): {message}", request.Name, request.RequestID, message);
            return ResponseCallTool.Error(message, kind, httpStatusCode).Pack(request.RequestID);
        }
    }
}
