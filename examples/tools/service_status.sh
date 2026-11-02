#!/usr/bin/env bash
# @tool service_status
# @description Status of a system service (systemd or OpenRC) and its last log lines.
# @param name string Service name, e.g. nginx
# @param lines integer? =20 Log lines to show
# @readonly
set -uo pipefail
if command -v systemctl >/dev/null && [ -d /run/systemd/system ]; then
  systemctl status --no-pager --lines="$lines" -- "$name"
elif command -v rc-service >/dev/null; then
  rc-service "$name" status
  for f in "/var/log/$name.log" "/var/log/$name/$name.log"; do
    if [ -f "$f" ]; then echo "--- $f"; tail -n "$lines" "$f"; break; fi
  done
  exit 0
else
  echo "neither systemd nor OpenRC found" >&2; exit 1
fi
