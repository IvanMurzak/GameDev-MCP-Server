# CLAUDE.md

## What this is

Engine-agnostic C# ASP.NET Core MCP server shared by the Unity-MCP, Godot-MCP and Unreal-MCP engine plugins. Thin host (`src/Program.cs`) over the NuGet packages `com.IvanMurzak.McpPlugin.Server` + `com.IvanMurzak.ReflectorNet` — ALL real server logic lives in those packages; this repo contains no engine-specific code. Distributed as standalone executables (`gamedev-mcp-server-<rid>.zip`), a Docker image (`aigamedeveloper/mcp-server`), and a global dotnet tool on NuGet (`com.IvanMurzak.GameDev.MCP.Server`, command `gamedev-mcp-server`).

**NOT AI-Game-Dev-Server (the ai-game.dev cloud LLM/billing proxy) — this is the local MCP stdio/http proxy host shared by the engine plugins.**

## Build / run

```bash
dotnet build com.IvanMurzak.GameDev.MCP.Server.csproj
dotnet run --project com.IvanMurzak.GameDev.MCP.Server.csproj -- --client-transport stdio --port 8080
```

- All-platform self-contained publish: `./build/build-all.sh` / `./build/build-all.ps1` (`--no-zip` / `-NoZip` for CI signing flows). The scripts live in `build/` and anchor `publish/` to the repo root, so they work from any cwd.
- Local cross-project dev against MCP-Plugin-dotnet/ReflectorNet source (sibling checkouts): `dotnet build -p:UseLocalMcpPlugin=true`.

## Versioning / releases

- `<Version>` in the csproj + `server.json` move together. Keep the McpPlugin.Server / ReflectorNet pins in lockstep with the engine plugins (see the Compatibility table in README.md).
- **NuGet readme is `docs/NUGET.md`, NOT the root README.** The root `README.md` is GitHub-flavoured HTML (centered divs, raw `<img>` banners, logo rows) that NuGet's markdown sanitizer mangles into run-together text; the csproj packs the plain-markdown `docs/NUGET.md` as `README.md`. Keep its versions/compatibility table in sync with the root README on each release. (NuGet bakes the readme into the published `.nupkg` — a readme fix only shows on nuget.org once a **new version** is published.)
- `.github/workflows/deploy_server_executables.yml` (release-published trigger) builds, code-signs (Azure Trusted Signing on Windows, codesign+notarytool on macOS — gracefully degrading when secrets are absent) and uploads the 7 zips.
- `.github/workflows/deploy_docker.yml` pushes `aigamedeveloper/mcp-server:<version>` + `:latest` (secrets `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`).
- `.github/workflows/deploy_nuget.yml` (release-published trigger) packs the global dotnet tool and pushes `com.IvanMurzak.GameDev.MCP.Server` to NuGet via Trusted Publishing (OIDC, `NuGet/login@v1` as `IvanMurzak` — no stored API key). All three deploy workflows fire on the same published release, so the NuGet tool, Docker image, and zips ship together. **One-time setup**: the package id (or its `com.IvanMurzak.*` reserved prefix) needs a NuGet.org Trusted Publishing policy bound to `IvanMurzak/GameDev-MCP-Server` before the first release, exactly as the sibling library repos have.
