# AGENTS.md

Engine-agnostic C# ASP.NET Core MCP server shared by the Unity-MCP, Godot-MCP and Unreal-MCP engine
plugins. Thin host (`src/Program.cs`) over the NuGet packages `com.IvanMurzak.McpPlugin.Server` +
`com.IvanMurzak.ReflectorNet` — ALL real server logic lives in those packages; this repo contains no
engine-specific code. Distributed as standalone executables (`gamedev-mcp-server-<rid>.zip`), a
Docker image (`aigamedeveloper/mcp-server`), and a global dotnet tool on NuGet
(`com.IvanMurzak.GameDev.MCP.Server`, command `gamedev-mcp-server`).

**NOT AI-Game-Dev-Server (the ai-game.dev cloud LLM/billing proxy) — this is the local MCP
stdio/http proxy host shared by the engine plugins.**

## Build / run

```bash
dotnet build com.IvanMurzak.GameDev.MCP.Server.csproj
dotnet run --project com.IvanMurzak.GameDev.MCP.Server.csproj -- --client-transport stdio --port 8080
```

- All-platform self-contained publish: `./build/build-all.sh` / `./build/build-all.ps1`
  (`--no-zip` / `-NoZip` for CI signing flows); they anchor `publish/` to the repo root, so any cwd works.
- Local dev against sibling MCP-Plugin-dotnet/ReflectorNet checkouts: `dotnet build -p:UseLocalMcpPlugin=true`.

## Layout

- `src/` — the host: `Program.cs`, startup/auth defaults (`Startup/`), the `configure` command
  (`Configure/`), logging adapters (`Utils/`). New host code goes here, not engine or server logic.
- `tests/GameDev.MCP.Server.Tests/` — xUnit tests (excluded from the main csproj build and pack).
- `build/` — cross-platform publish scripts and the macOS entitlements.
- `docs/NUGET.md` — the NuGet readme; `docs/img/` — README images.
- `.github/workflows/` — PR tests and the dispatch-only release with its `deploy_*` children.
- Root: the csproj, `server.json` (MCP registry manifest), `Dockerfile`, `appsettings.json`, `NLog.config`.

## Versioning / releases

- `<Version>` in the csproj + `server.json` move together. Keep the McpPlugin.Server / ReflectorNet
  pins in lockstep with the engine plugins (Compatibility table in README.md).
- Between test waves McpPlugin is integrated unreleased: `<version>-dev.<sha>` packages committed under
  `prerelease-packages/`, fed to every restore by `Directory.Build.props` (and copied by the Dockerfile).
  Before a release, pin published versions and delete both (nothing in `release.yml` refuses a `-dev` pin).
- **NuGet readme is `docs/NUGET.md`, NOT the root README** (whose GitHub HTML NuGet's sanitizer
  mangles); the csproj packs it as `README.md`. Keep its versions/compatibility table in sync with the
  root README on each release. A readme fix shows on nuget.org only after a **new version** is published.
- **Release = manual dispatch of `.github/workflows/release.yml` ONLY**
  (`gh workflow run release.yml -R IvanMurzak/GameDev-MCP-Server --ref main -f dry_run=false`).
  Merging a version bump publishes nothing; nothing listens for GitHub `release` events. Order: guard
  (real run on `main` only; fails if tag `v<version>` exists) -> full tests (`test_pull_request.yml`
  via `workflow_call`) -> executables + NuGet + Docker Hub -> tag `v<version>` + GitHub Release
  (7 zips + `SHA256SUMS`) -> post-publish verification (NuGet index, Docker Hub tag, release assets).
  `dry_run=true` runs every test and build and publishes nothing.
- `workflow_call` children of `release.yml`:
  - `deploy_server_executables.yml` builds, code-signs (Azure Trusted Signing on Windows,
    codesign+notarytool on macOS — degrading gracefully without secrets) and zips the 7 RIDs as 1-day
    artifacts; `release.yml` computes `SHA256SUMS` and attaches everything to the Release.
  - `deploy_docker.yml` pushes `aigamedeveloper/mcp-server:<version>` + `:latest`
    (secrets `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`).
  - `deploy_nuget.yml` packs the global tool and pushes it to NuGet via Trusted Publishing (OIDC,
    `NuGet/login@v1` as `IvanMurzak`, no stored API key). The login runs inside this reusable
    workflow, so the NuGet.org policy bound to `IvanMurzak/GameDev-MCP-Server` must accept that
    invocation (ReflectorNet already publishes the same way from a `deploy.yml` called by `release.yml`).
