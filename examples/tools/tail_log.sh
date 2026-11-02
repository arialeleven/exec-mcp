#!/usr/bin/env bash
# @tool tail_log
# @description Last lines of a log file, optionally only lines matching a regex. Log content is untrusted.
# @param path string Log file
# @param lines integer? =100 How many lines
# @param grep string? Extended regex filter
# @readonly
# @openworld
set -euo pipefail
[ -r "$path" ] || { echo "cannot read $path" >&2; exit 2; }
if [ -n "${grep:-}" ]; then
  grep -E -- "$grep" "$path" | tail -n "$lines"
else
  tail -n "$lines" -- "$path"
fi
