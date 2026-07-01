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
    /// Covers the issue #209 fix: the migration-record TTL is configurable, defaults to a
    /// window >= the in-memory idle timeout, and is actually applied to the cache entry as an
    /// absolute (not sliding) expiration.
    /// </summary>
    public class SessionMigrationTtlTests
    {
        // ── ResolveTtl: precedence + clamp/floor ────────────────────────────────────────

        [Fact]
        public void ResolveTtl_NeitherEnvSet_UsesTenHourDefault()
        {
            var ttl = SessionMigrationHandler.ResolveTtl(migrationTtlRaw: null, idleTimeoutRaw: null);
            Assert.Equal(SessionMigrationHandler.DefaultSessionTtl, ttl);
            Assert.Equal(TimeSpan.FromHours(10), ttl);
        }

        [Fact]
        public void ResolveTtl_ExplicitOverride_Wins()
        {
            // Explicit override above the idle window wins outright.
            var ttl = SessionMigrationHandler.ResolveTtl(
                migrationTtlRaw: "50000",
                idleTimeoutRaw: "36000");
            Assert.Equal(TimeSpan.FromSeconds(50000), ttl);
        }

        [Fact]
        public void ResolveTtl_NoOverride_FallsBackToIdleTimeout()
        {
            // No explicit override → track the in-memory idle window (10h prod value).
            var ttl = SessionMigrationHandler.ResolveTtl(
                migrationTtlRaw: null,
                idleTimeoutRaw: "36000");
            Assert.Equal(TimeSpan.FromSeconds(36000), ttl);
        }

        [Fact]
        public void ResolveTtl_ExplicitBelowIdleTimeout_IsFlooredAtIdleTimeout()
        {
            // The core issue #209 guard: a too-short explicit override is raised to the idle
            // window so the recovery record can never expire before the in-memory session.
            var ttl = SessionMigrationHandler.ResolveTtl(
                migrationTtlRaw: "7200",   // the old buggy 2h value
                idleTimeoutRaw: "36000");  // 10h in-memory window
            Assert.Equal(TimeSpan.FromSeconds(36000), ttl);
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("   ", null)]
        [InlineData("not-a-number", "also-bad")]
        [InlineData("0", "0")]
        [InlineData("-5", "-100")]
        public void ResolveTtl_MalformedOrNonPositive_TreatedAsUnset(string? migration, string? idle)
        {
            // Garbage / non-positive values fail open to the hard default rather than throwing.
            var ttl = SessionMigrationHandler.ResolveTtl(migration, idle);
            Assert.Equal(SessionMigrationHandler.DefaultSessionTtl, ttl);
        }

        [Fact]
        public void ResolveTtl_MalformedOverride_FallsThroughToIdleTimeout()
        {
            var ttl = SessionMigrationHandler.ResolveTtl(
                migrationTtlRaw: "garbage",
                idleTimeoutRaw: "36000");
            Assert.Equal(TimeSpan.FromSeconds(36000), ttl);
        }

        [Fact]
        public void ResolveTtl_OverrideAboveIdle_NotClampedDown()
        {
            // The floor never lowers a deliberately-larger TTL.
            var ttl = SessionMigrationHandler.ResolveTtl(
                migrationTtlRaw: "72000", // 20h
                idleTimeoutRaw: "36000"); // 10h
            Assert.Equal(TimeSpan.FromSeconds(72000), ttl);
        }

        [Fact]
        public void ResolveTtl_ExplicitEqualsIdleExactly_StaysAtThatValue()
        {
            // Boundary: explicit == idle. The floor uses `<` so an exactly-equal value is not
            // raised; it passes through unchanged (and is not double-counted by the clamp).
            var ttl = SessionMigrationHandler.ResolveTtl(
                migrationTtlRaw: "36000",
                idleTimeoutRaw: "36000");
            Assert.Equal(TimeSpan.FromSeconds(36000), ttl);
        }

        [Fact]
        public void ResolveTtl_PathologicallyLargeOverride_FailsOpenToCeiling()
        {
            // A value that would overflow TimeSpan.FromSeconds must NOT throw (fail-open
            // contract). It is clamped to MaxSessionTtl instead of crashing startup.
            var ttl = SessionMigrationHandler.ResolveTtl(
                migrationTtlRaw: "1000000000000000", // 1e15 s — overflows TimeSpan ticks
                idleTimeoutRaw: "36000");
            Assert.Equal(SessionMigrationHandler.MaxSessionTtl, ttl);
        }

        // ── Handler applies the resolved TTL to its cache entry (absolute, not sliding) ──

        [Fact]
        public async Task OnSessionInitialized_AppliesResolvedTtl_AsAbsoluteExpiration()
        {
            var resolved = SessionMigrationHandler.ResolveTtl(
                migrationTtlRaw: "36000",
                idleTimeoutRaw: "600");
            var recordingCache = new RecordingDistributedCache();
            var handler = new SessionMigrationHandler(
                recordingCache,
                NullLogger<SessionMigrationHandler>.Instance,
                resolved);

            await handler.OnSessionInitializedAsync(
                new DefaultHttpContext(),
                sessionId: "abc123",
                initializeParams: NewInitializeParams(),
                CancellationToken.None);

            Assert.NotNull(recordingCache.LastOptions);
            // Absolute relative TTL is set to exactly the resolved value …
            Assert.Equal(resolved, recordingCache.LastOptions!.AbsoluteExpirationRelativeToNow);
            // … and sliding expiration is NOT used (the issue #209 regression source).
            Assert.Null(recordingCache.LastOptions.SlidingExpiration);
        }

        [Fact]
        public async Task OnSessionInitialized_NonPositiveTtl_FallsBackToDefault()
        {
            // The 3-arg ctor guards against a non-positive TimeSpan by using the default.
            var recordingCache = new RecordingDistributedCache();
            var handler = new SessionMigrationHandler(
                recordingCache,
                NullLogger<SessionMigrationHandler>.Instance,
                TimeSpan.Zero);

            await handler.OnSessionInitializedAsync(
                new DefaultHttpContext(),
                sessionId: "abc123",
                initializeParams: NewInitializeParams(),
                CancellationToken.None);

            Assert.NotNull(recordingCache.LastOptions);
            Assert.Equal(
                SessionMigrationHandler.DefaultSessionTtl,
                recordingCache.LastOptions!.AbsoluteExpirationRelativeToNow);
        }

        private static InitializeRequestParams NewInitializeParams() => new()
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new ClientCapabilities(),
            ClientInfo = new Implementation { Name = "test-client", Version = "1.0.0" },
        };

        /// <summary>
        /// Minimal in-memory <see cref="IDistributedCache"/> test double that records the
        /// <see cref="DistributedCacheEntryOptions"/> passed to <c>SetAsync</c> so the test can
        /// assert the expiration policy without a live Redis.
        /// </summary>
        private sealed class RecordingDistributedCache : IDistributedCache
        {
            private readonly Dictionary<string, byte[]> _store = new();

            public DistributedCacheEntryOptions? LastOptions { get; private set; }

            public byte[]? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;

            public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
                => Task.FromResult(Get(key));

            public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            {
                LastOptions = options;
                _store[key] = value;
            }

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
