#!/usr/bin/env bash
# @tool disk_usage
# @description Disk usage of a directory: filesystem totals, then the largest entries, biggest first.
# @param path string Directory to measure
# @param depth integer? =1 How many levels down to sum
# @param top integer? =15 How many entries to list
# @readonly
# @timeout 60s
set -euo pipefail
[ -d "$path" ] || { echo "not a directory: $path" >&2; exit 2; }
df -h -- "$path" | tail -n +1
echo
du -h -x -d "$depth" -- "$path" 2>/dev/null | sort -rh | head -n "$top"
