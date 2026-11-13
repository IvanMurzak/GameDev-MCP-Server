# Hosting and authentication

[← README](../README.md) · [Configuration](configuration.md) · [Compatibility](compatibility.md)

For local use none of this is needed: the engine plugin downloads and starts the server on loopback, and the
defaults just work. Read on when you expose the server to a network or run it in a container.

## Authentication

The server is an OAuth 2.1 **resource server**, with an offline shared-secret mode beside it.

| Mode | How a request is checked | Default for | Use it for |
| --- | --- | --- | --- |
| `none` | not checked | **stdio** | local use, offline work, CI |
| `token` | one shared bearer secret (`MCP_PLUGIN_TOKEN`), compared in constant time; `401` without it | only when set explicitly | self-hosting one project without an authorization server |
| `oauth` | ES256 JWT validated locally against cached JWKS (with PAT introspection as fallback); `401` with a `resource_metadata` challenge | **streamable HTTP**, when issuer and public URL are set | signed-in use, local and hosted |

- **Default by transport.** `stdio` uses `none`. `streamableHttp` uses `oauth` when both
  `--auth-issuer` and `--public-url` are set; otherwise it stays on `none` and logs a warning, so the
  bare `--port 8080` quickstart still works. An explicit `--auth` always wins.
- **`oauth`** requires `--auth-issuer` (the authorization server) and `--public-url` (this server's
  canonical URL, the token audience). The server refuses to start if either is missing.
- **`token`** applies to streamable HTTP AI clients and to the engine plugin: both must send the
  secret. A plugin that sends no secret or a wrong one is disconnected, and `tools/list` then returns
  an empty list. The secret crosses the network in clear text unless TLS terminates in front of the
  server.
- **Origin validation** (DNS-rebinding defense) runs in every mode: a request with a non-allowed
  `Origin` gets `403`. Loopback and the `--public-url` origin are allowed; add more with
  `--allowed-origins`.
- **Bind address** defaults to loopback. A reverse proxy or a container port mapping can reach the
  server only with `--bind any` (or `MCP_BIND=any`).

```bash
# Local, offline (stdio, auth none): the zero-config default
gamedev-mcp-server --client-transport stdio --port 8080

# Hosted resource server behind a reverse proxy (streamable HTTP, oauth)
gamedev-mcp-server --client-transport streamableHttp --port 8080 \
  --auth oauth --auth-issuer https://ai-game.dev --public-url https://ai-game.dev/mcp \
  --bind any

# Self-hosted in Docker with one shared secret
docker run -p 8080:8080 \
  -e MCP_PLUGIN_CLIENT_TRANSPORT=streamableHttp -e MCP_PLUGIN_PORT=8080 -e MCP_BIND=any \
  -e MCP_AUTH=token -e MCP_PLUGIN_TOKEN=<secret> \
  aigamedeveloper/mcp-server
```

## Docker image

[`aigamedeveloper/mcp-server`](https://hub.docker.com/r/aigamedeveloper/mcp-server) is published for
`linux/amd64` and `linux/arm64` with the tags `<version>` and `latest`. It runs as a non-root user on
a chiseled (distroless) .NET 9 base, has no shell, and exposes port `8080`. Configure it with the
environment variables from [Configuration](configuration.md); remember `MCP_BIND=any`, otherwise the
server listens only on the container's own loopback.

## Sessions and restarts

An MCP session lives in the server process. What a streamable HTTP client sees after a restart
depends on `REDIS_URL`:

| | With `REDIS_URL` | Without |
| --- | --- | --- |
| The session | Survives for the migration-record lifetime (`MCP_SESSION_MIGRATION_TTL_SECONDS`, default the idle window, 8 h): the same session id keeps working for the user who created it | **Lost** |
| The client's next request | Works | `404` with JSON-RPC error `-32001` (`Session not found`) |
| What the client must do | Nothing within that lifetime; after it, or while Redis is down, as in the other column | Start a new session with `initialize` |
| The catalog | The first standalone GET stream of the restored session gets one `list_changed` per capability that declares it: list again | A new session lists it anyway |

Without a store, a restarted server no longer knows the session or the client's protocol version, so
the `404` is the correct signal, and the MCP specification tells the client to re-initialize on it.
Not every client SDK does that automatically. Configure Redis when clients must survive a server
update.

A persisted session belongs to its creator: another authenticated user presenting its id gets `403`
while the session is live, and `404` after a restart until the owner has resumed it.
