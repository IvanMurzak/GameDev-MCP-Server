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
using com.IvanMurzak.GameDev.MCP.Server.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    public class ErrorReportingTests
    {
        const string Dsn = "https://key@glitchtip.example/1";

        [Fact]
        public void WithoutDsn_ReportingStaysOff()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration[ErrorReporting.DsnEnvVar] = "  ";

            Assert.False(ErrorReporting.TryEnable(builder));
        }

        [Fact]
        public void WithDsn_ReportingIsEnabled()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration[ErrorReporting.DsnEnvVar] = Dsn;

            Assert.True(ErrorReporting.TryEnable(builder));
        }

        [Fact]
        public void Configure_ReportsOnlyUnhandledExceptionsWithoutPii()
        {
            var options = new SentryAspNetCoreOptions();

            ErrorReporting.Configure(options, " " + Dsn + " ", "production");

            Assert.Equal(Dsn, options.Dsn);
            Assert.Equal("production", options.Environment);
            Assert.StartsWith("gamedev-mcp-server@", options.Release);
            Assert.False(options.SendDefaultPii);
            Assert.Equal(RequestSize.None, options.MaxRequestBodySize);
            Assert.Equal(LogLevel.Critical, options.MinimumEventLevel);
            Assert.Equal(LogLevel.Warning, options.MinimumBreadcrumbLevel);
            Assert.Equal(ErrorReporting.ServiceTag, options.DefaultTags["service"]);
        }

        [Fact]
        public void Scrub_StripsRequestDataAndUser()
        {
            var sentryEvent = new SentryEvent(new InvalidOperationException("boom"));
            sentryEvent.Request.Headers["Authorization"] = "Bearer secret";
            sentryEvent.Request.QueryString = "?token=secret";
            sentryEvent.Request.Cookies = "a=b";
            sentryEvent.User = new SentryUser { Id = "user-1", IpAddress = "1.2.3.4" };

            var scrubbed = ErrorReporting.Scrub(sentryEvent);

            Assert.NotNull(scrubbed);
            Assert.Empty(scrubbed!.Request.Headers);
            Assert.Null(scrubbed.Request.QueryString);
            Assert.Null(scrubbed.Request.Cookies);
            Assert.Null(scrubbed.User.Id);
            Assert.Null(scrubbed.User.IpAddress);
        }

        public static IEnumerable<object[]> IgnoredMessages()
        {
            foreach (var marker in ErrorReporting.IgnoredMessageMarkers)
                yield return new object[] { "Error: " + marker + "." };
        }

        [Theory]
        [MemberData(nameof(IgnoredMessages))]
        public void Scrub_DropsUserSideConditions(string message)
        {
            Assert.Null(ErrorReporting.Scrub(new SentryEvent(new InvalidOperationException(message))));
            Assert.Null(ErrorReporting.Scrub(new SentryEvent { Message = message }));
        }
    }
}
