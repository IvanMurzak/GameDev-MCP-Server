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
using com.IvanMurzak.GameDev.MCP.Server.Tests.Harness;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Utils;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>The startup lines end up in <c>docker logs</c>: no secret may reach them.</summary>
    [Collection(LiveServerCollection.Name)] // the env-var cases below must not race a live server's startup
    public class LaunchLogTests
    {
        const string Secret = "s3cret-value";

        static string Log(string[] args)
        {
            var lines = new List<string>();
            LaunchLog.Write(lines.Add, args, new DataArguments(args));
            return string.Join("\n", lines);
        }

        [Theory]
        [InlineData("--token", Secret)]
        [InlineData("--token=" + Secret)]
        [InlineData("--webhook-token", Secret)]
        [InlineData("--webhook-token=" + Secret)]
        [InlineData("--REDIS_URL=redis://:" + Secret + "@redis:6379/0")]
        public void SecretOnTheCommandLine_IsNotLogged(params string[] args)
        {
            var log = Log(args);

            Assert.DoesNotContain(Secret, log);
            Assert.Contains("Launch arguments:", log); // else nothing was looked at
        }

        [Theory]
        [InlineData(Consts.MCP.Server.Env.Token)]
        [InlineData(Consts.MCP.Server.Env.WebhookToken)]
        public void SecretFromTheEnvironment_IsNotLogged(string variable)
        {
            var before = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, Secret);
            try
            {
                var log = Log(Array.Empty<string>());

                Assert.DoesNotContain(Secret, log);
                Assert.Contains("***", log); // the variable was read and masked, not just absent
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, before);
            }
        }

        [Fact]
        public void OrdinaryOptions_StayReadable()
        {
            var log = Log(new[] { "--port", "6123", "--auth=none", "--token", Secret });

            Assert.Contains("--port 6123", log);
            Assert.Contains("--auth=none", log);
            Assert.Contains("--token ***", log);
            Assert.Contains("6123", LaunchLog.RedactParsed(new DataArguments(new[] { "--port", "6123" })));
        }

        [Fact]
        public void SecretOption_WithoutAValue_DoesNotSwallowTheNextOption()
            => Assert.Equal(new[] { "--token", "--port", "6123" }, LaunchLog.RedactArguments(new[] { "--token", "--port", "6123" }));
    }
}
