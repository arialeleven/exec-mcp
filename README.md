# exec-mcp

An MCP server that gives Claude a full, Claude Code–style toolbox on **your** machine: read, edit and
search files, run shell commands in a persistent session, delegate to subagents, and script many tool calls
in a JavaScript sandbox. You can add new tools by dropping a script into a folder, with no rebuild and no
restart. It works locally over stdio (Claude Code, Claude Desktop) and remotely over HTTPS with built-in
OAuth (claude.ai custom connectors, including the mobile apps).

```
claude.ai / Claude Desktop / Claude Code
        │  MCP (stdio or Streamable HTTP + OAuth)
        ▼
   exec-mcp ── core tools: Read · Edit · MultiEdit · Write · Glob · Grep · Bash · BashOutput · TodoWrite · Task · ToolSearch · exec
      │
      ├── script tools   ~/.exec-mcp/tools/*.sh|py|js|ps1   (hot-reloaded, no rebuild)
      └── plugins        node · python · stdio · existing MCP servers (manifest, permissions, approval)
```

## Highlights

- **Tools that behave like Claude Code's.** `Edit` refuses to touch files you have not `Read`, or that changed since you read them.
  It reports ambiguous matches with line numbers and survives copied line-number prefixes. Its fuzzy fallback reports
  what it normalised and keeps BOM, CRLF and curly quotes. `Bash` keeps cwd, exported variables, functions and aliases between
  calls; a timeout kills the command, not the session.
- **Small context footprint.** Partial reads, search before read, head+tail truncation with the full output
  spilled to a file, and short confirmations such as "Applied, lines 40–47".
- **Errors that say what to do next**, for example "File not found: src/aa.py. Did you mean src/a.py?" or "found 2 matches (lines 4, 5)".
- **Extensible without rebuilding**: [script tools](docs/script-tools.md) (one file per tool, any language) and
  [plugins](docs/plugins.md) (manifest, permissions enforced by the host, Node/Python SDKs).
- **Code mode**: the `exec` tool runs JavaScript with every tool available as a typed async function
  (`core.Read`, `tools.disk_usage`, `db.query`), so 50 calls cost one round trip and you print only the result.
- **Remote mode for claude.ai**: Streamable HTTP, an OAuth 2.1 server (DCR, PKCE, rotating refresh tokens,
  owner password), an Anthropic egress allowlist, PROXY protocol support and [sni-router](deploy/sni-router) for sharing port 443.

## Install

Linux, x86-64 or arm64, glibc or musl (Alpine). One command installs or updates:

```sh
curl -fsSL https://github.com/arialeleven/exec-mcp/releases/latest/download/install.sh | sh
```

exec-mcp is one portable .NET archive, about 9 MB and the same for every Linux. If the machine has no ASP.NET Core 10
runtime, the installer downloads Microsoft's runtime next to exec-mcp, verifies its SHA-512 and keeps it private.
It does not touch the system. As root, it installs to `/opt/exec-mcp` with the `exec-mcp` command in `/usr/local/bin`.
As a normal user, it installs to `~/.local/share/exec-mcp` and `~/.local/bin`. Re-running the command updates exec-mcp
and the runtime, keeps the previous version in `*.prev`, and restarts the service if one is running.

```sh
… | sh -s -- --examples                 # also copy the example script tools to ~/.exec-mcp/tools
… | sh -s -- --version v0.1.0           # a specific release
… | sudo sh -s -- --service --letsencrypt   # claude.ai connector: HTTPS on 443, certificate, OAuth, service
… | sh -s -- --uninstall
```

Optional at runtime: `ripgrep` (faster Glob/Grep; a built-in fallback is used otherwise), `node` / `python3`
for plugins and script tools in those languages, `pdftotext` for reading PDFs as text.

From source (.NET 10 SDK): `dotnet run --project src/ExecMcp -- --workspace /path/to/project`, or `./build.sh` → `dist/`.

## Connect

**Claude Code** (on the Linux machine)

```bash
claude mcp add exec -- exec-mcp --workspace /path/to/project
```

**claude.ai, Claude Desktop, mobile apps**: on a Linux server with ports 80 and 443 free, one command sets up the
whole connector:

```sh
curl -fsSL https://github.com/arialeleven/exec-mcp/releases/latest/download/install.sh | sudo sh -s -- --service --letsencrypt
```

- **Name and certificate.** It names the server `<public-ip>.sslip.io` (or use `--external-url`) and gets an
  auto-renewing Let's Encrypt certificate.
- **Service.** It starts a systemd/OpenRC service as the unprivileged user `exec-mcp` (`--user root` to administer
  the host) with OAuth and the Anthropic-only allowlist.
- **What to do next.** It prints the connector URL and the owner password. Add the URL in claude.ai under
  **Settings → Connectors → Add custom connector**, then sign in with the password.

claude.ai only connects to **port 443** over **IPv4**. More details, including how to share 443 with another
service, are in [docs/remote.md](docs/remote.md).

## Add a tool in 30 seconds

```bash
exec-mcp tools new disk_usage --lang sh      # creates ~/.exec-mcp/tools/disk_usage.sh
```

```bash
#!/usr/bin/env bash
# @tool disk_usage
# @description Disk usage of a directory, largest entries first.
# @param path string Directory to measure
# @param top integer? =15 How many entries
# @readonly
du -h -d 1 -- "$path" | sort -rh | head -n "$top"
```

A running server picks it up right away and tells connected clients that the tool list changed.
More examples are in [examples/tools](examples/tools).

## Documentation

| | |
| --- | --- |
| [docs/tools.md](docs/tools.md) | Core tools and their behaviour |
| [docs/script-tools.md](docs/script-tools.md) | Adding tools with a script: header format, parameters, output |
| [docs/plugins.md](docs/plugins.md) | Plugins: manifest, permissions, SDKs, protocol, `plugin test` |
| [docs/sandbox.md](docs/sandbox.md) | The `exec` code-mode sandbox |
| [docs/configuration.md](docs/configuration.md) | `~/.exec-mcp/config.json` reference and CLI |
| [docs/remote.md](docs/remote.md) | claude.ai connector: HTTPS, OAuth, port 443, IPv4/IPv6, sni-router |
| [SECURITY.md](SECURITY.md) | Threat model and hardening — read this before exposing a shell |
| [docs/development.md](docs/development.md) | Building, testing, project layout, releasing |

## Security in one paragraph

exec-mcp is a remote shell with good manners. Whoever can call it can do whatever its OS user can do. The
command rules (`bash.ask` / `bash.deny`), protected paths and confirmations protect against mistakes; they are
not a security boundary. For real limits, run it as a dedicated unprivileged user (and/or in a container), point
`--workspace` at a project folder, and keep the remote mode behind OAuth plus `--anthropic-only`. Details are in [SECURITY.md](SECURITY.md).

## License

[Apache License 2.0](LICENSE). See [NOTICE](NOTICE).
