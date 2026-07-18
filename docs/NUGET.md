# AI Game Developer — GameDev MCP Server

[![NuGet](https://img.shields.io/nuget/v/com.IvanMurzak.GameDev.MCP.Server?label=NuGet&labelColor=333A41)](https://www.nuget.org/packages/com.IvanMurzak.GameDev.MCP.Server/)
[![Docker Image](https://img.shields.io/docker/image-size/aigamedeveloper/mcp-server/latest?label=Docker%20Image&logo=docker&logoColor=white&labelColor=333A41)](https://hub.docker.com/r/aigamedeveloper/mcp-server)
[![License](https://img.shields.io/github/license/IvanMurzak/GameDev-MCP-Server?label=License&labelColor=333A41)](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/LICENSE)

Engine-agnostic [Model Context Protocol](https://modelcontextprotocol.io/) server shared by the game-engine MCP plugins:

- [Unity-MCP](https://github.com/IvanMurzak/Unity-MCP) — Unity Editor & games
- [Godot-MCP](https://github.com/IvanMurzak/Godot-MCP) — Godot Editor & games
- [Unreal-MCP](https://github.com/IvanMurzak/Unreal-MCP) — Unreal Editor & games

It is a thin host over the NuGet packages [`com.IvanMurzak.McpPlugin.Server`](https://www.nuget.org/packages/com.IvanMurzak.McpPlugin.Server) and [`com.IvanMurzak.ReflectorNet`](https://www.nuget.org/packages/com.IvanMurzak.ReflectorNet), where all the real server logic lives. The server bridges MCP clients (Claude, Cursor, Copilot, …) and an engine plugin over SignalR:

```
MCP client  <->  gamedev-mcp-server  <-> (SignalR) <->  engine plugin (Unity / Godot / Unreal)
```

There is **no engine-specific code** in this package — one server binary serves all three engine plugins. Tools, resources and prompts are provided dynamically by whichever engine plugin connects.

> **Not to be confused with** [AI-Game-Dev-Server](https://ai-game.dev) — the cloud LLM/billing proxy. This project is the **local** MCP stdio/http proxy host.

## Install

### dotnet tool (this package)

```bash
dotnet tool install --global com.IvanMurzak.GameDev.MCP.Server
gamedev-mcp-server --port 8080
```

Typical MCP client configuration (stdio):

```json
{
  "mcpServers": {
    "GameDev-MCP": {
      "command": "gamedev-mcp-server",
      "args": ["--port=8080", "--client-transport=stdio"]
    }
  }
}
```

### Docker

```bash
docker run -i --rm -p 8080:8080 aigamedeveloper/mcp-server
```

### Pre-built executables

Download the zip for your platform from [Releases](https://github.com/IvanMurzak/GameDev-MCP-Server/releases) — `gamedev-mcp-server-<rid>.zip` for `win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` — unzip, and run:

```bash
./gamedev-mcp-server --port 8080 --client-transport stdio
```

## Configuration

CLI arguments override environment variables.

| Environment variable | CLI argument | Default | Description |
| --- | --- | --- | --- |
| `MCP_PLUGIN_PORT` | `--port` | `8080` | Client to Server to Plugin connection port |
| `MCP_PLUGIN_CLIENT_TIMEOUT` | `--plugin-timeout` | `10000` | Plugin to Server connection timeout (ms) |
| `MCP_PLUGIN_CLIENT_TRANSPORT` | `--client-transport` | `stdio` | Client to Server transport: `stdio` or `streamableHttp` |
| `MCP_PLUGIN_IDLE_TIMEOUT_SECONDS` | `--idle-timeout-seconds` | `21600` | streamableHttp idle-session eviction window |
| `MCP_AUTH` | `--auth` | transport-dependent | Authentication mode: `none` or `oauth` (stdio -> none; http -> oauth when issuer + public-url set) |
| `MCP_AUTH_ISSUER` | `--auth-issuer` | — | OAuth authorization-server URL. Required for `oauth` |
| `MCP_PUBLIC_URL` | `--public-url` | — | Canonical public URL / token audience. Required for `oauth` |
| `MCP_BIND` | `--bind` | `loopback` | Bind address: `loopback`, `any` (0.0.0.0), or a specific IP |
| `MCP_ALLOWED_ORIGINS` | `--allowed-origins` | — | Additional allowed browser Origins (comma/semicolon-separated) |

In stdio mode console logging is redirected to stderr so stdout stays clean for the MCP JSON stream.

Write a pinned, URL-only MCP client config for an AI agent from the terminal:
`gamedev-mcp-server configure --agent claude-code` (or `--agent codex --url https://ai-game.dev/mcp`; `configure --help` lists all agents).

## Compatibility

| GameDev-MCP-Server | McpPlugin.Server | ReflectorNet | Engine plugins |
| --- | --- | --- | --- |
| 8.0.3 (released) | 6.11.0 | 5.3.1 | engine plugins on McpPlugin 6.x |
| 9.0.0 (released) | 7.0.0-preview.1 | 5.3.2 | engine plugins on McpPlugin 7.x (Phase 4) |

The released `9.0.0` server line builds against McpPlugin.Server 7.0.0-preview.1 (the OAuth resource-server major). The `main` branch is now an in-development `9.1.0` snapshot building against McpPlugin.Server 7.1.1; its version bump + publish is a separate owner-gated release step. Older McpPlugin 6.x engine plugins pair with the released `8.0.3` server line.

## Links

- **Source & full documentation:** [github.com/IvanMurzak/GameDev-MCP-Server](https://github.com/IvanMurzak/GameDev-MCP-Server)
- **Discord:** [discord.gg/cfbdMZX99G](https://discord.gg/cfbdMZX99G)

## License

[Apache-2.0](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/LICENSE) — Copyright © 2026 Ivan Murzak
