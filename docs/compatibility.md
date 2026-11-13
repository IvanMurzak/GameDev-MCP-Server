# Compatibility

[← README](../README.md) · [Configuration](configuration.md) · [Hosting and authentication](hosting.md)

The server is built on
[`com.IvanMurzak.McpPlugin.Server`](https://www.nuget.org/packages/com.IvanMurzak.McpPlugin.Server) and
[`com.IvanMurzak.ReflectorNet`](https://www.nuget.org/packages/com.IvanMurzak.ReflectorNet); the engine
plugins are built on the McpPlugin family too. Pick the server line that matches the McpPlugin major
version of your plugin. The plugins pin and download their server version themselves.

| GameDev-MCP-Server | McpPlugin.Server | ReflectorNet | Engine plugins |
| --- | --- | --- | --- |
| **9.5.2** (current) | 8.23.0 | 5.5.2 | on McpPlugin **8.x** |
| 9.2.4 (last 7.x) | 7.5.2 | 5.4.0 | on McpPlugin **7.x** |
| 8.0.3 (legacy) | 6.11.0 | 5.3.1 | on McpPlugin **6.x** |

Server `9.2.5` and newer build against McpPlugin **8.x**; `9.0.0`–`9.2.4` build against **7.x**;
McpPlugin 6.x plugins pair with the `8.0.3` line. The exact pins of any other version are in its
entry on [Releases](https://github.com/IvanMurzak/GameDev-MCP-Server/releases).

## Behavior that depends on the plugin version

**Tool-argument checks (plugin on McpPlugin 8.14.0+).** A plugin that reports `mcpPluginVersion`
8.14.0 or newer in its handshake gets server-side validation of tool arguments (required arguments
and types) and one whole-call deadline with reload / re-send handling. An older plugin, or one that
reports no version, has its calls forwarded unchanged, as server 9.2.x did. The editor and game
connection roles (the editor stays connected when a game connects; game-only tools go to the game)
apply only when both connections are 8.14+ plugins; otherwise the newest connection replaces the
previous one.

**Tool errors on the direct endpoint (plugin on McpPlugin 8.15.0+).** A tool that fails because of
what the request asked for (for example, code that does not compile) answers
`POST /api/tools/{name}` with **422** and the tool's message; a fault inside the plugin stays **500**.
The `/mcp` channel is unchanged: an `isError` result with the tool's message. Older plugins report
every exception as 500.
