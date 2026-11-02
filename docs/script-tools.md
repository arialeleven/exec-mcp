# Script tools

A script tool is **one executable file** in a watched folder. The comment header describes the tool; the body
does the work. There is no rebuild and no restart: exec-mcp watches the folders, re-reads changed files and sends
`notifications/tools/list_changed` to connected clients.

Folders:

- `~/.exec-mcp/tools/` (always)
- every folder in `tools.dirs` of `~/.exec-mcp/config.json`

These folders are trusted: anything placed there runs with exec-mcp's permissions. Project folders are never
scanned implicitly, so a cloned repository cannot add tools.

## Quick start

```bash
exec-mcp tools new my_tool --lang sh     # sh | py | js | ps1 — writes a commented template
exec-mcp tools list                      # what loaded, and header errors with the reason
exec-mcp tools run my_tool '{"path": "/var/log"}'   # run it exactly as a client would (schema-checked)
```

## Header

The header is read from the first 80 lines. Comment markers `#`, `//`, `--`, `;`, `REM` and `::` all work, so any
language can carry it. A file without an `@tool` line is ignored, which is useful for helper scripts.

| Directive | Meaning |
| --- | --- |
| `@tool name` | Tool name, `[a-zA-Z][a-zA-Z0-9_]*`. Without a value, the file name is used. Must not clash with core or other tools. |
| `@description text` | Required. What the tool does and returns, in 1–2 sentences. Repeat the line to continue it. |
| `@param name type[?] [=default] description` | A parameter. `?` makes it optional; a default makes it optional too. |
| `@readonly` | No side effects: clients may run it without asking. |
| `@destructive` | Explicitly has side effects. **Tools without `@readonly` count as destructive.** |
| `@idempotent` | Safe to repeat; with `@readonly` it allows an automatic retry. |
| `@openworld` | Output contains third-party text (web pages, logs); it is wrapped in `<untrusted>`. |
| `@output json` | stdout is JSON: it becomes `structuredContent` and the sandbox gets an object. |
| `@timeout 30s` | `500ms`, `30s`, `2m`, `1h`. Default 60s. The process tree is killed at the limit. |
| `@deferred` | Not in `tools/list` until loaded with ToolSearch. Use it for rarely used tools to save context. |
| `@hidden` | Only callable from the `exec` sandbox (`tools.name(...)`). |

### Parameter types

| Type | JSON Schema |
| --- | --- |
| `string`, `path` | string |
| `integer` / `int` | integer |
| `number` / `float` | number |
| `boolean` / `bool` | boolean |
| `object`, `array` | object, array |
| `string[]`, `integer[]`, … | array of that type |
| `enum(a\|b\|c)` | string with those values |

```bash
# @param path string Directory to measure
# @param depth integer? =1 How many levels
# @param unit enum(K|M|G)? =M Size unit
# @param exclude string[]? Globs to skip
# @param dry boolean =false Only print what would happen
```

## How the script is run

- **Interpreter**: from the `#!` line (`#!/usr/bin/env python3`, `#!/bin/bash -e`), else by
  extension: `.sh` → bash, `.py` → python3, `.js/.mjs` → node, `.ps1` → pwsh, `.rb`, `.pl`, `.php`, `.cmd/.bat`.
  The executable bit is not required.
- **Arguments**, with defaults applied:
  - one **environment variable per parameter**, named exactly like it (`$path`, `$depth`); arrays and objects arrive as JSON;
  - the whole input as **JSON on stdin**, and also in `EXEC_MCP_INPUT`;
  - `EXEC_MCP_WORKSPACE` and `EXEC_MCP_TOOL`.
- **Working directory**: the workspace.
- **Result**: stdout (ANSI codes stripped, long output truncated and spilled to a file). A non-zero exit code is an
  error whose message carries stderr and stdout. Exit code `2` or `64` reports `INVALID_INPUT`, other codes report
  `UPSTREAM`.
- Input is validated against the generated schema **before** the script runs, so the script can trust types and
  required fields.

## Safety

- Calls from the `exec` sandbox and from subagents to tools that are not `@readonly` need the user's confirmation (see
  `confirmDestructive`), exactly like core tools.
- Script output passes through secret redaction. If the tool is `@openworld`, it is also wrapped in `<untrusted>`.
- The tool runs as the exec-mcp user. Write scripts as you would write sudo-able admin scripts: quote variables and
  validate paths.

## Examples

See [examples/tools](../examples/tools): `disk_usage`, `listening_ports`, `service_status`, `tail_log`, `http_check`.

```python
#!/usr/bin/env python3
# @tool word_stats
# @description Word count and the most frequent words of a text.
# @param text string Text to analyse
# @param top integer? =5 How many words
# @readonly
# @output json
import collections, json, re, sys
args = json.load(sys.stdin)
words = re.findall(r"\w+", args["text"].lower())
print(json.dumps({"words": len(words), "top": collections.Counter(words).most_common(args["top"])}))
```

## Script tool or plugin?

| | Script tool | Plugin |
| --- | --- | --- |
| Effort | one file | manifest + SDK |
| Process | one per call | long-running (connections, caches) |
| Permissions | the exec-mcp user | declared in the manifest, enforced by the host (net/fs/exec/secrets) |
| Approval | none: the folder is trusted | `exec-mcp plugin approve`, lockfile with hashes |
| Best for | admin helpers, wrappers around CLIs | API clients, databases, third-party code |
