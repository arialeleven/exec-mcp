# Core tools

Names and semantics follow Claude Code, which models are trained on. Every result is short, and every error says
what to do next. Paths may be absolute or relative to `--workspace`.

| Tool | Annotations | Summary |
| --- | --- | --- |
| `Read` | readOnly | File with `N\t` line numbers, `offset`/`limit` (default 2000 lines). Lines over 2000 chars are cut; the result says where to continue. Images come back as image content; PDFs via `pdftotext`, else embedded. Binary files are described, not dumped. |
| `Edit` | destructive | Exact `old_string` → `new_string`. Requires a prior `Read` and an unchanged file (mtime plus SHA-256). Ambiguous matches name their lines; `replace_all` replaces every one. |
| `MultiEdit` | destructive | Several edits to one file, atomically: all or nothing. |
| `Write` | destructive | Create or overwrite a file. Overwriting needs a prior `Read`; BOM and line endings of the old file are kept. |
| `Glob` | readOnly | Files by glob (`src/**/*.ts`), `.gitignore`-aware, newest first, 100 max ("… N more, refine the pattern"). |
| `Grep` | readOnly | ripgrep regex search (managed fallback without rg). Modes `files_with_matches` (default), `content` with `context`, `count`; `glob`, `ignore_case`, `multiline`, `head_limit`. |
| `Bash` | destructive, openWorld | Shell command in a persistent session (see below). `timeout_ms` (default 120s, max 600s), `run_in_background`. |
| `BashOutput` | — | New output of a background command, or `kill: true`. |
| `TodoWrite` | readOnly | Task list; exactly one item `in_progress`. Disable with `tools.disabled` if your client has its own. |
| `Task` | — | Subagent with a fresh context; `explore` gets Read/Grep/Glob on the small model. Needs `ANTHROPIC_API_KEY` or the `claude` CLI. |
| `ToolSearch` | readOnly | Loads the schemas of deferred tools (`select:a,b` or keywords). Hidden while nothing is deferred. |
| `exec` | destructive | JavaScript code mode, see [sandbox.md](sandbox.md). |

## Edit details

1. Exact match.
2. If that fails and every line of `old_string` starts with a copied `N\t` prefix, the prefixes are stripped (from `new_string` too).
3. If that fails: a match ignoring tab/space differences, trailing spaces and curly versus straight quotes. The result
   says which of these happened. When the file uses curly quotes, `new_string` gets them too.

The file is decoded once (BOM and CRLF detected), edited with LF, and written back in its original style.
The response is `Applied to path, lines 40–47:` plus a few lines of context.

## Bash details

Each command runs in a fresh process that first restores, and at the end saves, the session state: cwd, exported
variables, functions, aliases. So `cd`, `export` and function definitions persist, and a timeout kills only the
command. Plain (non-exported) shell variables do not persist. stdin is `/dev/null`; `TERM=dumb`, `NO_COLOR=1`,
`PAGER=cat`, `GIT_PAGER=cat`.

Shell: bash, else sh (`bash.shell` overrides).

Output: stdout and stderr interleaved; ANSI codes and `\r` redraws removed; above `bash.maxOutputChars` the head and
the (more important) tail are kept, and the full text is saved under `<workspace>/.exec-mcp/artifacts/`. The result
is wrapped in `<untrusted source="Bash">` because command output can contain text written by others.

Command rules (`bash.allow` / `bash.ask` / `bash.deny`) see each simple command of a pipeline in a canonical form.
`rm -r -f`, `rm --recursive --force` and `/bin/rm -Rf` all count as `rm -rf`. `find … -delete` and `find -exec rm`
count as `find -delete`. Wrappers like `sudo`, `env`, `nohup`, `timeout` and `xargs` are looked through. These rules
are a guard against mistakes, not a sandbox: see [SECURITY.md](../SECURITY.md).

## Untrusted content

When `untrustedWrap` is on (the default), the result is wrapped in `<untrusted source="…">` when it carries text
other people may have written:

- Bash, and script tools or plugin tools marked `openWorld`;
- `Read` of a file outside the workspace, and `Grep` content over a path outside it;
- `Task` reports;
- `exec` output when the script called an open-world tool.

The server instructions tell the model to treat such blocks as data.
