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
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace com.IvanMurzak.GameDev.MCP.Server
{
    /// <summary>
    /// Persists MCP session <see cref="InitializeRequestParams"/> to a shared
    /// <see cref="IDistributedCache"/> so a fresh server process can rehydrate a session
    /// whose ID it has never seen in memory. Closes the "lose session on restart" gap.
    /// See https://github.com/IvanMurzak/AI-Game-Dev-Server/issues/70 (Option B).
    /// Final Redis key is <c>{InstanceName}{KeyPrefix}{sessionId}</c>, e.g.
    /// <c>mcp-server:mcp:session:&lt;sessionId&gt;</c>.
    /// </summary>
    public sealed class SessionMigrationHandler : ISessionMigrationHandler
    {
        // Floor for the migration-record TTL when neither env var is set. Chosen to be >=
        // the in-memory idle window deployed in prod (MCP_PLUGIN_IDLE_TIMEOUT_SECONDS=36000
        // = 10h on the VPS). A recovery record that outlives the in-memory session by a
        // comfortable margin guarantees a restart can always rehydrate any session the
        // in-memory layer still considers live. See issue #209.
        public static readonly TimeSpan DefaultSessionTtl = TimeSpan.FromHours(10);

        // Sane ceiling for a configured TTL. A pathological value (e.g. 1e15 seconds)
        // would overflow TimeSpan.FromSeconds and throw OverflowException, which is NOT
        // caught here and would crash startup — violating the fail-open contract. Any
        // value above this cap is clamped down to it instead of throwing. 30 days is far
        // longer than any plausible continuous session yet well inside TimeSpan's range.
        public static readonly TimeSpan MaxSessionTtl = TimeSpan.FromDays(30);

        // Env var names this handler resolves its TTL from. Kept here (rather than only in
        // Program.cs) so the resolution helper and its tests share one source of truth.
        public const string MigrationTtlEnvVar = "MCP_SESSION_MIGRATION_TTL_SECONDS";
        public const string IdleTimeoutEnvVar = "MCP_PLUGIN_IDLE_TIMEOUT_SECONDS";

        private const string KeyPrefix = "mcp:session:";

        // Defence-in-depth: validate Mcp-Session-Id format ourselves before using it as
        // a Redis key suffix, instead of trusting the upstream MCP SDK alone (issue #72).
        // Pattern intentionally narrower than the SDK accepts — covers Guid.NewGuid()
        // ("N" = 32 hex chars; "D" = 36 with hyphens) and base64url-style IDs, blocks
        // separators, control chars, and oversized payloads that could bloat Redis or
        // confuse namespace-scanning tooling (KEYS / SCAN / dashboards).
        [StringSyntax(StringSyntaxAttribute.Regex)]
        private const string SessionIdPatternText = @"^[A-Za-z0-9_\-]{1,128}$";

        // Explicit ReDoS belt: the current charset class is linear, but a small timeout
        // keeps the contract intact if the pattern is ever broadened.
        private static readonly Regex SessionIdPattern = new(
            SessionIdPatternText,
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(50));

        // Canonical log message for failed defensive session-ID validation. Distinguished
        // per call site by the structured {Phase} property (Init|Migration) so a single
        // alert/grep query covers both paths.
        private const string SessionIdValidationFailedMessage =
            "Rejecting MCP session: session ID failed defensive format validation. Phase={Phase}, SessionIdLength={SessionIdLength}";

        private static bool IsValidSessionId(string? sessionId)
            => !string.IsNullOrEmpty(sessionId) && SessionIdPattern.IsMatch(sessionId);

        private readonly IDistributedCache _cache;
        private readonly ILogger<SessionMigrationHandler> _logger;
        private readonly TimeSpan _sessionTtl;
        private readonly DistributedCacheEntryOptions _cacheEntryOptions;

        public SessionMigrationHandler(
            IDistributedCache cache,
            ILogger<SessionMigrationHandler> logger)
            : this(cache, logger, DefaultSessionTtl)
        {
        }

        public SessionMigrationHandler(
            IDistributedCache cache,
            ILogger<SessionMigrationHandler> logger,
            TimeSpan sessionTtl)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sessionTtl = sessionTtl > TimeSpan.Zero ? sessionTtl : DefaultSessionTtl;
            // ABSOLUTE (relative-to-now) expiration, deliberately NOT sliding. The previous
            // implementation used SlidingExpiration on the assumption that an active client
            // would refresh the window on every reconnect — that assumption was FALSE for this
            // access pattern (issue #209). The hot path keeps the session in memory and never
            // calls IDistributedCache.GetAsync for a live session, so the sliding window never
            // rolled; it just counted down from `initialize`. A 2h sliding window therefore
            // expired the recovery record while the in-memory session (idle window up to 10h)
            // was still alive, so a restart 404'd every session older than 2h.
            //
            // The fix: a single absolute TTL sized to cover the longest expected continuous
            // session (>= the in-memory idle window, see ResolveTtl). The record is written
            // once at `initialize` and lives for the full window regardless of reconnect
            // traffic — simpler and correct, because rolling the window on the hot path would
            // require a redundant cache read we deliberately avoid.
            _cacheEntryOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _sessionTtl,
            };
        }

        /// <summary>
        /// Pure resolution of the migration-record TTL from raw env-var string values, so the
        /// policy is unit-testable without Redis or a live <see cref="IConfiguration"/>.
        /// Precedence:
        /// <list type="number">
        ///   <item><see cref="MigrationTtlEnvVar"/> (<c>MCP_SESSION_MIGRATION_TTL_SECONDS</c>), if a positive integer — it wins outright.</item>
        ///   <item>else <see cref="IdleTimeoutEnvVar"/> (<c>MCP_PLUGIN_IDLE_TIMEOUT_SECONDS</c>), if a positive integer — the recovery window then tracks the in-memory idle window.</item>
        ///   <item>else <see cref="DefaultSessionTtl"/> (10h).</item>
        /// </list>
        /// In all cases the result is floored at the resolved idle timeout (when that is a
        /// positive integer) so the recovery window can NEVER be shorter than the in-memory
        /// session lifetime — that shorter-window state is exactly the bug in issue #209.
        /// Malformed / non-positive values are ignored (treated as unset) rather than throwing,
        /// to fail open: a bad override must not crash startup. A pathologically large value is
        /// clamped to <see cref="MaxSessionTtl"/> for the same reason (the raw conversion would
        /// otherwise overflow <see cref="TimeSpan"/> and throw).
        /// </summary>
        /// <param name="migrationTtlRaw">Raw value of <c>MCP_SESSION_MIGRATION_TTL_SECONDS</c> (may be null/empty/garbage).</param>
        /// <param name="idleTimeoutRaw">Raw value of <c>MCP_PLUGIN_IDLE_TIMEOUT_SECONDS</c> (may be null/empty/garbage).</param>
        public static TimeSpan ResolveTtl(string? migrationTtlRaw, string? idleTimeoutRaw)
        {
            var explicitTtl = ParsePositiveSeconds(migrationTtlRaw);
            var idleTimeout = ParsePositiveSeconds(idleTimeoutRaw);

            // Base TTL: explicit override > idle-timeout fallback > hard default.
            var ttl = explicitTtl ?? idleTimeout ?? DefaultSessionTtl;

            // Floor at the idle timeout (when known) so the recovery window can never be
            // shorter than the in-memory session lifetime — the issue #209 regression.
            if (idleTimeout is { } floor && ttl < floor)
                ttl = floor;

            return ttl;
        }

        /// <summary>
        /// Parse a string as a strictly-positive whole number of seconds into a
        /// <see cref="TimeSpan"/>. Returns null for null/empty/non-numeric/&lt;=0 input so the
        /// caller can treat any malformed value as "unset" and fall through to the next source.
        /// </summary>
        private static TimeSpan? ParsePositiveSeconds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return null;
            if (seconds <= 0)
                return null;
            // Clamp before converting: TimeSpan.FromSeconds overflows (and throws) for very
            // large inputs, so cap at MaxSessionTtl to keep the parse fail-open and total.
            if (seconds >= (long)MaxSessionTtl.TotalSeconds)
                return MaxSessionTtl;
            return TimeSpan.FromSeconds(seconds);
        }

        public async ValueTask OnSessionInitializedAsync(
            HttpContext context,
            string sessionId,
            InitializeRequestParams initializeParams,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            if (!IsValidSessionId(sessionId))
            {
                // Length-only log — never echo the raw ID, per issue #72 acceptance criteria.
                _logger.LogWarning(
                    SessionIdValidationFailedMessage,
                    "Init",
                    sessionId.Length);
                return;
            }

            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(
                    initializeParams,
                    McpJsonUtilities.DefaultOptions);

                await _cache.SetAsync(BuildKey(sessionId), payload, _cacheEntryOptions, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "Persisted MCP session init params to distributed cache. SessionId={SessionId}, TtlSeconds={TtlSeconds}, Bytes={Bytes}",
                    sessionId, (long)_sessionTtl.TotalSeconds, payload.Length);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Persistence failure must NOT break the live session — worst case is this
                // session can't migrate after a restart (same as before this handler existed).
                _logger.LogWarning(ex,
                    "Failed to persist MCP session init params for migration. SessionId={SessionId}",
                    sessionId);
            }
        }

        public async ValueTask<InitializeRequestParams?> AllowSessionMigrationAsync(
            HttpContext context,
            string sessionId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;

            if (!IsValidSessionId(sessionId))
            {
                _logger.LogWarning(
                    SessionIdValidationFailedMessage,
                    "Migration",
                    sessionId.Length);
                return null;
            }

            byte[]? payload;
            try
            {
                // Read the persisted init params. The TTL is absolute (see ctor) — this read
                // does NOT extend it, and intentionally so: the record is sized to outlive the
                // longest expected session, so there is no window to refresh here.
                payload = await _cache.GetAsync(BuildKey(sessionId), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Cache outage: we can't tell real from forged, so reject migration.
                // Client gets 404 and re-initializes — same as if no handler existed.
                _logger.LogWarning(ex,
                    "Distributed cache lookup failed during MCP session migration; rejecting reconnect. SessionId={SessionId}",
                    sessionId);
                return null;
            }

            if (payload == null || payload.Length == 0)
            {
                _logger.LogDebug(
                    "No persisted init params found for unrecognised MCP session ID. SessionId={SessionId}",
                    sessionId);
                return null;
            }

            try
            {
                var initializeParams = JsonSerializer.Deserialize<InitializeRequestParams>(
                    payload,
                    McpJsonUtilities.DefaultOptions);
                if (initializeParams == null)
                {
                    _logger.LogWarning(
                        "Deserialised init params were null; rejecting MCP session migration. SessionId={SessionId}",
                        sessionId);
                    return null;
                }

                _logger.LogInformation(
                    "Rehydrated MCP session from distributed cache after process restart. SessionId={SessionId}",
                    sessionId);
                return initializeParams;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deserialise persisted MCP init params; rejecting migration. SessionId={SessionId}",
                    sessionId);
                return null;
            }
        }

        private static string BuildKey(string sessionId)
            => KeyPrefix + sessionId;
    }
}
