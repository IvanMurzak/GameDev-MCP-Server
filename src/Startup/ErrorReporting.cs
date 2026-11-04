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
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;

namespace com.IvanMurzak.GameDev.MCP.Server.Startup
{
    /// <summary>
    /// Opt-in crash reporting to a Sentry-compatible endpoint (GlitchTip for ai-game.dev).
    ///
    /// <para>Off unless <see cref="DsnEnvVar"/> is set; the SDK's own <c>SENTRY_DSN</c> is deliberately
    /// not used, so a developer who has it set for something else never reports from a local server.</para>
    ///
    /// <para>Scope: unhandled exceptions only — the ones that escape the request pipeline (an HTTP 500)
    /// or the process. Log lines never become events (<see cref="SentryLoggingOptions.MinimumEventLevel"/>
    /// is Critical) and stay as breadcrumbs: the library logs routine, user-side conditions such as
    /// "No engine plugin is connected" at Error thousands of times a day. Events carry the tag
    /// <c>service=mcp-server</c>, so they can share a project with other services.</para>
    ///
    /// <para>Privacy: no default PII, no request bodies, and <see cref="Scrub"/> removes request headers,
    /// cookies, query strings and the user before an event leaves the process.</para>
    /// </summary>
    public static class ErrorReporting
    {
        public const string DsnEnvVar = "MCP_SENTRY_DSN";
        public const string EnvironmentEnvVar = "MCP_SENTRY_ENVIRONMENT";
        public const string ServiceTag = "mcp-server";

        /// <summary>Messages of user-side conditions that are never worth an event, even as an exception.</summary>
        internal static readonly string[] IgnoredMessageMarkers =
        {
            "No engine plugin is connected",
            "The engine plugin is reconnecting or offline",
        };

        /// <summary>Enables reporting when <see cref="DsnEnvVar"/> is set. Returns whether it did.</summary>
        public static bool TryEnable(WebApplicationBuilder builder)
        {
            var dsn = builder.Configuration[DsnEnvVar];
            if (string.IsNullOrWhiteSpace(dsn))
                return false;

            var environment = builder.Configuration[EnvironmentEnvVar];
            builder.WebHost.UseSentry(options => Configure(options, dsn!, environment));
            return true;
        }

        internal static void Configure(SentryAspNetCoreOptions options, string dsn, string? environment)
        {
            options.Dsn = dsn.Trim();
            if (!string.IsNullOrWhiteSpace(environment))
                options.Environment = environment.Trim();
            options.Release = "gamedev-mcp-server@" + ReleaseVersion();
            options.SendDefaultPii = false;
            options.MaxRequestBodySize = RequestSize.None;
            options.TracesSampleRate = 0;
            options.MinimumEventLevel = LogLevel.Critical;
            options.MinimumBreadcrumbLevel = LogLevel.Information;
            options.DefaultTags["service"] = ServiceTag;
            options.AddExceptionFilterForType<OperationCanceledException>();
            options.SetBeforeSend((sentryEvent, _) => Scrub(sentryEvent));
        }

        /// <summary>Drops ignorable events and strips request data that may carry credentials.</summary>
        internal static SentryEvent? Scrub(SentryEvent sentryEvent)
        {
            if (IsIgnored(sentryEvent.Message?.Formatted ?? sentryEvent.Message?.Message)
                || IsIgnored(sentryEvent.Exception?.Message))
                return null;

            var request = sentryEvent.Request;
            request.Headers.Clear();
            request.Cookies = null;
            request.QueryString = null;
            request.Data = null;
            sentryEvent.User = new SentryUser();
            return sentryEvent;
        }

        static bool IsIgnored(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return false;
            foreach (var marker in IgnoredMessageMarkers)
                if (message.Contains(marker, StringComparison.Ordinal))
                    return true;
            return false;
        }

        static string ReleaseVersion()
        {
            var assembly = typeof(ErrorReporting).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
                return informational!.Split('+')[0]; // drop the source-commit suffix
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }
}
