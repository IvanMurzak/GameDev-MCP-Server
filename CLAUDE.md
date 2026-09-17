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
- **Release = manual dispatch of `.github/workflows/release.yml` ONLY** (`gh workflow run release.yml -R IvanMurzak/GameDev-MCP-Server --ref main -f dry_run=false`). Merging a version bump publishes nothing, and nothing listens for GitHub `release` events. Order: guard (real run on `main` only; fails if tag `v<version>` already exists) -> full tests (`test_pull_request.yml` via `workflow_call`) -> executables + NuGet + Docker Hub -> tag `v<version>` + GitHub Release (7 zips + `SHA256SUMS`) -> post-publish verification (NuGet index, Docker Hub tag, release assets). `dry_run=true` runs every test and build and publishes nothing.
- `.github/workflows/deploy_server_executables.yml` (`workflow_call` child) builds, code-signs (Azure Trusted Signing on Windows, codesign+notarytool on macOS — gracefully degrading when secrets are absent) and zips the 7 RIDs, handing them to `release.yml` as 1-day artifacts; `release.yml` computes `SHA256SUMS` and attaches everything to the Release.
- `.github/workflows/deploy_docker.yml` (`workflow_call` child) pushes `aigamedeveloper/mcp-server:<version>` + `:latest` (secrets `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`).
- `.github/workflows/deploy_nuget.yml` (`workflow_call` child) packs the global dotnet tool and pushes `com.IvanMurzak.GameDev.MCP.Server` to NuGet via Trusted Publishing (OIDC, `NuGet/login@v1` as `IvanMurzak` — no stored API key). **Trusted Publishing policy**: the login now runs inside a reusable workflow called by `release.yml`, so the NuGet.org policy bound to `IvanMurzak/GameDev-MCP-Server` must accept that invocation (the sibling library repos, e.g. ReflectorNet, already publish from a `deploy.yml` called by `release.yml`).
