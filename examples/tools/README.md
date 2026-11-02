# Example script tools

Copy any of these into `~/.exec-mcp/tools/` (or a folder listed in `tools.dirs`) and a running exec-mcp
picks it up immediately. See [docs/script-tools.md](../../docs/script-tools.md) for the header format.

| Tool | What it does |
| --- | --- |
| `disk_usage.sh` | Filesystem totals and the largest entries of a directory |
| `listening_ports.sh` | Listening sockets with their processes |
| `service_status.sh` | systemd / OpenRC service status plus recent log lines |
| `tail_log.sh` | Tail of a log, optionally filtered (marked `@openworld`: logs carry third-party text) |
| `http_check.mjs` | Status, timing and headers of a URL, as JSON |
