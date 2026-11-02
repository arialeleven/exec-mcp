# Security

exec-mcp lets a language model run commands and change files. Treat access to it like SSH access for its OS user.

## Threat model

| Threat | What exec-mcp does | What you should do |
| --- | --- | --- |
| Someone else reaches the remote endpoint | OAuth 2.1 with an owner-password consent page, rate-limited sign-in, short-lived bearer tokens, `--anthropic-only` allowlist, redirects limited to claude.ai and loopback | Keep a strong `EXEC_MCP_PASSWORD`; delete `oauth-state.json` to revoke all tokens |
| The model makes a mistake | `Edit`/`Write` need a fresh `Read`; ask/deny rules on canonical command forms; destructive calls from `exec`/`Task` need confirmation; protected paths | Put destructive prefixes into `bash.deny`; review what the model does |
| Prompt injection via web pages, logs, files from others | `<untrusted>` wrapping of third-party output, plus instructions to treat it as data | Do not give the same session both secrets and untrusted inputs; prefer an unprivileged user |
| A plugin misbehaves | Host-enforced permissions (net, fs, exec, secrets), approval with file hashes, separate processes, limits | Read the permission list before `plugin approve` |
| Secrets leak into the transcript | Values of declared plugin secrets are redacted from all output and logs; exec-mcp's own secret files are protected from file tools | Do not keep unrelated secrets readable by the exec-mcp user |

## Not security boundaries

- **Command rules** (`bash.ask`, `bash.deny`) match the canonical form of each simple command. `rm --recursive --force`
  counts as `rm -rf`, and wrappers like `sudo` or `timeout` are seen through. But anything can be rewritten: `python3 -c`,
  `base64 -d | sh`, a script file. They stop accidents, not intent.
- **Protected paths** apply to Read/Edit/Write. `Bash` can still `cat` them if the OS user may.
- **`permissions.net` for plugins** is enforced for `ctx.fetch`. A plugin that opens its own sockets (a database
  driver) is limited only by the OS.

## Hardening checklist

1. Run as a dedicated user without sudo, with `--workspace` pointing at a project folder (systemd unit and OpenRC
   script in [deploy/](deploy)). Running as root, for example to administer a VPS, is a deliberate choice; then at least
   add `tools.protected` for `~/.ssh/**` and other credentials.
2. In remote mode: OAuth on (the default), `--anthropic-only`, TLS from a public CA, port 443 shared via sni-router
   rather than exposing other ports.
3. Add `bash.deny` for what must never run, and consider `"defaultAction": "ask"` with an allowlist.
4. Keep `untrustedWrap` on.
5. For hard isolation, run exec-mcp in a container or VM with only the mounts and network it needs.

## Reporting a vulnerability

Please open a private security advisory on GitHub (Security → Report a vulnerability) instead of a public issue.
