/*
┌───────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                      │
│  Repository: GitHub (https://github.com/IvanMurzak/GameDev-MCP-Server)    │
│  Copyright (c) 2026 Ivan Murzak                                           │
│  Licensed under the Apache License, Version 2.0.                          │
│  See the LICENSE file in the project root for more information.           │
└───────────────────────────────────────────────────────────────────────────┘
*/

using System.Threading;

namespace com.IvanMurzak.GameDev.MCP.Server.Tools
{
    /// <summary>
    /// The caller's cancellation token for the tool call running in this async flow. The library hands a
    /// tool call to <see cref="GuardedToolHub"/> without it (its cancellable entry point is internal), so
    /// the MCP <c>tools/call</c> filter and the HTTP pipeline set it here instead.
    /// </summary>
    public static class ToolCallAmbient
    {
        static readonly AsyncLocal<CancellationToken?> _cancellation = new AsyncLocal<CancellationToken?>();

        public static CancellationToken Cancellation
        {
            get => _cancellation.Value ?? CancellationToken.None;
            set => _cancellation.Value = value;
        }
    }
}
