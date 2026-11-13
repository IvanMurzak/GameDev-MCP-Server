# Configuration

[← README](../README.md) · [Hosting and authentication](hosting.md) · [Compatibility](compatibility.md)

Every option can be set as an environment variable or a command-line argument. **CLI arguments
override environment variables.** Both `--port 8080` and `--port=8080` work.

## Options

| Environment variable | CLI argument | Default | Description |
| --- | --- | --- | --- |
| `MCP_PLUGIN_PORT` | `--port` | `8080` | The one port both sides use: AI clients (streamable HTTP) and the engine plugin (SignalR) |
| `MCP_PLUGIN_CLIENT_TRANSPORT` | `--client-transport` | `stdio` | Client → server transport: `stdio` or `streamableHttp` |
| `MCP_PLUGIN_CLIENT_TIMEOUT` | `--plugin-timeout` | `10000` | One attempt of a catalog / prompt / resource request to the plugin (ms). Tool calls use `--tool-timeout` |
| `MCP_PLUGIN_TOOL_TIMEOUT` | `--tool-timeout` | `300000` (or `--plugin-timeout` if larger) | How long one tool call may run in the plugin (ms). A tool call is sent once and never re-sent: on timeout or a dropped connection it fails, and the tool may still be running in the editor. Raise it for long editor operations (builds, imports) |
| `MCP_PLUGIN_IDLE_TIMEOUT_SECONDS` | `--idle-timeout-seconds` | `28800` | streamable HTTP idle-session eviction window. This host uses 8 h instead of the package default of 600 s, so a local session survives a working day |
| `MCP_AUTH` | `--auth` | depends on transport | `none`, `token` or `oauth` — see [Hosting and authentication](hosting.md#authentication) |
| `MCP_PLUGIN_TOKEN` | `--token` | — | The shared secret of `token` mode. Ignored by `none` and `oauth` |
| `MCP_AUTH_ISSUER` | `--auth-issuer` | — | OAuth authorization-server URL. Required for `oauth` |
| `MCP_PUBLIC_URL` | `--public-url` | — | This server's canonical public URL (the token audience). Required for `oauth`; also added to the Origin allow-list |
| `MCP_BIND` | `--bind` | `loopback` | Listen address: `loopback`, `any` (0.0.0.0), or a specific IP |
| `MCP_ALLOWED_ORIGINS` | `--allowed-origins` | — | Extra allowed browser Origins, comma- or semicolon-separated (loopback and the `--public-url` origin are always allowed) |
| `REDIS_URL` | — | — | streamable HTTP: keep sessions in Redis so they survive a restart — see [Sessions and restarts](hosting.md#sessions-and-restarts) |
| `MCP_REDIS_KEY_PREFIX` | — | `mcp-server:` | Key namespace when several servers share one Redis |
| `MCP_SESSION_MIGRATION_TTL_SECONDS` | — | the idle window | How long a persisted session can be resumed after a restart |
| `MCP_SSE_KEEPALIVE_SECONDS` | — | `30` | streamable HTTP: seconds of silence before a keep-alive comment is sent on the standalone SSE stream, so idle clients and proxies keep it open. `0` turns it off |
| `MCP_SENTRY_DSN` / `MCP_SENTRY_ENVIRONMENT` | — | — | Opt-in crash reporting to a Sentry-compatible endpoint: unhandled exceptions only, no PII. Unset = off |
| `MCP_LOG_LEVEL` | — | `Info` | Log level floor: `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal`. Read by NLog, so it has no CLI argument |

## Logs

Logs go to `logs/server-log.txt` and `logs/server-log-error.txt` next to the executable (10 MB files,
5 archives). In stdio mode console logging goes to **stderr**, so stdout carries only the MCP JSON
stream.

`MCP_LOG_LEVEL=Trace` logs every MCP message body. Use it only while reproducing a protocol issue:
on a busy server it produces tens of thousands of lines per minute, and rotation then keeps only a few
minutes of history.

## Configure an AI agent

`configure` writes a project-scoped MCP client config for an AI agent, without opening an editor:

```bash
gamedev-mcp-server configure --agent claude-code                                 # local server, current folder
gamedev-mcp-server configure --agent codex --url https://ai-game.dev/mcp         # hosted server
gamedev-mcp-server configure --help                                              # list every agent id
```

| Option | Meaning |
| --- | --- |
| `--agent <id>` | Required. `antigravity`, `claude-code`, `claude-desktop`, `cline`, `codex`, `cursor`, `gemini`, `github-copilot-cli`, `kilo-code`, `open-code`, `rider-junie`, `unity-ai`, `vs-copilot`, `vscode-copilot`, `zoo-code`, `other-custom` |
| `--url <url>` | Use a hosted server instead of the local one |
| `--transport stdio\|http` | Write a stdio spawn config instead of an HTTP one |
| `--project <path>` | Project root (default: the current folder) |

The written config contains a URL only — credentials are never written to project files; the agent
signs in with native OAuth. The URL carries the project pin (`/p/<pin>`) and, for a local server, the
port derived from the project path, so an agent started in this folder always reaches this project's
engine.
