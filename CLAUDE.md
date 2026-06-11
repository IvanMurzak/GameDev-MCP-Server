# CLAUDE.md

## What this is

Engine-agnostic C# ASP.NET Core MCP server shared by the Unity-MCP, Godot-MCP and Unreal-MCP engine plugins. Thin host (`src/Program.cs`) over the NuGet packages `com.IvanMurzak.McpPlugin.Server` + `com.IvanMurzak.ReflectorNet` — ALL real server logic lives in those packages; this repo contains no engine-specific code. Distributed as standalone executables (`gamedev-mcp-server-<rid>.zip`), a Docker image (`aigamedeveloper/mcp-server`), and a dotnet global tool.

**NOT AI-Game-Dev-Server (the ai-game.dev cloud LLM/billing proxy) — this is the local MCP stdio/http proxy host shared by the engine plugins.**

## Build / run

```bash
dotnet build com.IvanMurzak.GameDev.MCP.Server.csproj
dotnet run --project com.IvanMurzak.GameDev.MCP.Server.csproj -- --client-transport stdio --port 8080
```

- All-platform self-contained publish: `./build-all.sh` / `./build-all.ps1` (`--no-zip` / `-NoZip` for CI signing flows).
- Local cross-project dev against MCP-Plugin-dotnet/ReflectorNet source (sibling checkouts): `dotnet build -p:UseLocalMcpPlugin=true`.

## Versioning / releases

- `<Version>` in the csproj + `server.json` move together. Keep the McpPlugin.Server / ReflectorNet pins in lockstep with the engine plugins (see the Compatibility table in README.md).
- `.github/workflows/deploy_server_executables.yml` (release-published trigger) builds, code-signs (Azure Trusted Signing on Windows, codesign+notarytool on macOS — gracefully degrading when secrets are absent) and uploads the 7 zips.
- `.github/workflows/deploy_docker.yml` pushes `aigamedeveloper/mcp-server:<version>` + `:latest` (secrets `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`).
