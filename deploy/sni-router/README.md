# sni-router

Shares one TLS port between several services **without terminating TLS**. It reads the SNI from the ClientHello
and forwards the raw connection; connections without SNI (clients that dial a bare IP) go to the default route.
For routes with `proxy_protocol: true` it prepends a PROXY protocol v1 line, so the backend still sees the real
client address (exec-mcp: `--proxy-protocol`).

Why: claude.ai connects to remote MCP servers only on port 443, which is often already in use.

```bash
CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o sni-router .
./sni-router -config config.json -check     # validate
./sni-router -config config.json
go test ./...
```

```json
{
  "listen": [":443"],
  "routes": { "mcp.example.com": { "addr": "127.0.0.1:9443", "proxy_protocol": true } },
  "default": { "addr": "127.0.0.1:8444", "proxy_protocol": false }
}
```

Moving an existing service from `:443` to `127.0.0.1:8444` with almost no downtime:

1. Start the second copy of the service on `127.0.0.1:8444`, and exec-mcp on `127.0.0.1:9443`.
2. Stop the old instance and immediately start sni-router on `:443`; the gap is well under a second.
3. Check both routes, and keep the old command line around to roll back.

Service files: [../openrc/sni-router](../openrc/sni-router), [../systemd/sni-router.service](../systemd/sni-router.service).
Standard library only; Go 1.22+.
