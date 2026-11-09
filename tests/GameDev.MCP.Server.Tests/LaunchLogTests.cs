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
using com.IvanMurzak.GameDev.MCP.Server.Startup;
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Utils;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>The startup line ends up in <c>docker logs</c>: no secret may reach it.</summary>
    [Collection(LiveServerCollection.Name)] // the env-var cases below must not race a live server's startup
    public class LaunchLogTests
    {
        const string Secret = "s3cret-value";

        [Theory]
        [InlineData("--token", Secret)]
        [InlineData("--token=" + Secret)]
        [InlineData("token", Secret)] // the parser needs no dashes
        [InlineData("--token", "-5" + Secret)] // nor does a value starting with dash-digit end the option
        public void TokenOnTheCommandLine_IsReportedByPresenceOnly(params string[] args)
        {
            Assert.Equal(Secret, new DataArguments(args).Token?.Replace("-5", "")); // else the parser ignored it

            var log = LaunchLog.Describe(new DataArguments(args));

            Assert.DoesNotContain(Secret, log);
            Assert.Contains("\"tokenSet\": true", log, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("--webhook-token", Secret)]
        [InlineData("--webhook-tool-url=https://hooks.example/" + Secret)]
        public void WebhookSecrets_AreNotLogged(params string[] args)
            => Assert.DoesNotContain(Secret, LaunchLog.Describe(new DataArguments(args)));

        [Theory]
        [InlineData(Consts.MCP.Server.Env.Token)]
        [InlineData(Consts.MCP.Server.Env.WebhookToken)]
        public void SecretFromTheEnvironment_IsNotLogged(string variable)
        {
            var before = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, Secret);
            try
            {
                var arguments = new DataArguments(Array.Empty<string>());
                Assert.True(arguments.Token == Secret || arguments.WebhookToken == Secret); // else nothing was read

                Assert.DoesNotContain(Secret, LaunchLog.Describe(arguments));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, before);
            }
        }

        [Fact]
        public void WithoutTokens_PresenceIsFalse_AndOrdinaryOptionsStayReadable()
        {
            var log = LaunchLog.Describe(new DataArguments(new[] { "--port", "6123", "--bind", "any" }));

            Assert.Contains("6123", log);
            Assert.Contains("any", log);
            Assert.Contains("\"tokenSet\": false", log, StringComparison.OrdinalIgnoreCase);
        }
    }
}
