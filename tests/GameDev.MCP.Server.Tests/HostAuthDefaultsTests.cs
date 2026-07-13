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
using com.IvanMurzak.McpPlugin.Common;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// Covers the transport-conditional auth default policy (mcp-authorize, design 02 §"modes"):
    /// stdio → none, streamableHttp → oauth-when-configurable-else-none-with-warning, and explicit
    /// choices are always left untouched.
    /// </summary>
    public class HostAuthDefaultsTests
    {
        [Fact]
        public void Stdio_NoExplicitAuth_LeavesAsConfigured()
        {
            var decision = HostAuthDefaults.Decide(
                Consts.MCP.Server.TransportMethod.stdio,
                authExplicitlySet: false,
                hasAuthIssuer: false,
                hasPublicUrl: false);

            Assert.Equal(HostAuthDefaults.Decision.LeaveAsConfigured, decision);
        }

        [Fact]
        public void Http_NoExplicitAuth_IssuerAndPublicUrl_DefaultsToOauth()
        {
            var decision = HostAuthDefaults.Decide(
                Consts.MCP.Server.TransportMethod.streamableHttp,
                authExplicitlySet: false,
                hasAuthIssuer: true,
                hasPublicUrl: true);

            Assert.Equal(HostAuthDefaults.Decision.DefaultHttpToOauth, decision);
        }

        [Theory]
        [InlineData(true, false)]  // issuer only
        [InlineData(false, true)]  // public-url only
        [InlineData(false, false)] // neither
        public void Http_NoExplicitAuth_MissingIssuerOrPublicUrl_FallsBackToNoneWithWarning(bool hasIssuer, bool hasPublicUrl)
        {
            var decision = HostAuthDefaults.Decide(
                Consts.MCP.Server.TransportMethod.streamableHttp,
                authExplicitlySet: false,
                hasAuthIssuer: hasIssuer,
                hasPublicUrl: hasPublicUrl);

            Assert.Equal(HostAuthDefaults.Decision.DefaultHttpToNoneWithWarning, decision);
        }

        [Fact]
        public void Http_ExplicitAuth_LeavesAsConfigured_EvenWithIssuer()
        {
            var decision = HostAuthDefaults.Decide(
                Consts.MCP.Server.TransportMethod.streamableHttp,
                authExplicitlySet: true,
                hasAuthIssuer: true,
                hasPublicUrl: true);

            Assert.Equal(HostAuthDefaults.Decision.LeaveAsConfigured, decision);
        }

        [Fact]
        public void IsAuthExplicitlySet_AuthArg_True()
        {
            Assert.True(HostAuthDefaults.IsAuthExplicitlySet(new[] { "--auth", "oauth" }));
            Assert.True(HostAuthDefaults.IsAuthExplicitlySet(new[] { "--auth=none" }));
            Assert.True(HostAuthDefaults.IsAuthExplicitlySet(new[] { "--authorization", "none" }));
        }

        [Fact]
        public void IsAuthExplicitlySet_NoAuthSignal_False()
        {
            // Guard: no MCP_AUTH / MCP_AUTHORIZATION env leaking in from the test host.
            var savedAuth = Environment.GetEnvironmentVariable(Consts.MCP.Server.Env.Auth);
            var savedAuthorization = Environment.GetEnvironmentVariable(Consts.MCP.Server.Env.Authorization);
            try
            {
                Environment.SetEnvironmentVariable(Consts.MCP.Server.Env.Auth, null);
                Environment.SetEnvironmentVariable(Consts.MCP.Server.Env.Authorization, null);

                Assert.False(HostAuthDefaults.IsAuthExplicitlySet(new[] { "--client-transport", "streamableHttp", "--port", "8080" }));
                Assert.False(HostAuthDefaults.IsAuthExplicitlySet(Array.Empty<string>()));
            }
            finally
            {
                Environment.SetEnvironmentVariable(Consts.MCP.Server.Env.Auth, savedAuth);
                Environment.SetEnvironmentVariable(Consts.MCP.Server.Env.Authorization, savedAuthorization);
            }
        }

        [Fact]
        public void IsAuthExplicitlySet_AuthEnv_True()
        {
            var saved = Environment.GetEnvironmentVariable(Consts.MCP.Server.Env.Auth);
            try
            {
                Environment.SetEnvironmentVariable(Consts.MCP.Server.Env.Auth, "oauth");
                Assert.True(HostAuthDefaults.IsAuthExplicitlySet(Array.Empty<string>()));
            }
            finally
            {
                Environment.SetEnvironmentVariable(Consts.MCP.Server.Env.Auth, saved);
            }
        }
    }
}
