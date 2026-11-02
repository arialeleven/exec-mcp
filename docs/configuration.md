# Configuration

## Command line

```text
exec-mcp [--workspace DIR] [--dev] [--verbose]          MCP over stdio
exec-mcp --http ADDR --external-url URL [--tls-cert PEM --tls-key PEM] [--anthropic-only] [--proxy-protocol] [--no-auth]
exec-mcp tools  list | new <name> [--lang sh|py|js|ps1] | run <name> ['{json}']
exec-mcp plugin list | approve <name> [--yes] | build <name|dir> | test <name|dir>
exec-mcp version
```

| Option | |
| --- | --- |
| `--workspace DIR` | Base for relative paths, Bash's starting directory, and where AGENTS.md / CLAUDE.md are read from (with its parents). Default: `EXEC_MCP_WORKSPACE` or the current directory. |
| `--dev` | Hot-reload plugins on file changes; skip plugin file-hash checks. |
| `--http ADDR` | Serve Streamable HTTP at `/mcp`: `:9443` (all interfaces, IPv4+IPv6), `127.0.0.1:9443`, `[::]:9443`. |
| `--external-url URL` | Public base URL as clients see it (OAuth metadata). |
| `--tls-cert/--tls-key` | PEM files; re-read on change. |
| `--anthropic-only` | `/mcp`, `/token` and `/register` are accepted only from `160.79.104.0/21` and loopback. |
| `--proxy-protocol` | Accept PROXY v1 headers from loopback (behind sni-router). |
| `--no-auth` | Disable OAuth. Only for a trusted network or another auth layer in front. |

Environment variables:

| Variable | |
| --- | --- |
| `EXEC_MCP_HOME` | Config and state folder (default `~/.exec-mcp`). |
| `EXEC_MCP_PASSWORD` | Owner password for the OAuth consent page. |
| `EXEC_MCP_RG` | Path to ripgrep, or `none` to force the built-in search. |
| `ANTHROPIC_API_KEY` | Enables `Task` and `ctx.llm` through the Anthropic API. |

## `~/.exec-mcp/config.json`

All keys are optional. Comments and trailing commas are allowed.

```jsonc
{
  "bash": {
    "defaultAction": "allow",          // allow | ask | deny for commands no rule matches
    "allow": ["git status", "npm test"],
    "ask":   ["rm -rf", "find -delete", "git push --force", "git push -f", "git reset --hard", "sudo",
              "curl", "wget", "ssh", "scp", "nc", "mkfs", "dd", "shutdown", "reboot"],   // the defaults
    "deny":  [],
    "askWithoutElicitation": "deny",   // what "ask" means when the client cannot ask (claude.ai): deny | allow
    "maxOutputChars": 30000,
    "shell": null                      // bash | sh | a full path
  },
  "confirmDestructive": "indirect",    // never | indirect (calls from exec/Task) | always
  "untrustedWrap": true,               // wrap third-party text in <untrusted>
  "tools": {
    "disabled": ["TodoWrite"],         // core tools to leave out
    "dirs": ["/srv/admin-tools"],      // more script-tool folders (~/.exec-mcp/tools is always used)
    "protected": ["~/.ssh/**", "/etc/shadow"]   // file tools refuse these; exec-mcp's own secrets are always protected
  },
  "llm": { "provider": "auto", "mainModel": "claude-opus-5-5", "smallModel": "claude-haiku-5-5", "maxTurns": 50 },
  "sandbox": { "timeoutMs": 60000, "memoryMb": 256, "maxOutputChars": 30000 },
  "plugins": {
    "db": { "enabled": true, "config": { "maxRows": 500 }, "secrets": { "DATABASE_URL": "postgres://…" } }
  }
}
```

A project's `.exec-mcp/config.json` may only set `plugins.<name>.config`. Policies, secrets and tool folders are
user-level only, so a cloned repository cannot loosen them.

`protected` covers Read, Edit and Write. Bash can still read anything its OS user can; see
[SECURITY.md](../SECURITY.md).

`llm.provider`: `auto` tries, in order:
1. MCP sampling, for `ctx.llm` only;
2. the Anthropic API, if a key is set;
3. the `claude` CLI.

You can also pin one with `anthropic`, `claude-cli`, `sampling` or `none`.

## Files

| Path | |
| --- | --- |
| `~/.exec-mcp/config.json` | configuration |
| `~/.exec-mcp/tools/` | script tools |
| `~/.exec-mcp/plugins/` | user plugins |
| `~/.exec-mcp/approvals.json` | plugin approvals (lockfile) |
| `~/.exec-mcp/http-password`, `oauth-state.json` | remote mode secrets (mode 600, protected) |
| `~/.exec-mcp/cache/`, `state/` | plugin cache and state |
| `<workspace>/.exec-mcp/artifacts/` | spilled long outputs, plugin artifacts (git-ignored automatically) |
