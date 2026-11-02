# Plugins

Plugins are the heavyweight extension mechanism (spec v0.1). Use them when a tool keeps state (connections,
caches), needs secrets, or comes from someone else. For a quick helper, a [script tool](script-tools.md) is
simpler.

A plugin describes its tools once. The host shows them as MCP tools (`db_query`) and as typed functions in the
sandbox (`db.query`). All of the plugin's side effects go through the host, which enforces the permissions
declared in the manifest.

## Layout and discovery

```
.exec-mcp/plugins/db/          # project plugins (first)
~/.exec-mcp/plugins/db/        # user plugins
node_modules/exec-mcp-plugin-*  # npm packages (local and global)
site-packages/exec_mcp_plugin_* # pip packages
  plugin.json   manifest (required)
  index.js      entry point (node), plugin.py (python), any binary (stdio)
  tools.json    written by `exec-mcp plugin build`, for discovery without starting the plugin
```

## Manifest

```json
{
  "name": "db",
  "version": "0.3.0",
  "apiVersion": "^0.1",
  "description": "Read-only Postgres access: schema, queries, EXPLAIN.",
  "runtime": { "type": "node", "entry": "index.js" },
  "permissions": {
    "net":     { "allow": ["localhost:5432"] },
    "fs":      { "read": ["${workspace}/**"], "write": ["${cache}/**", "${artifacts}/**"] },
    "exec":    [],
    "secrets": ["DATABASE_URL"],
    "llm":     []
  },
  "config": { "type": "object", "properties": { "maxRows": { "type": "integer", "default": 200 } } },
  "exposure": { "namespace": "db", "mcp": "deferred", "sandbox": true },
  "limits": { "timeoutMs": 30000, "maxOutputTokens": 4000, "concurrency": 4, "ratePerMin": 120 },
  "dependsOn": []
}
```

- `runtime.type`:
  - `node`: the plugin uses the bundled `@exec-mcp/plugin-sdk`, and zod is included;
  - `python`: the bundled `exec_mcp_plugin`;
  - `stdio`: any executable that speaks the protocol below, set with `command` and `args`;
  - `mcp`: wraps an existing MCP server. Permissions then apply only at the process level, and secrets go to its environment.
- `exposure.mcp`:
  - `eager`: the schema is always in `tools/list`;
  - `deferred`: only the name and a one-line description appear in the server instructions; ToolSearch loads the rest;
  - `hidden`: the tool exists only in the sandbox.
- `permissions`: anything not listed is denied.
  - `net` takes `host:port`, `*.domain` or `*`, and is re-checked on every redirect.
  - `fs` takes globs with `${workspace}`, `${cache}`, `${artifacts}`, `${plugin}` and `${home}`.
  - `exec` lists command names that may run without a shell.
  - `secrets` come from `plugins.<name>.secrets` or the environment, and are redacted from all output.
  - `llm` lists the model tiers the plugin may use (`small`, `main`).

## Lifecycle

discover → manifest (schema, `apiVersion`) → config (defaults, validation, secrets present) → **approve** → load
(lazy for deferred plugins that have `tools.json`) → setup (10 s) → ready → dispose.

```bash
exec-mcp plugin list            # status of every plugin found
exec-mcp plugin approve db      # shows the permissions (or what changed since the last approval) and asks y/N
exec-mcp plugin build db        # writes tools.json and prints the sandbox .d.ts
exec-mcp plugin test db         # conformance checks
```

Approvals live in `~/.exec-mcp/approvals.json`: the permissions plus a SHA-256 over the plugin's files. A plugin
whose permissions or files changed is not loaded until you approve it again. `--dev` skips the file hash and
hot-reloads plugins on change.

Each plugin runs in its own process. A crash triggers a restart with backoff, and three crashes in a row disable
the plugin; calls to its tools then explain why. If an error is `retryable` and the tool is readOnly or idempotent,
the host retries it once.

## Node SDK

```js
import { definePlugin, tool, z, ToolError } from "@exec-mcp/plugin-sdk";

export default definePlugin({
  async setup(ctx) { /* open connections; ctx.secrets.get("DATABASE_URL") */ },
  tools: {
    query: tool({
      description: "Run a read-only SQL query. Returns rows as a compact table.",
      input: z.object({ sql: z.string().min(1), limit: z.number().int().max(1000).optional() }),
      output: z.object({ columns: z.array(z.string()), rows: z.array(z.array(z.unknown())), truncated: z.boolean() }),
      annotations: { readOnlyHint: true, idempotentHint: true },
      examples: [{ input: { sql: "select 1" } }],
      async run({ sql, limit }, ctx) { /* … */ },
      format: (out) => "compact text for the model",
    }),
  },
  async dispose() {},
});
```

`ctx` provides:
- `config` and `secrets.get`;
- `fetch`, `fs` (`readText`, `writeText`, …) and `exec`;
- `cache` (with TTL), `state` (persistent KV) and `artifacts.write`;
- `llm.complete`, available only when the manifest declares `llm`;
- `call` for tools of plugins listed in `dependsOn`;
- `progress` and `log`, which go to stderr;
- `signal`, which a plugin **must** honour.

Types are in `src/ExecMcp/sdk/node/index.d.ts`.

## Python SDK

```python
from exec_mcp_plugin import Plugin, ToolError
plugin = Plugin()

@plugin.tool(description="Word statistics.", input={"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]},
             annotations={"readOnlyHint": True}, format=lambda o: f"{o['words']} words")
async def stats(input, ctx):
    return {"words": len(input["text"].split())}
```

`input` and `output` accept a JSON Schema dict or a pydantic model. `ctx` has the same capabilities, in snake_case.

## Protocol (stdio runtimes)

JSON-RPC 2.0, one message per line on stdin/stdout; stderr is the log.

| Direction | Method | |
| --- | --- | --- |
| host → plugin | `initialize {apiVersion, name, config, permissions}` | → `{tools: [{name, description, inputSchema, outputSchema?, annotations?, cost?, examples?}]}` |
| host → plugin | `tools/call {callId, name, input}` | → `{output, text?}` or `{error: {code, message, retryable, retryAfterMs?}}` |
| host → plugin | `$/cancel {callId}`, `shutdown` | |
| plugin → host | `$/progress {callId, message, ratio}` | notification |
| plugin → host | `host/fetch`, `host/fs.read`, `host/fs.write`, `host/exec`, `host/secrets.get`, `host/llm.complete`, `host/artifacts.write`, `host/cache.get/set`, `host/state.get/set/delete`, `host/call` | permission-checked by the host |

Error codes: `INVALID_INPUT`, `NOT_FOUND`, `PERMISSION_DENIED`, `RATE_LIMITED`, `UPSTREAM`, `TIMEOUT`, `INTERNAL`.

`examples/plugins/echo` implements the protocol in about 35 lines of Python, without any SDK.

## Conformance: `exec-mcp plugin test`

- The manifest is valid and `apiVersion` is compatible.
- The plugin starts, and setup takes less than 10 s.
- `examples` run, and `format(output)` fits `maxOutputTokens`.
- `$/cancel` is answered within 1 s, tested on the slowest example.
- A request outside the net allowlist fails with `PERMISSION_DENIED`.
- Errors have the `ToolError` shape.
