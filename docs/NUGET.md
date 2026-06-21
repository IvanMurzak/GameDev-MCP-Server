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

In stdio mode console logging is redirected to stderr so stdout stays clean for the MCP JSON stream.

## Compatibility

| GameDev-MCP-Server | McpPlugin.Server | ReflectorNet | Unity-MCP plugin | Godot-MCP addon | Unreal-MCP plugin |
| --- | --- | --- | --- | --- | --- |
| 8.0.1 | 6.10.0 | 5.3.1 | >= 0.80.x | >= 0.3.x | >= 0.1.x |

Any engine plugin built against McpPlugin 6.x talks to this server.

## Links

- **Source & full documentation:** [github.com/IvanMurzak/GameDev-MCP-Server](https://github.com/IvanMurzak/GameDev-MCP-Server)
- **Discord:** [discord.gg/cfbdMZX99G](https://discord.gg/cfbdMZX99G)

## License

[Apache-2.0](https://github.com/IvanMurzak/GameDev-MCP-Server/blob/main/LICENSE) — Copyright © 2026 Ivan Murzak
