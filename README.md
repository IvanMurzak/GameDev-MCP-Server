# GameDev MCP Server

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)

Engine-agnostic [Model Context Protocol](https://modelcontextprotocol.io/) server shared by the game-engine MCP plugins:

- [Unity-MCP](https://github.com/IvanMurzak/Unity-MCP) — Unity Editor & games
- [Godot-MCP](https://github.com/IvanMurzak/Godot-MCP) — Godot Editor & games
- [Unreal-MCP](https://github.com/IvanMurzak/Unreal-MCP) — Unreal Editor & games

It is a thin host over the NuGet packages [`com.IvanMurzak.McpPlugin.Server`](https://www.nuget.org/packages/com.IvanMurzak.McpPlugin.Server) and [`com.IvanMurzak.ReflectorNet`](https://www.nuget.org/packages/com.IvanMurzak.ReflectorNet), where all the real server logic lives. The server bridges MCP clients (Claude, Cursor, Copilot, …) and an engine plugin over SignalR:

```
MCP client  ⇄  gamedev-mcp-server  ⇄ (SignalR) ⇄  engine plugin (Unity / Godot / Unreal)
```

There is **no engine-specific code** in this repository — one server binary serves all three engine plugins. Tools, resources and prompts are provided dynamically by whichever engine plugin connects.

> **Not to be confused with** [AI-Game-Dev-Server](https://ai-game.dev) — the cloud LLM/billing proxy. This project is the **local** MCP stdio/http proxy host.

## Install / Run

### Pre-built executables

Download the zip for your platform from [Releases](https://github.com/IvanMurzak/GameDev-MCP-Server/releases) (`gamedev-mcp-server-<rid>.zip` — `win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`), unzip, and run:

```bash
./gamedev-mcp-server --port 8080 --client-transport stdio
```

Typical MCP client configuration (stdio):

```json
{
  "mcpServers": {
    "GameDev-MCP": {
      "command": "path/to/gamedev-mcp-server",
      "args": ["--port=8080", "--client-transport=stdio"]
    }
  }
}
```

### Docker

```bash
docker run -i --rm -p 8080:8080 aigamedeveloper/mcp-server
```

### dotnet tool

```bash
dotnet tool install --global com.IvanMurzak.GameDev.MCP.Server
gamedev-mcp-server --port 8080
```

### Build from source

```bash
dotnet build com.IvanMurzak.GameDev.MCP.Server.csproj
dotnet run --project com.IvanMurzak.GameDev.MCP.Server.csproj -- --client-transport stdio --port 8080
```

Cross-platform self-contained executables for all 7 RIDs: `./build-all.sh` (bash) or `./build-all.ps1` (PowerShell). Outputs land in `publish/<rid>/` and are zipped as `gamedev-mcp-server-<rid>.zip` (skip zipping with `--no-zip` / `-NoZip`).

## Configuration

CLI arguments override environment variables.

| Environment variable | CLI argument | Default | Description |
| --- | --- | --- | --- |
| `MCP_PLUGIN_PORT` | `--port` | `8080` | Client → Server ← Plugin connection port |
| `MCP_PLUGIN_CLIENT_TIMEOUT` | `--plugin-timeout` | `10000` | Plugin → Server connection timeout (ms) |
| `MCP_PLUGIN_CLIENT_TRANSPORT` | `--client-transport` | `stdio` | Client → Server transport: `stdio` or `streamableHttp` |
| `MCP_PLUGIN_IDLE_TIMEOUT_SECONDS` | `--idle-timeout-seconds` | `21600` | streamableHttp idle-session eviction window (this host seeds 6h instead of the package default of 600s) |

Logs are written to `logs/server-log.txt` (and `logs/server-log-error.txt`); in stdio mode console logging is redirected to stderr so stdout stays clean for the MCP JSON stream.

## Compatibility

| GameDev-MCP-Server | McpPlugin.Server | ReflectorNet | Unity-MCP plugin | Godot-MCP addon | Unreal-MCP plugin |
| --- | --- | --- | --- | --- | --- |
| 8.0.0 | 6.7.1 | 5.3.1 | ≥ 0.80.x | ≥ 0.3.x | ≥ 0.1.x |

The engine plugin versions listed are the ones pinning McpPlugin 6.7.x — any plugin built against McpPlugin 6.7.x talks to this server. The server version (8.0.0) is deliberately above every per-engine server artifact it replaces so auto-updaters treat it as newer.

## License

[Apache-2.0](LICENSE) — Copyright (c) 2026 Ivan Murzak
