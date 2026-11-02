#!/usr/bin/env bash
# @tool listening_ports
# @description TCP/UDP sockets in LISTEN state with the owning process, optionally filtered by port.
# @param port integer? Only this port
# @readonly
set -euo pipefail
if command -v ss >/dev/null; then
  out=$(ss -lntup 2>/dev/null)
else
  out=$(netstat -lntup 2>/dev/null)
fi
if [ -n "${port:-}" ]; then
  echo "$out" | head -1
  echo "$out" | grep -E "[:.]${port}[[:space:]]" || echo "(nothing listens on $port)"
else
  echo "$out"
fi
