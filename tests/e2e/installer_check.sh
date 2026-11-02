#!/bin/sh
# Installs exec-mcp from local archives the way users do, then checks it. Used by CI in clean containers.
#
#   sh tests/e2e/installer_check.sh /path/to/dist [root|user]
#
# Every step's output is kept; on failure the step name and the tail of its output are printed as a
# GitHub Actions ::error:: annotation, so the cause is visible without downloading the job log.
set -u
DIST=$1
MODE=${2:-root}
LOG=$(mktemp)
SRC=$(cd "$(dirname "$0")/../.." && pwd)

step() { # name command...
  name=$1; shift
  printf '== %s\n' "$name"
  if "$@" >"$LOG" 2>&1; then
    cat "$LOG"
  else
    code=$?
    cat "$LOG"
    tail=$(tail -n 25 "$LOG" | sed 's/%/%25/g' | awk '{printf "%s%%0A", $0}')
    echo "::error title=installer check: $name (exit $code)::$tail"
    exit 1
  fi
}

if [ "$MODE" = root ]; then BIN=/usr/local/bin; else BIN=$HOME/.local/bin; fi

step "install" sh "$SRC/install.sh" --from "$DIST" --examples
step "version" "$BIN/exec-mcp" version
step "tools list" "$BIN/exec-mcp" tools list
step "end-to-end" python3 "$SRC/tests/e2e/stdio_check.py" "$BIN/exec-mcp"
step "re-run updates in place" sh -c "sh '$SRC/install.sh' --from '$DIST' | tee /dev/stderr | grep -q 'updated'"
step "uninstall" sh "$SRC/install.sh" --uninstall
step "binary gone" sh -c "! test -e '$BIN/exec-mcp'"
echo "installer check passed ($MODE)"
