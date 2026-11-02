#!/bin/sh
# exec-mcp installer and updater for Linux (x64/arm64, glibc or musl).
#
#   curl -fsSL https://github.com/arialeleven/exec-mcp/releases/latest/download/install.sh | sh
#
# A claude.ai connector on a fresh server (HTTPS on 443, certificate, OAuth, service) in one line:
#
#   curl -fsSL https://github.com/arialeleven/exec-mcp/releases/latest/download/install.sh | sudo sh -s -- --service --letsencrypt
#
# exec-mcp ships as one portable .NET archive. If the machine has no ASP.NET Core 10 runtime, the installer
# downloads Microsoft's runtime (verified against the official SHA-512) into a private folder; nothing
# system-wide is touched except, as root, missing base libraries (libstdc++, OpenSSL, CA certificates).
# Re-running the command updates exec-mcp and the private runtime, and restarts the service.
#
# Options go after `sh -s --`:
#   --version TAG        release tag (default: latest)
#   --prefix DIR         install dir (root: /opt/exec-mcp, user: ~/.local/share/exec-mcp)
#   --bin-dir DIR        where the exec-mcp launcher goes (root: /usr/local/bin, user: ~/.local/bin)
#   --from DIR|URL       take exec-mcp.tar.gz and SHA256SUMS from here instead of GitHub
#   --private-runtime    always use the private runtime, even if a suitable system one exists
#   --examples           copy example script tools into ~/.exec-mcp/tools (existing files are kept)
#   --service            run remote mode as a systemd/OpenRC service (root), with:
#     --letsencrypt        get and auto-renew a Let's Encrypt certificate (certbot, needs port 80 once);
#                          without --external-url the name is <public-ipv4>.sslip.io
#     --email ADDR         contact address for Let's Encrypt (optional)
#     --external-url URL   public https URL clients use (no port: claude.ai only connects to 443)
#     --tls-cert/--tls-key PEM files you already have; or --no-tls when a TLS proxy (sni-router, nginx) is in front
#     --listen ADDR        default :443 with TLS, 127.0.0.1:9443 with --no-tls
#     --workspace DIR      default /srv/exec-mcp
#     --user NAME          service user (default exec-mcp, created if missing; "root" to administer the host)
#     --proxy-protocol     behind sni-router
#     --password PW        owner sign-in password (default: generated and printed once)
#   --uninstall          remove exec-mcp, its runtime, launcher and service (keeps ~/.exec-mcp, /etc/exec-mcp)
#   --dry-run            print what would be done
set -eu

REPO="${EXEC_MCP_REPO:-arialeleven/exec-mcp}"
DOTNET_CHANNEL=10.0
VERSION=latest PREFIX="" BIN_DIR="" FROM="" PRIVATE_RT=0 EXAMPLES=0 SERVICE=0 UNINSTALL=0 DRY=0
LE=0 EMAIL="" EXTERNAL_URL="" TLS_CERT="" TLS_KEY="" NO_TLS=0 LISTEN="" WORKSPACE=/srv/exec-mcp SVC_USER=exec-mcp PROXY=0 PASSWORD=""

say() { printf '%s\n' "exec-mcp: $*"; }
die() { printf '%s\n' "exec-mcp: error: $*" >&2; exit 1; }
run() { if [ "$DRY" = 1 ]; then printf '  + %s\n' "$*" >&2; else "$@"; fi; }   # dry-run lines go to stderr: callers may redirect stdout

while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION=$2; shift ;;
    --prefix) PREFIX=$2; shift ;;
    --bin-dir) BIN_DIR=$2; shift ;;
    --from) FROM=$2; shift ;;
    --private-runtime) PRIVATE_RT=1 ;;
    --examples) EXAMPLES=1 ;;
    --service) SERVICE=1 ;;
    --external-url) EXTERNAL_URL=$2; shift ;;
    --tls-cert) TLS_CERT=$2; shift ;;
    --tls-key) TLS_KEY=$2; shift ;;
    --no-tls) NO_TLS=1 ;;
    --listen) LISTEN=$2; shift ;;
    --workspace) WORKSPACE=$2; shift ;;
    --user) SVC_USER=$2; shift ;;
    --proxy-protocol) PROXY=1 ;;
    --password) PASSWORD=$2; shift ;;
    --letsencrypt) LE=1 ;;
    --email) EMAIL=$2; shift ;;
    --uninstall) UNINSTALL=1 ;;
    --dry-run) DRY=1 ;;
    -h|--help) sed -n '2,40p' "$0" 2>/dev/null || echo "see https://github.com/$REPO#install"; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
  shift
done

[ "$(uname -s)" = Linux ] || die "only Linux is supported (got $(uname -s))"
IS_ROOT=0; [ "$(id -u)" = 0 ] && IS_ROOT=1
if [ "$IS_ROOT" = 1 ]; then
  : "${PREFIX:=/opt/exec-mcp}"; : "${BIN_DIR:=/usr/local/bin}"
else
  : "${PREFIX:=$HOME/.local/share/exec-mcp}"; : "${BIN_DIR:=$HOME/.local/bin}"
fi
RUNTIME_DIR="$PREFIX-dotnet"   # survives updates of $PREFIX

init_system() {
  if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then echo systemd
  elif command -v rc-service >/dev/null 2>&1; then echo openrc
  else echo none; fi
}

# ---- uninstall ---------------------------------------------------------------------
if [ "$UNINSTALL" = 1 ]; then
  case "$(init_system)" in
    systemd) if [ -f /etc/systemd/system/exec-mcp.service ]; then run systemctl disable --now exec-mcp || true; run rm -f /etc/systemd/system/exec-mcp.service; run systemctl daemon-reload; fi ;;
    openrc) if [ -f /etc/init.d/exec-mcp ]; then run rc-service exec-mcp stop || true; run rc-update del exec-mcp default || true; run rm -f /etc/init.d/exec-mcp; fi ;;
  esac
  run rm -rf "$PREFIX" "$PREFIX.prev" "$RUNTIME_DIR"
  run rm -f "$BIN_DIR/exec-mcp" "$BIN_DIR/sni-router"
  say "removed (configuration in ~/.exec-mcp and /etc/exec-mcp was kept)"
  exit 0
fi

# ---- platform ----------------------------------------------------------------------
case "$(uname -m)" in
  x86_64|amd64) ARCH=x64 GOARCH=amd64 ;;
  aarch64|arm64) ARCH=arm64 GOARCH=arm64 ;;
  *) die "unsupported CPU $(uname -m) (x86_64 and aarch64 are supported)" ;;
esac
if ls /lib/ld-musl-*.so.1 >/dev/null 2>&1; then LIBC=musl RID="linux-musl-$ARCH"; else LIBC=glibc RID="linux-$ARCH"; fi

fetch() { # source dest
  case "$1" in
    http://*|https://*)
      if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 -o "$2" "$1"
      elif command -v wget >/dev/null 2>&1; then wget -q -O "$2" "$1"
      else die "need curl or wget"; fi ;;
    *) cp "$1" "$2" ;;
  esac
}
sum() { # algo file
  if command -v "${1}sum" >/dev/null 2>&1; then "${1}sum" "$2" | cut -d' ' -f1
  else shasum -a "${1#sha}" "$2" | cut -d' ' -f1; fi
}

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT INT TERM

# ---- base libraries the .NET runtime needs (root only, only when missing) ----------
ensure_libs() {
  [ "$IS_ROOT" = 1 ] || return 0
  if command -v apk >/dev/null 2>&1; then
    missing=""
    for p in libgcc libstdc++ ca-certificates; do apk info -e "$p" >/dev/null 2>&1 || missing="$missing $p"; done
    if [ -n "$missing" ]; then say "installing$missing"; run apk add --no-cache $missing >/dev/null; fi
  elif command -v apt-get >/dev/null 2>&1; then
    missing=""
    for p in libstdc++6 ca-certificates; do dpkg -s "$p" >/dev/null 2>&1 || missing="$missing $p"; done
    ldconfig -p 2>/dev/null | grep -q 'libssl\.so\.3' || missing="$missing libssl3"
    if [ -n "$missing" ]; then
      say "installing$missing"
      run apt-get update -qq && run env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends $missing >/dev/null \
        || say "warning: could not install$missing (libssl3t64 on newer Ubuntu?) — install it yourself if exec-mcp fails to start"
    fi
  fi
}

# ---- .NET runtime ------------------------------------------------------------------
has_aspnet10() { "$1" --list-runtimes 2>/dev/null | grep -q "^Microsoft.AspNetCore.App $DOTNET_CHANNEL\."; }

DOTNET=""
if [ "$PRIVATE_RT" = 0 ]; then
  for d in "${DOTNET_ROOT:-}/dotnet" "$(command -v dotnet 2>/dev/null || true)" /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /usr/local/share/dotnet/dotnet; do
    [ -x "$d" ] && has_aspnet10 "$d" && { DOTNET=$d; break; }
  done
fi

install_runtime() {
  ensure_libs
  rt_version=$(fetch_text "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/$DOTNET_CHANNEL/latest.version" | tr -d '\r\n ')
  [ -n "$rt_version" ] || die "cannot determine the latest .NET $DOTNET_CHANNEL runtime"
  if [ -x "$RUNTIME_DIR/dotnet" ] && "$RUNTIME_DIR/dotnet" --list-runtimes 2>/dev/null | grep -q "^Microsoft.AspNetCore.App $rt_version "; then
    say ".NET runtime $rt_version (private) is current"; return
  fi
  file="aspnetcore-runtime-$rt_version-$RID.tar.gz"
  say "downloading .NET runtime $rt_version for $RID (private, ~45 MB)"
  fetch "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$DOTNET_CHANNEL/releases.json" "$TMP/releases.json"
  want=$(awk -v f="/$file\"" 'index($0, f) { getline; sub(/.*"hash": *"/, ""); sub(/".*/, ""); print; exit }' "$TMP/releases.json")
  [ -n "$want" ] || die "no checksum for $file in Microsoft's release metadata"
  fetch "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/$rt_version/$file" "$TMP/$file"
  [ "$(sum sha512 "$TMP/$file")" = "$want" ] || die "checksum mismatch for $file"
  run rm -rf "$RUNTIME_DIR.new"
  run mkdir -p "$RUNTIME_DIR.new"
  run tar -xzf "$TMP/$file" -C "$RUNTIME_DIR.new"
  run rm -rf "$RUNTIME_DIR"
  run mv "$RUNTIME_DIR.new" "$RUNTIME_DIR"
  say ".NET runtime $rt_version installed in $RUNTIME_DIR"
}
fetch_text() { fetch "$1" "$TMP/text" && cat "$TMP/text"; }

if [ -n "$DOTNET" ]; then
  say "using system .NET runtime: $DOTNET"
  RUNTIME_DIR=""
else
  install_runtime
  DOTNET="$RUNTIME_DIR/dotnet"
fi

# ---- exec-mcp ----------------------------------------------------------------------
if [ -z "$FROM" ]; then
  if [ "$VERSION" = latest ]; then FROM="https://github.com/$REPO/releases/latest/download"
  else FROM="https://github.com/$REPO/releases/download/$VERSION"; fi
fi
say "downloading exec-mcp from $FROM"
fetch "$FROM/exec-mcp.tar.gz" "$TMP/exec-mcp.tar.gz" || die "download failed: $FROM/exec-mcp.tar.gz"
fetch "$FROM/SHA256SUMS" "$TMP/SHA256SUMS" || die "download failed: $FROM/SHA256SUMS"
want=$(grep ' exec-mcp.tar.gz$' "$TMP/SHA256SUMS" | cut -d' ' -f1)
[ -n "$want" ] && [ "$want" = "$(sum sha256 "$TMP/exec-mcp.tar.gz")" ] || die "checksum mismatch for exec-mcp.tar.gz"

tar -xzf "$TMP/exec-mcp.tar.gz" -C "$TMP"
[ -f "$TMP/exec-mcp/exec-mcp.dll" ] || die "archive does not contain exec-mcp/exec-mcp.dll"
NEW_VERSION=$(cat "$TMP/exec-mcp/VERSION" 2>/dev/null || echo "?")
OLD_VERSION=$(cat "$PREFIX/VERSION" 2>/dev/null || true)

# Atomic swap; the previous version stays in $PREFIX.prev.
run mkdir -p "$(dirname "$PREFIX")" "$BIN_DIR"
run rm -rf "$PREFIX.new" "$PREFIX.prev"
run mv "$TMP/exec-mcp" "$PREFIX.new"
if [ -d "$PREFIX" ]; then run mv "$PREFIX" "$PREFIX.prev"; fi
run mv "$PREFIX.new" "$PREFIX"

LAUNCHER="#!/bin/sh
# exec-mcp launcher written by install.sh
${RUNTIME_DIR:+export DOTNET_ROOT=\"$RUNTIME_DIR\"
}export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec \"$DOTNET\" \"$PREFIX/exec-mcp.dll\" \"\$@\""
if [ "$DRY" = 1 ]; then printf '%s\n' "$LAUNCHER"; else
  rm -f "$BIN_DIR/exec-mcp"; printf '%s\n' "$LAUNCHER" > "$BIN_DIR/exec-mcp"; chmod 755 "$BIN_DIR/exec-mcp"
fi
if [ -f "$PREFIX/sni-router/linux-$GOARCH" ]; then
  run chmod 755 "$PREFIX/sni-router/linux-$GOARCH"
  run ln -sf "$PREFIX/sni-router/linux-$GOARCH" "$BIN_DIR/sni-router"
fi

if [ "$DRY" = 0 ]; then
  "$BIN_DIR/exec-mcp" version >/dev/null 2>&1 || {
    ensure_libs
    "$BIN_DIR/exec-mcp" version >/dev/null 2>&1 || die "exec-mcp does not start: run $BIN_DIR/exec-mcp version to see why (missing libstdc++ or OpenSSL?)"
  }
fi
if [ -n "$OLD_VERSION" ]; then say "updated $OLD_VERSION -> $NEW_VERSION in $PREFIX ($LIBC/$ARCH)"; else say "installed $NEW_VERSION in $PREFIX ($LIBC/$ARCH)"; fi
case ":$PATH:" in *":$BIN_DIR:"*) ;; *) say "note: add $BIN_DIR to PATH" ;; esac

if [ "$EXAMPLES" = 1 ]; then
  dest="${EXEC_MCP_HOME:-$HOME/.exec-mcp}/tools"
  run mkdir -p "$dest"
  for f in "$PREFIX"/examples/tools/*; do
    case "$f" in *.md) continue ;; esac
    [ -e "$dest/$(basename "$f")" ] || run cp "$f" "$dest/"
  done
  say "example script tools copied to $dest"
fi

# ---- service (remote mode) ---------------------------------------------------------
port_busy() { # port -> 0 when something listens on it
  if command -v ss >/dev/null 2>&1; then ss -ltn | awk '{print $4}' | grep -qE "[:.]$1\$"
  else netstat -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]$1\$"; fi
}

open_firewall() { # ports...
  if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    for p in "$@"; do run ufw allow "$p/tcp" >/dev/null; done
    say "ufw: opened $*"
  fi
}

letsencrypt() {
  if [ -z "$EXTERNAL_URL" ] || [ "$EXTERNAL_URL" = auto ]; then
    # claude.ai connects over IPv4 only, so the name must resolve to the IPv4 address.
    ip=$(fetch_text https://api.ipify.org 2>/dev/null | tr -d ' \r\n' || true)
    echo "$ip" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' || die "could not detect the public IPv4 address; pass --external-url https://your.host"
    EXTERNAL_URL="https://$(echo "$ip" | tr . -).sslip.io"
    say "public name: $EXTERNAL_URL (sslip.io resolves it to $ip)"
  fi
  host=${EXTERNAL_URL#https://}; host=${host%%/*}; host=${host%%:*}

  if ! command -v certbot >/dev/null 2>&1; then
    say "installing certbot"
    if command -v apt-get >/dev/null 2>&1; then run apt-get update -qq && run env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq certbot >/dev/null
    elif command -v apk >/dev/null 2>&1; then run apk add --no-cache certbot >/dev/null
    elif command -v dnf >/dev/null 2>&1; then run dnf install -y -q certbot
    else die "install certbot, or pass --tls-cert/--tls-key"; fi
  fi
  if [ ! -d "/etc/letsencrypt/live/$host" ]; then
    port_busy 80 && die "port 80 is in use; Let's Encrypt needs it for a moment. Stop that service and re-run, or pass --tls-cert/--tls-key"
    open_firewall 80 443
    if [ -n "$EMAIL" ]; then contact="--email $EMAIL"; else contact="--register-unsafely-without-email"; fi
    say "requesting a Let's Encrypt certificate for $host"
    run certbot certonly --standalone --non-interactive --agree-tos --keep-until-expiring $contact -d "$host" >/dev/null \
      || die "certbot failed (is port 80 reachable from the internet? cloud firewalls/security groups must allow 80 and 443)"
  fi

  # The service user cannot read /etc/letsencrypt/live; a deploy hook keeps a readable copy fresh after every renewal.
  hook=/etc/letsencrypt/renewal-hooks/deploy/exec-mcp.sh
  HOOK="#!/bin/sh
# Written by the exec-mcp installer: copies the renewed certificate for the exec-mcp service user.
# exec-mcp notices the new file by itself; no restart is needed.
set -e
src=/etc/letsencrypt/live/$host
[ -z \"\${RENEWED_LINEAGE:-}\" ] || [ \"\$RENEWED_LINEAGE\" = \"\$src\" ] || exit 0
install -d -m 0750 -o $SVC_USER /etc/exec-mcp/tls
install -m 0644 -o $SVC_USER \"\$src/fullchain.pem\" /etc/exec-mcp/tls/fullchain.pem
install -m 0600 -o $SVC_USER \"\$src/privkey.pem\" /etc/exec-mcp/tls/privkey.pem"
  if [ "$DRY" = 1 ]; then printf '%s\n' "$HOOK"; else
    mkdir -p "$(dirname "$hook")"; printf '%s\n' "$HOOK" > "$hook"; chmod 755 "$hook"; RENEWED_LINEAGE="" "$hook"
  fi

  # certbot from apt/snap brings its own renewal timer; elsewhere add a daily job.
  if ! { command -v systemctl >/dev/null 2>&1 && systemctl list-timers --all 2>/dev/null | grep -q certbot; }; then
    if [ -d /etc/periodic/daily ]; then
      [ "$DRY" = 1 ] || { printf '#!/bin/sh\ncertbot renew -q\n' > /etc/periodic/daily/certbot-renew; chmod 755 /etc/periodic/daily/certbot-renew; }
    elif [ -d /etc/cron.d ]; then
      [ "$DRY" = 1 ] || printf '17 3 * * * root certbot renew -q\n' > /etc/cron.d/certbot-exec-mcp
    fi
  fi
  TLS_CERT=/etc/exec-mcp/tls/fullchain.pem
  TLS_KEY=/etc/exec-mcp/tls/privkey.pem
  say "certificate ready; renewals are automatic"
}

if [ "$SERVICE" = 1 ]; then
  [ "$IS_ROOT" = 1 ] || die "--service needs root"
  INIT=$(init_system)
  [ "$INIT" != none ] || die "neither systemd nor OpenRC found; run: exec-mcp --http … yourself"

  if [ "$SVC_USER" != root ] && ! id "$SVC_USER" >/dev/null 2>&1; then
    if command -v useradd >/dev/null 2>&1; then run useradd --system --create-home --home-dir /var/lib/exec-mcp --shell /bin/sh "$SVC_USER"
    else run adduser -S -D -h /var/lib/exec-mcp -s /bin/sh "$SVC_USER"; fi
  fi
  SVC_HOME=$(getent passwd "$SVC_USER" 2>/dev/null | cut -d: -f6); [ -n "$SVC_HOME" ] || SVC_HOME=/var/lib/exec-mcp
  run mkdir -p "$WORKSPACE" /etc/exec-mcp
  [ "$SVC_USER" = root ] || run chown "$SVC_USER" "$WORKSPACE"

  [ "$LE" = 1 ] && letsencrypt
  [ -n "$EXTERNAL_URL" ] || die "--service needs --letsencrypt or --external-url https://your.host"
  if [ "$NO_TLS" = 0 ]; then
    [ -n "$TLS_CERT" ] && [ -n "$TLS_KEY" ] || die "--service needs --letsencrypt, or --tls-cert and --tls-key (or --no-tls behind a TLS proxy)"
    : "${LISTEN:=:443}"
  else
    : "${LISTEN:=127.0.0.1:9443}"
  fi
  svc_running=0
  case "$INIT" in
    systemd) systemctl is-active --quiet exec-mcp 2>/dev/null && svc_running=1 ;;
    openrc) [ -f /etc/init.d/exec-mcp ] && rc-service exec-mcp status >/dev/null 2>&1 && svc_running=1 ;;
  esac
  lport=${LISTEN##*:}
  if [ "$svc_running" = 0 ] && port_busy "$lport"; then
    die "port $lport is already in use. claude.ai only connects to 443: free it, or share it with deploy/sni-router (see docs/remote.md)"
  fi
  [ "$lport" = 443 ] && open_firewall 443

  if [ ! -f /etc/exec-mcp/env ] || [ -n "$PASSWORD" ]; then
    [ -n "$PASSWORD" ] || PASSWORD=$(head -c 18 /dev/urandom | base64 | tr '+/' 'xy')
    if [ "$DRY" = 0 ]; then
      ( umask 077; printf 'EXEC_MCP_PASSWORD=%s\n' "$PASSWORD" > /etc/exec-mcp/env )
      [ "$SVC_USER" = root ] || chown "$SVC_USER" /etc/exec-mcp/env
    fi
    SHOW_PASSWORD=1
  fi

  ARGS="--workspace $WORKSPACE --http $LISTEN --external-url $EXTERNAL_URL --anthropic-only"
  [ "$NO_TLS" = 1 ] || ARGS="$ARGS --tls-cert $TLS_CERT --tls-key $TLS_KEY"
  [ "$PROXY" = 1 ] && ARGS="$ARGS --proxy-protocol"

  if [ "$INIT" = systemd ]; then
    CAPS=""; case "$LISTEN" in *:443|*:80) [ "$SVC_USER" = root ] || CAPS="AmbientCapabilities=CAP_NET_BIND_SERVICE" ;; esac
    UNIT="[Unit]
Description=exec-mcp MCP server
After=network-online.target
Wants=network-online.target

[Service]
User=$SVC_USER
WorkingDirectory=$WORKSPACE
Environment=HOME=$SVC_HOME
EnvironmentFile=/etc/exec-mcp/env
ExecStart=$BIN_DIR/exec-mcp $ARGS
Restart=on-failure
RestartSec=3
$CAPS

[Install]
WantedBy=multi-user.target"
    if [ "$DRY" = 1 ]; then printf '%s\n' "$UNIT"; else printf '%s\n' "$UNIT" > /etc/systemd/system/exec-mcp.service; fi
    run systemctl daemon-reload
    run systemctl enable exec-mcp
    run systemctl restart exec-mcp
  else
    SCRIPT="#!/sbin/openrc-run
name=\"exec-mcp\"
description=\"exec-mcp MCP server\"
command=\"$BIN_DIR/exec-mcp\"
command_args=\"$ARGS\"
command_user=\"$SVC_USER\"
directory=\"$WORKSPACE\"
supervisor=\"supervise-daemon\"
respawn_delay=3
capabilities=\"^cap_net_bind_service\"
output_log=\"/var/log/exec-mcp.log\"
error_log=\"/var/log/exec-mcp.log\"
depend() { need net; after firewall; }
start_pre() {
  set -a; . /etc/exec-mcp/env; set +a
  export HOME=\"$SVC_HOME\"
  checkpath -f -m 0600 -o $SVC_USER /var/log/exec-mcp.log
}"
    if [ "$DRY" = 1 ]; then printf '%s\n' "$SCRIPT"; else printf '%s\n' "$SCRIPT" > /etc/init.d/exec-mcp; chmod 755 /etc/init.d/exec-mcp; fi
    run rc-update add exec-mcp default
    run rc-service exec-mcp restart
  fi
  say "service exec-mcp ($INIT) listening on $LISTEN as $SVC_USER"
  say "connector URL for claude.ai: ${EXTERNAL_URL%/}/mcp"
  if [ "${SHOW_PASSWORD:-0}" = 1 ]; then say "owner password (stored in /etc/exec-mcp/env): $PASSWORD"; fi
else
  case "$(init_system)" in
    systemd) if systemctl is-active --quiet exec-mcp 2>/dev/null; then run systemctl restart exec-mcp; say "service restarted"; fi ;;
    openrc) if [ -f /etc/init.d/exec-mcp ] && rc-service exec-mcp status >/dev/null 2>&1; then run rc-service exec-mcp restart >/dev/null; say "service restarted"; fi ;;
  esac
fi

say "next steps:"
say "  Claude Code:  claude mcp add exec -- $BIN_DIR/exec-mcp --workspace \"\$PWD\""
say "  new tool:     exec-mcp tools new my_tool --lang sh"
say "  docs:         https://github.com/$REPO#readme"
