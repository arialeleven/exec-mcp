# Changelog

## 0.1.0

First release.

- **Core tools** in the style of Claude Code:
  - `Read`, `Edit`, `MultiEdit` and `Write`. Editing requires a fresh `Read`, ambiguous matches name their lines, and the fuzzy fallback reports what it normalised. BOM, CRLF and curly quotes are kept.
  - `Glob` and `Grep`, using ripgrep or a built-in fallback, `.gitignore`-aware.
  - `Bash` and `BashOutput`. The session keeps cwd, exported variables, functions and aliases; a timeout kills the command, not the session; background jobs are supported.
  - `TodoWrite`, `Task` (subagents) and `ToolSearch` (deferred tools).
- **Script tools**: one file in `~/.exec-mcp/tools/`, in any language, with a comment header, becomes a tool. It is hot-reloaded with `tools/list_changed`, needs no rebuild or restart, and is managed with `exec-mcp tools list | new | run`.
- **Plugins** (spec v0.1):
  - runtimes: node, python, stdio, and existing MCP servers;
  - manifest with host-enforced permissions, approvals with file hashes, limits, crash recovery, hot reload;
  - Node and Python SDKs, and `exec-mcp plugin test`.
- **Code mode**: `exec` runs JavaScript in a sandbox with typed namespaces (`core`, `tools`, plugins). Everything goes through the same permission checks.
- **Remote mode for claude.ai**:
  - Streamable HTTP with a built-in OAuth 2.1 server (DCR, PKCE, rotating refresh tokens, owner password);
  - `--anthropic-only`, PROXY protocol, reloading TLS certificates;
  - `sni-router` for sharing port 443.
- **Guard rails**: canonical command rules (ask/deny), protected paths, confirmation of destructive indirect calls, secret redaction, `<untrusted>` wrapping of third-party text.
- **Distribution**:
  - one portable archive for every Linux (x64/arm64, glibc/musl);
  - `install.sh` for one-command install and update, which fetches a private .NET runtime when needed (SHA-512 verified) and sets up a systemd/OpenRC service;
  - `--service --letsencrypt` turns a fresh server into a claude.ai connector in one command: `<ip>.sslip.io` name, auto-renewing certificate, ufw, port checks;
  - Linux CI with installer tests on Alpine and Debian.
- Apache-2.0. Third-party licenses ship with every archive.
