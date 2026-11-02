# Remote mode: claude.ai custom connectors

claude.ai (web, desktop and mobile apps) connects to remote MCP servers **from Anthropic's cloud**, not from your
browser. That decides most of the requirements.

## Requirements (as of November 2026)

| Requirement | Why |
| --- | --- |
| Public hostname with an A record | The connection comes from Anthropic's backend. Outbound traffic is **IPv4 only** (`160.79.104.0/21`), so an IPv6-only host is unreachable. |
| HTTPS on **port 443** | Anthropic only connects to the standard port. Servers on `:8443` or `:9443` never see a request, and the UI says "Couldn't reach this address". This is not in the official docs but is confirmed by maintainers in [anthropics/claude-ai-mcp#1155](https://github.com/anthropics/claude-ai-mcp/issues/1155) and [#125](https://github.com/anthropics/claude-ai-mcp/issues/125). |
| Publicly trusted certificate | For example Let's Encrypt. Wildcard-IP DNS like `sslip.io` works. |
| Streamable HTTP | `/mcp`. SSE is deprecated. |
| OAuth 2.0, or no auth | exec-mcp ships its own OAuth server: DCR, PKCE S256, `401` with `resource_metadata`, form-urlencoded `/token`, rotating refresh tokens. |
| No WAF or bot challenge in front | A blocked backend request also shows as "Couldn't reach". |

IPv6: exec-mcp listens on both stacks, and your browser may use either for the sign-in page. Claude itself always
comes over IPv4. If your host only has IPv6, put an IPv4-reachable proxy in front, such as Cloudflare (proxied DNS),
a tunnel, or another dual-stack machine.

## Running it

```bash
exec-mcp --workspace /srv/project \
  --http 127.0.0.1:9443 --external-url https://mcp.example.com \
  --tls-cert /etc/letsencrypt/live/mcp.example.com/fullchain.pem \
  --tls-key  /etc/letsencrypt/live/mcp.example.com/privkey.pem \
  --anthropic-only            # /mcp, /token, /register only from 160.79.104.0/21 (+ loopback)
```

- `--external-url` is the URL users enter, without `/mcp`. It must not contain a port other than 443.
- The **owner password** comes from `EXEC_MCP_PASSWORD`, or is generated into `~/.exec-mcp/http-password`
  (mode 600). Every sign-in shows a consent page asking for it. Ten wrong attempts lock sign-in for 15 minutes.
- TLS: the PEM files are re-read when certbot renews them. Without `--tls-cert`, the server speaks plain HTTP; use
  that only behind a TLS-terminating proxy.
- Only `https://claude.ai/api/mcp/auth_callback` and loopback redirects (Claude Code) can be registered.
- Tokens: access 1 h, refresh 30 days with rotation. They are stored hashed in `~/.exec-mcp/oauth-state.json`.
  Delete that file to revoke everything.

Then in claude.ai: **Settings → Connectors → Add custom connector**, URL `https://mcp.example.com/mcp`, OAuth fields
empty (Claude falls back to dynamic client registration), sign in with the owner password.

## Sharing port 443: sni-router

If 443 already serves something else on the same IP, [deploy/sni-router](../deploy/sni-router) shares it
without terminating TLS. It reads the SNI of each connection and forwards it. The exec-mcp hostname goes to exec-mcp
(with a PROXY protocol header, so `--anthropic-only` still sees real client addresses); everything else goes to the
existing service.

```json
{
  "listen": [":443"],
  "routes": { "mcp.example.com": { "addr": "127.0.0.1:9443", "proxy_protocol": true } },
  "default": { "addr": "127.0.0.1:8444", "proxy_protocol": false }
}
```

Start exec-mcp with `--proxy-protocol`. It honours the header only on loopback connections, so remote clients cannot
spoof their address. Move the old service to `127.0.0.1:8444` and run `sni-router -config config.json`. Clients that
connect to a bare IP send no SNI and land on the default route.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| "Couldn't reach this address", nothing in the exec-mcp log | Port is not 443; firewall; the name has no A record; a WAF in front. |
| `rejected /mcp from …` in the log | `--anthropic-only` and the request did not come from `160.79.104.0/21`. Behind a proxy, use `--proxy-protocol`, otherwise every client looks like the proxy. |
| Sign-in page says "Unknown client_id" | The connector was registered against an older state file: remove the connector and add it again. |
| Commands under `bash.ask` always fail | claude.ai cannot ask the user (no MCP elicitation). Use `bash.allow`, or `"askWithoutElicitation": "allow"`. |

Each request is logged with its source IP. TLS handshake failures and connection events are logged at debug
level in HTTP mode.
