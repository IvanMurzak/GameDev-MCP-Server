# Configuration

[← README](../README.md) · [Hosting and authentication](hosting.md) · [Compatibility](compatibility.md)

Each option below can be set as a command-line argument or an environment variable (the option column lists both). **CLI arguments override environment variables.** Both `--port 8080` and `--port=8080` work.

## Options

| Option | Default | Description |
| --- | --- | --- |
| `--port`<br>`MCP_PLUGIN_PORT` | `8080` | The one port both sides use: AI clients (streamable HTTP) and the engine plugin (SignalR) |
| `--client-transport`<br>`MCP_PLUGIN_CLIENT_TRANSPORT` | `stdio` | Client → server transport: `stdio` or `streamableHttp` |
| `--plugin-timeout`<br>`MCP_PLUGIN_CLIENT_TIMEOUT` | `10000` | One attempt of a catalog / prompt / resource request to the plugin (ms). Tool calls use `--tool-timeout` |
| `--tool-timeout`<br>`MCP_PLUGIN_TOOL_TIMEOUT` | `300000` | How long one tool call may run in the plugin (ms); by default `300000` or `--plugin-timeout`, whichever is larger. A tool call is sent once and never re-sent: on timeout or a dropped connection it fails, and the tool may still be running in the editor. Raise it for long editor operations (builds, imports) |
| `--idle-timeout-seconds`<br>`MCP_PLUGIN_IDLE_TIMEOUT_SECONDS` | `28800` | streamable HTTP idle-session eviction window. This host uses 8 h instead of the package default of 600 s, so a local session survives a working day |
| `--auth`<br>`MCP_AUTH` | by transport | `none`, `token` or `oauth` — see [Hosting and authentication](hosting.md#authentication) |
| `--token`<br>`MCP_PLUGIN_TOKEN` | — | The shared secret of `token` mode. Ignored by `none` and `oauth` |
| `--auth-issuer`<br>`MCP_AUTH_ISSUER` | — | OAuth authorization-server URL. Required for `oauth` |
| `--public-url`<br>`MCP_PUBLIC_URL` | — | This server's canonical public URL (the token audience). Required for `oauth`; also added to the Origin allow-list |
| `--bind`<br>`MCP_BIND` | `loopback` | Listen address: `loopback`, `any` (0.0.0.0), or a specific IP |
| `--allowed-origins`<br>`MCP_ALLOWED_ORIGINS` | — | Extra allowed browser Origins, comma- or semicolon-separated (loopback and the `--public-url` origin are always allowed) |
| `REDIS_URL` | — | streamable HTTP: keep sessions in Redis so they survive a restart — see [Sessions and restarts](hosting.md#sessions-and-restarts) |
| `MCP_REDIS_KEY_PREFIX` | `mcp-server:` | Key namespace when several servers share one Redis |
| `MCP_SESSION_MIGRATION_TTL_SECONDS` | idle timeout | How long a persisted session can be resumed after a restart. Never shorter than the idle timeout, which it reads from `MCP_PLUGIN_IDLE_TIMEOUT_SECONDS` only (`--idle-timeout-seconds` does not change it) |
| `MCP_SSE_KEEPALIVE_SECONDS` | `30` | streamable HTTP: seconds of silence before a keep-alive comment is sent on the standalone SSE stream, so idle clients and proxies keep it open. `0` turns it off |
| `MCP_SENTRY_DSN` / `MCP_SENTRY_ENVIRONMENT` | — | Opt-in crash reporting to a Sentry-compatible endpoint: unhandled exceptions only, no PII. Unset = off |
| `MCP_LOG_LEVEL` | `Info` | Log level floor: `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal`. Read by NLog, so it has no CLI argument |

## Logs

Logs go to `logs/server-log.txt` and `logs/server-log-error.txt` next to the executable (10 MB files, 5 archives). In stdio mode console logging goes to **stderr**, so stdout carries only the MCP JSON stream.

`MCP_LOG_LEVEL=Trace` logs every MCP message body. Use it only while reproducing a protocol issue: on a busy server it produces tens of thousands of lines per minute, and rotation then keeps only a few minutes of history.

## Configure an AI agent

`configure` writes a project-scoped MCP client config for an AI agent, without opening an editor:

```bash
# local server, current folder
gamedev-mcp-server configure --agent claude-code

# hosted server
gamedev-mcp-server configure --agent codex --url https://ai-game.dev/mcp

# list every agent id
gamedev-mcp-server configure --help
```

| Option | Meaning |
| --- | --- |
| `--agent <id>` | Required. `antigravity`, `claude-code`, `claude-desktop`, `cline`, `codex`, `cursor`, `gemini`, `github-copilot-cli`, `kilo-code`, `open-code`, `rider-junie`, `unity-ai`, `vs-copilot`, `vscode-copilot`, `zoo-code`, `other-custom` |
| `--url <url>` | Use a hosted server instead of the local one |
| `--transport stdio\|http` | Write a stdio spawn config instead of an HTTP one |
| `--project <path>` | Project root (default: the current folder) |

The written config contains a URL only — credentials are never written to project files; the agent signs in with native OAuth. The URL carries the project pin (`/p/<pin>`) and, for a local server, the port derived from the project path, so an agent started in this folder always reaches this project's engine.
