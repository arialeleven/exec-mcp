# Development

## Layout

```
src/ExecMcp/                the server (.NET 10)
  Host/                     registry, config, output handling, instructions, LLM access
  Tools/                    core tools, script tools
  Plugins/                  manifest, discovery, approvals, plugin host, stdio/MCP connections, CLI
  Sandbox/                  exec (Jint) and .d.ts generation
  Http/                     remote mode: Kestrel, OAuth server, PROXY protocol
  sdk/node, sdk/python      plugin SDKs shipped next to the binary
tests/ExecMcp.Tests/        xUnit tests (unit + in-process integration)
tests/e2e/                  black-box checks against a running binary (stdio and HTTP/OAuth)
deploy/sni-router/          TLS SNI router (Go) with tests
deploy/openrc, deploy/systemd
examples/tools, examples/plugins
```

## Build and test

```bash
dotnet build
dotnet test                                   # ~100 tests, ~10 s
EXEC_MCP_RG=none dotnet test                  # same tests against the built-in search instead of ripgrep
(cd deploy/sni-router && go test ./...)
python3 tests/e2e/stdio_check.py src/ExecMcp/bin/Debug/net10.0/exec-mcp
```

Linux is the supported and tested platform. Plugin conformance tests need `node` and
`python3`:

```bash
EXEC_MCP_HOME=$(mktemp -d) sh -c 'mkdir -p $EXEC_MCP_HOME/plugins && cp -r examples/plugins/{notes,wordcount,echo} $EXEC_MCP_HOME/plugins/ &&
  for p in notes wordcount echo; do exec-mcp plugin approve $p --yes && exec-mcp plugin test $p; done'
```

## Release builds

```bash
./build.sh                 # tests, then dist/: exec-mcp.tar.gz, install.sh, SHA256SUMS
SKIP_TESTS=1 ./build.sh
```

The release is one framework-dependent archive. exec-mcp is pure managed code with no native libraries, so the same
IL runs on x64 and arm64, glibc and musl. It unpacks to `exec-mcp/`, which holds `exec-mcp.dll`, `sdk/`, the examples,
`deploy/`, the licenses and `VERSION`. It also carries `sni-router/linux-amd64` and `linux-arm64`, because sni-router
is Go.

`install.sh` reuses a system .NET 10 runtime when one with ASP.NET Core is present. Otherwise it downloads
`aspnetcore-runtime-<latest 10.0.x>-<rid>.tar.gz` from builds.dotnet.microsoft.com, checks it against the SHA-512 in
Microsoft's `releases.json`, and unpacks it to `<prefix>-dotnet`. It then writes a small launcher script as
`exec-mcp`. As root on Alpine/Debian, it installs missing base libraries (`libstdc++`, OpenSSL, CA certificates).

Test the installer against local archives:

```bash
sh install.sh --from "$PWD/dist" --prefix /tmp/x/opt/exec-mcp --bin-dir /tmp/x/bin --examples
sh install.sh --from "$PWD/dist" --prefix /tmp/x/opt/exec-mcp --bin-dir /tmp/x/bin --private-runtime
sh install.sh --from "$PWD/dist" --dry-run --service --external-url https://mcp.example.com --no-tls
```

## Publishing a release

1. Bump `<Version>` in `Directory.Build.props` and add a `## x.y.z` section at the top of `CHANGELOG.md`.
2. Commit, then `git tag vX.Y.Z && git push origin vX.Y.Z`.
3. The `release` workflow checks that the tag matches the version, then runs the tests and builds the archive. It
   installs the archive on Alpine through `install.sh` and publishes a GitHub release with `exec-mcp.tar.gz`,
   `install.sh` and `SHA256SUMS`. The release notes are the CHANGELOG section. Tags with a dash (`v0.3.0-rc.1`) become
   pre-releases.

`https://github.com/<owner>/<repo>/releases/latest/download/install.sh` then always serves the newest installer, and
the installer fetches archives from the same release.

CI (`ci` workflow) runs on every push and pull request, on Linux only:
- unit tests twice, with ripgrep and with the built-in search;
- the sni-router tests;
- the end-to-end check on the installed archive, plus plugin conformance;
- the installer in clean Alpine (root) and Debian (non-root) containers, both with the private runtime.

## Adding a core tool

Core tools are `HostTool` records registered in `Program.cs`. A handler gets a `ToolCall` (validated args, origin,
cancellation) and returns a `ToolOutcome`. Throw `ToolException(code, message)` for errors with a next step. The
registry handles schema validation, confirmation, retry, redaction, spilling and `<untrusted>` wrapping. Most new
tools should be [script tools](script-tools.md) or [plugins](plugins.md) instead.
