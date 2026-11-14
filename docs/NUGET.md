# AI Game Developer — GameDev MCP Server

[![NuGet](https://img.shields.io/nuget/v/com.IvanMurzak.GameDev.MCP.Server?label=NuGet&color=2F6F9F&logo=nuget&style=for-the-badge&labelColor=0F172A&logoColor=white)](https://www.nuget.org/packages/com.IvanMurzak.GameDev.MCP.Server/) [![Docker](https://img.shields.io/docker/v/aigamedeveloper/mcp-server?sort=semver&label=Docker&color=2F6F9F&logo=docker&style=for-the-badge&labelColor=0F172A&logoColor=white)](https://hub.docker.com/r/aigamedeveloper/mcp-server) [![.NET 9](https://img.shields.io/badge/-9.0-0E7490?logo=dotnet&style=for-the-badge&labelColor=0F172A&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/9.0) [![License](https://img.shields.io/github/license/IvanMurzak/GameDev-MCP-Server?label=License&color=047857&style=for-the-badge&labelColor=0F172A&logoColor=white)](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/LICENSE) [![Discord](https://img.shields.io/badge/Discord-Join-5865F2?logo=discord&style=for-the-badge&labelColor=0F172A&logoColor=white)](https://discord.gg/cfbdMZX99G) [![Stand with Ukraine](https://raw.githubusercontent.com/IvanMurzak/IvanMurzak/main/images/ukraine/flag-badge.svg)](https://stand-with-ukraine.pp.ua)

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

Inside a container the server must listen on all interfaces (`MCP_BIND=any`), or the port mapping cannot reach it:

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  -e MCP_PLUGIN_CLIENT_TRANSPORT=streamableHttp -e MCP_BIND=any \
  aigamedeveloper/mcp-server
```

### Pre-built executables

Download `gamedev-mcp-server-<rid>.zip` for Windows, Linux or macOS from [Releases](https://github.com/IvanMurzak/GameDev-MCP-Server/releases), unzip, and run:

```bash
./gamedev-mcp-server --port 8080 --client-transport stdio
```

## Configuration

Every option is an environment variable or a CLI argument (arguments win). The ones you will reach for:

| CLI argument | Environment variable | Default | Meaning |
| --- | --- | --- | --- |
| `--port` | `MCP_PLUGIN_PORT` | `8080` | Port for AI clients and the engine plugin |
| `--client-transport` | `MCP_PLUGIN_CLIENT_TRANSPORT` | `stdio` | `stdio` or `streamableHttp` |
| `--tool-timeout` | `MCP_PLUGIN_TOOL_TIMEOUT` | `300000` | Max time for one tool call (ms) |
| `--auth` | `MCP_AUTH` | by transport | `none`, `token` or `oauth` |
| `--bind` | `MCP_BIND` | `loopback` | `loopback`, `any` or an IP |

More options: [Configuration](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/docs/configuration.md). Auth modes: [Hosting and authentication](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/docs/hosting.md).

In stdio mode console logging is redirected to stderr so stdout stays clean for the MCP JSON stream.

The default level is `Info`. `MCP_LOG_LEVEL=Trace` dumps every MCP message body for debugging; it is very high volume and rotates `server-log.txt` down to a few minutes of retained history, so set it only while reproducing an issue.

Write a pinned, URL-only MCP client config for an AI agent from the terminal: `gamedev-mcp-server configure --agent claude-code` (or `--agent codex --url https://ai-game.dev/mcp`; `configure --help` lists all agents).

## Compatibility

| GameDev-MCP-Server | McpPlugin.Server | ReflectorNet | Engine plugins |
| --- | --- | --- | --- |
| 9.2.5 and newer ([latest release](https://github.com/IvanMurzak/GameDev-MCP-Server/releases/latest)) | 8.x | 5.x | engine plugins on McpPlugin 8.x |
| 9.2.4 (last 7.x) | 7.5.2 | 5.4.0 | engine plugins on McpPlugin 7.x |
| 8.0.3 (legacy) | 6.11.0 | 5.3.1 | engine plugins on McpPlugin 6.x |

Server releases `9.2.5` and newer build against McpPlugin 8.x; `9.0.0`–`9.2.4` build against McpPlugin 7.x (the instance-metadata handshake + `oauth` mode), and McpPlugin 6.x plugins pair with the `8.0.3` line. For the exact pins of an intermediate version see its entry on [Releases](https://github.com/IvanMurzak/GameDev-MCP-Server/releases).

## Links

- **Source & full documentation:** [github.com/IvanMurzak/GameDev-MCP-Server](https://github.com/IvanMurzak/GameDev-MCP-Server)
- **Docs:** [Configuration](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/docs/configuration.md) · [Hosting and authentication](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/docs/hosting.md) · [Compatibility](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/docs/compatibility.md)
- **Discord:** [discord.gg/cfbdMZX99G](https://discord.gg/cfbdMZX99G)

## License

[Apache-2.0](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/LICENSE) — Copyright © 2026 Ivan Murzak

<!-- mcp-name: io.github.IvanMurzak/Unity-MCP -->
