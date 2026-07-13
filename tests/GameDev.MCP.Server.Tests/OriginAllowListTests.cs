/*
┌───────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                      │
│  Repository: GitHub (https://github.com/IvanMurzak)                       │
│  Copyright (c) 2026 Ivan Murzak                                           │
│  Licensed under the Apache License, Version 2.0.                          │
│  See the LICENSE file in the project root for more information.           │
└───────────────────────────────────────────────────────────────────────────┘
*/

using System.Linq;
using com.IvanMurzak.GameDev.MCP.Server.Startup;
using com.IvanMurzak.McpPlugin.Common.Utils;
using Xunit;

namespace com.IvanMurzak.GameDev.MCP.Server.Tests
{
    /// <summary>
    /// Covers the configurable Origin allow-list host surface (b2 carry-forward): parsing +
    /// normalization + merge with the public-url origin, and that the replacement options are only
    /// built when there is something to add.
    /// </summary>
    public class OriginAllowListTests
    {
        [Fact]
        public void ParseOrigins_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Empty(OriginAllowList.ParseOrigins(null));
            Assert.Empty(OriginAllowList.ParseOrigins("   "));
        }

        [Fact]
        public void ParseOrigins_SplitsCommaAndSemicolon_Normalizes_Dedups()
        {
            var origins = OriginAllowList.ParseOrigins("https://Console.Example.com/ , https://console.example.com ; https://b.example.com");

            // Library normalization: scheme+host lowercased, path stripped, explicit port always appended
            // ({scheme}://{host}:{port}). The two Console.Example variants dedup to one.
            Assert.Equal(2, origins.Count);
            Assert.Contains("https://console.example.com:443", origins);
            Assert.Contains("https://b.example.com:443", origins);
        }

        [Fact]
        public void BuildAllowedOrigins_MergesPublicUrlOriginAndExtras()
        {
            var merged = OriginAllowList.BuildAllowedOrigins(
                publicUrl: "https://ai-game.dev/mcp",
                rawAllowedOrigins: "https://console.example.com");

            Assert.Contains("https://ai-game.dev:443", merged);
            Assert.Contains("https://console.example.com:443", merged);
        }

        [Fact]
        public void TryBuildOptions_NoExtraOrigins_ReturnsFalse()
        {
            var args = new DataArguments(new[] { "--public-url=https://ai-game.dev" });
            Assert.False(OriginAllowList.TryBuildOptions(args, rawAllowedOrigins: null, out var options));
            Assert.Null(options);
        }

        [Fact]
        public void TryBuildOptions_WithExtraOrigins_BuildsOptions_KeepsLoopback()
        {
            var args = new DataArguments(new[] { "--public-url=https://ai-game.dev" });
            Assert.True(OriginAllowList.TryBuildOptions(args, "https://console.example.com", out var options));

            Assert.NotNull(options);
            Assert.True(options!.AllowLoopback);
            Assert.Contains("https://console.example.com:443", options.AllowedOrigins);
            Assert.Contains("https://ai-game.dev:443", options.AllowedOrigins);
        }
    }
}
