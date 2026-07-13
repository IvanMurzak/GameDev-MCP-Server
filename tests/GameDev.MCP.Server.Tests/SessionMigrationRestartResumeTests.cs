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
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.GameDev.MCP.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// Restart-resume gate for the Redis session-migration path (design 07 risk row "Session-migration
    /// (Redis) interplay"; issue #70). Models a server-process restart: the durable distributed cache
    /// (Redis in prod) survives, so a FRESH <see cref="SessionMigrationHandler"/> instance — a stand-in
    /// for the restarted process, which has never seen the session in memory — must rehydrate the
    /// session's <see cref="InitializeRequestParams"/> from the shared cache.
    /// </summary>
    public class SessionMigrationRestartResumeTests
    {
        [Fact]
        public async Task PersistThenNewHandlerOnSameCache_RehydratesSession()
        {
            // One cache instance shared by both handlers == the Redis store surviving a restart.
            var sharedCache = new SharedDistributedCache();
            const string sessionId = "session-abc-123";
            var original = NewInitializeParams();

            // Process 1 persists on `initialize`.
            var before = new SessionMigrationHandler(sharedCache, NullLogger<SessionMigrationHandler>.Instance);
            await before.OnSessionInitializedAsync(new DefaultHttpContext(), sessionId, original, CancellationToken.None);

            // Process 2 (restarted — brand-new handler, empty in-memory state) resumes from the cache.
            var afterRestart = new SessionMigrationHandler(sharedCache, NullLogger<SessionMigrationHandler>.Instance);
            var rehydrated = await afterRestart.AllowSessionMigrationAsync(new DefaultHttpContext(), sessionId, CancellationToken.None);

            Assert.NotNull(rehydrated);
            Assert.Equal(original.ProtocolVersion, rehydrated!.ProtocolVersion);
            Assert.Equal(original.ClientInfo!.Name, rehydrated.ClientInfo!.Name);
            Assert.Equal(original.ClientInfo.Version, rehydrated.ClientInfo.Version);
        }

        [Fact]
        public async Task UnknownSessionAfterRestart_ReturnsNull()
        {
            var sharedCache = new SharedDistributedCache();
            var afterRestart = new SessionMigrationHandler(sharedCache, NullLogger<SessionMigrationHandler>.Instance);

            var result = await afterRestart.AllowSessionMigrationAsync(
                new DefaultHttpContext(), sessionId: "never-persisted", CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public async Task MalformedSessionId_RejectedDefensively()
        {
            // Defensive session-ID validation (issue #72): a value outside the accepted charset is never
            // used as a cache key on either the persist or the migrate path.
            var sharedCache = new SharedDistributedCache();
            var handler = new SessionMigrationHandler(sharedCache, NullLogger<SessionMigrationHandler>.Instance);
            const string badSessionId = "../../etc/passwd";

            await handler.OnSessionInitializedAsync(new DefaultHttpContext(), badSessionId, NewInitializeParams(), CancellationToken.None);
            Assert.Equal(0, sharedCache.Count); // nothing persisted for an invalid id

            var result = await handler.AllowSessionMigrationAsync(new DefaultHttpContext(), badSessionId, CancellationToken.None);
            Assert.Null(result);
        }

        private static InitializeRequestParams NewInitializeParams() => new()
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new ClientCapabilities(),
            ClientInfo = new Implementation { Name = "restart-resume-client", Version = "9.0.0" },
        };

        /// <summary>
        /// Dictionary-backed <see cref="IDistributedCache"/> whose contents persist across handler
        /// instances — the test stand-in for a Redis store that outlives a server-process restart.
        /// </summary>
        private sealed class SharedDistributedCache : IDistributedCache
        {
            private readonly Dictionary<string, byte[]> _store = new();

            public int Count => _store.Count;

            public byte[]? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;

            public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

            public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;

            public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            {
                Set(key, value, options);
                return Task.CompletedTask;
            }

            public void Refresh(string key) { }

            public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

            public void Remove(string key) => _store.Remove(key);

            public Task RemoveAsync(string key, CancellationToken token = default)
            {
                _store.Remove(key);
                return Task.CompletedTask;
            }
        }
    }
}
