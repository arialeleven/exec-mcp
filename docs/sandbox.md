# The `exec` sandbox (code mode)

`exec` runs JavaScript in [Jint](https://github.com/sebastienros/jint), an interpreter inside the server process.
Scripts have **no** `require`, `process`, `fetch`, file system or network of their own. They can only call tools, and
every call goes through the same registry as a direct MCP call: schema validation, permissions, confirmations,
limits and redaction. Only what the script prints is returned to the model.

```js
// Count lines of every C# file and print the five largest; one round trip instead of dozens.
const files = (await core.Glob({ pattern: "src/**/*.cs" })).split("\n");
const sizes = [];
for (const f of files) {
  const r = await core.Bash({ command: `wc -l < '${f}'` });
  sizes.push([f, parseInt(r.replace(/<\/?untrusted[^>]*>/g, ""))]);
}
sizes.sort((a, b) => b[1] - a[1]);
console.log(sizes.slice(0, 5));
```

## Namespaces

| Namespace | Contents |
| --- | --- |
| `core` | `Read`, `Edit`, `MultiEdit`, `Write`, `Glob`, `Grep`, `Bash`, `BashOutput` |
| `tools` | script tools ([script-tools.md](script-tools.md)) |
| `<plugin>` | plugin tools, e.g. `db.query` ([plugins.md](plugins.md)) |

`help()` prints TypeScript declarations for every namespace; `help("db")` prints one. Each function takes one object
and returns a promise. The result is the tool's structured output when it has one, otherwise its text.

## Rules

- Top-level `await` works; a `return value` at the end is printed too.
- A tool error is a normal JS exception (`[CODE] message`), so `try/catch` works. Unhandled errors return
  everything printed so far plus the error.
- Limits (config `sandbox`): `timeoutMs` (default 60s, or `timeout_ms` per call), `memoryMb` (256), recursion depth,
  and output size (spilled to a file above `maxOutputChars`).
- Destructive tools (`core.Write`, `core.Bash`, script tools without `@readonly`, …) called from a script need the
  user's confirmation through MCP elicitation, because the client approved only the `exec` call itself. Clients
  without elicitation (claude.ai today) get `PERMISSION_DENIED`. Set `"confirmDestructive": "never"` if you accept
  that risk.
