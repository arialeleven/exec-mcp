#!/usr/bin/env bash
# Builds the release into dist/:
#   exec-mcp.tar.gz   one framework-dependent archive for every Linux (x64/arm64, glibc/musl);
#                     install.sh provides the .NET runtime when the machine has none
#   install.sh        the installer
#   SHA256SUMS
#
#   ./build.sh              tests + archive
#   SKIP_TESTS=1 ./build.sh archive only
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
DIST=dist
rm -rf "$DIST"; mkdir -p "$DIST"

if [ "${SKIP_TESTS:-0}" != 1 ]; then
  echo "== tests"
  dotnet test tests/ExecMcp.Tests -c Release --nologo -v quiet
  if command -v go >/dev/null; then (cd deploy/sni-router && go test ./...); fi
fi

echo "== exec-mcp $VERSION"
stage="$DIST/stage"
out="$stage/exec-mcp"
# Framework-dependent and portable: the same IL runs on every RID; no apphost, the installer writes a launcher.
dotnet publish src/ExecMcp -c Release -o "$out" --nologo -v quiet -p:UseAppHost=false -p:DebugType=none

if command -v go >/dev/null; then
  for arch in amd64 arm64; do
    (cd deploy/sni-router && CGO_ENABLED=0 GOOS=linux GOARCH=$arch go build -trimpath -ldflags="-s -w" -o "../../$out/sni-router/linux-$arch" .)
  done
fi

echo "$VERSION" > "$out/VERSION"
cp README.md LICENSE NOTICE CHANGELOG.md SECURITY.md "$out/"
cp -r third_party "$out/"
mkdir -p "$out/examples" "$out/deploy"
cp -r examples/tools examples/plugins "$out/examples/"
cp -r deploy/openrc deploy/systemd deploy/config.example.json "$out/deploy/"
find "$out/examples" -name node_modules -prune -exec rm -rf {} + -o -name tools.json -delete

tar -C "$stage" -czf "$DIST/exec-mcp.tar.gz" exec-mcp
rm -rf "$stage"
cp install.sh "$DIST/install.sh"
(cd "$DIST" && sha256sum exec-mcp.tar.gz install.sh > SHA256SUMS)
echo "== done"
ls -la "$DIST"
