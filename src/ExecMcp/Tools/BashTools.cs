using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ExecMcp.Host;
using Microsoft.Extensions.Logging;

namespace ExecMcp.Tools;

/// <summary>Captured output of a running command. Absolute positions survive trimming of old output.</summary>
public sealed class OutputBuffer
{
    const int MaxKeep = 4_000_000;
    readonly StringBuilder _sb = new();
    long _dropped;

    public void Append(ReadOnlySpan<char> s)
    {
        lock (_sb)
        {
            _sb.Append(s);
            if (_sb.Length > MaxKeep)
            {
                int cut = _sb.Length - MaxKeep / 2;
                _sb.Remove(0, cut);
                _dropped += cut;
            }
        }
    }

    public long Total { get { lock (_sb) return _dropped + _sb.Length; } }

    /// <summary>Text from an absolute position; reports how much was lost if it was trimmed.</summary>
    public (string Text, long Lost) From(long pos)
    {
        lock (_sb)
        {
            long lost = Math.Max(0, _dropped - pos);
            int start = (int)Math.Max(0, pos - _dropped);
            return (start >= _sb.Length ? "" : _sb.ToString(start, _sb.Length - start), lost);
        }
    }
}

/// <summary>
/// "Persistent" shell: each command runs in a fresh process that restores and then saves cwd, exported env,
/// functions and aliases. A timeout kills the command's process tree, never the session state.
/// </summary>
public sealed class ShellSession
{
    enum Kind { Bash, Sh, Pwsh }

    readonly HostEnv _env;
    readonly ILogger _log;
    readonly Kind _kind;
    readonly string _exe;
    readonly string _stateFile;
    readonly string? _wrapper;
    int _seq;

    const string BashScript = """
        exec 2>&1 </dev/null
        __em_state="$1"; __em_cmd="$2"; __em_bg="$3"; shift 3
        __em_dump() { [ "$__em_bg" = 1 ] && return; { export -p; declare -f; alias -p; printf 'cd -- %q\n' "$PWD"; } > "$__em_state.tmp" 2>/dev/null && mv -f "$__em_state.tmp" "$__em_state"; }
        trap __em_dump EXIT
        [ -f "$__em_state" ] && . "$__em_state" >/dev/null 2>&1
        shopt -s expand_aliases
        . "$__em_cmd"
        """;

    const string ShScript = """
        exec 2>&1 </dev/null
        __em_state="$1"; __em_cmd="$2"; __em_bg="$3"; shift 3
        __em_dump() { [ "$__em_bg" = 1 ] && return; { export -p; printf "cd -- '%s'\n" "$(pwd | sed "s/'/'\\\\''/g")"; } > "$__em_state.tmp" 2>/dev/null && mv -f "$__em_state.tmp" "$__em_state"; }
        trap __em_dump EXIT
        [ -f "$__em_state" ] && . "$__em_state" >/dev/null 2>&1
        . "$__em_cmd"
        """;

    const string PwshScript = """
        param($State, $Cmd, $Bg)
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        if (Test-Path -LiteralPath $State) { . $State }
        $__emExit = 0
        try {
            . $Cmd
            if ($LASTEXITCODE) { $__emExit = $LASTEXITCODE } elseif (-not $?) { $__emExit = 1 }
        } catch { Write-Error $_; $__emExit = 1 }
        finally {
            if ($Bg -ne '1') {
                $q = { param($s) "'" + ($s -replace "'", "''") + "'" }
                $lines = @()
                foreach ($e in Get-ChildItem env:) { $lines += "[Environment]::SetEnvironmentVariable($(& $q $e.Name), $(& $q $e.Value))" }
                $lines += "Set-Location -LiteralPath $(& $q (Get-Location).Path)"
                Set-Content -LiteralPath $State -Value $lines -Encoding utf8
            }
        }
        exit $__emExit
        """;

    public ShellSession(HostEnv env, ILogger log)
    {
        _env = env;
        _log = log;
        (_kind, _exe) = PickShell(env.Config.Bash.Shell);
        _stateFile = Path.Combine(env.RunDir, "shell-state" + (_kind == Kind.Pwsh ? ".ps1" : ".sh"));
        if (_kind == Kind.Pwsh)
        {
            _wrapper = Path.Combine(env.RunDir, "wrapper.ps1");
            File.WriteAllText(_wrapper, PwshScript);
        }
    }

    public string ShellName => _kind == Kind.Pwsh ? "PowerShell" : Path.GetFileNameWithoutExtension(_exe);

    static (Kind, string) PickShell(string? configured)
    {
        static string? Which(string name)
        {
            if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                // C:\Windows\System32\bash.exe is the WSL launcher, not a usable bash for Windows paths.
                if (OperatingSystem.IsWindows() && dir.Contains("System32", StringComparison.OrdinalIgnoreCase) && name.StartsWith("bash")) continue;
                foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe", "" } : [""])
                {
                    var p = Path.Combine(dir, name + ext);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        static Kind KindOf(string exe) => Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() switch
        {
            "pwsh" or "powershell" => Kind.Pwsh,
            "bash" => Kind.Bash,
            _ => Kind.Sh,
        };

        if (!string.IsNullOrEmpty(configured) && Which(configured) is { } c) return (KindOf(c), c);
        if (Which("bash") is { } bash) return (Kind.Bash, bash);
        if (!OperatingSystem.IsWindows() && Which("sh") is { } sh) return (Kind.Sh, sh);
        if (Which("pwsh") is { } pwsh) return (Kind.Pwsh, pwsh);
        return (Kind.Pwsh, "powershell.exe");
    }

    public Process Start(string command, bool background, OutputBuffer buffer)
    {
        var n = Interlocked.Increment(ref _seq);
        var cmdFile = Path.Combine(_env.RunDir, $"cmd-{n}" + (_kind == Kind.Pwsh ? ".ps1" : ".sh"));
        File.WriteAllText(cmdFile, command + "\n");

        var psi = new ProcessStartInfo(_exe)
        {
            WorkingDirectory = _env.Workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (_kind == Kind.Pwsh)
        {
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _wrapper! }) psi.ArgumentList.Add(a);
        }
        else
        {
            if (_kind == Kind.Bash) { psi.ArgumentList.Add("--noprofile"); psi.ArgumentList.Add("--norc"); }
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(_kind == Kind.Bash ? BashScript : ShScript);
            psi.ArgumentList.Add("exec-mcp");
        }
        psi.ArgumentList.Add(_stateFile);
        psi.ArgumentList.Add(cmdFile);
        psi.ArgumentList.Add(background ? "1" : "0");

        // Clean, non-interactive output.
        psi.Environment["TERM"] = "dumb";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        var p = Process.Start(psi)!;
        p.StandardInput.Close();
        _ = Pump(p.StandardOutput, buffer);
        _ = Pump(p.StandardError, buffer);
        return p;
    }

    static async Task Pump(StreamReader reader, OutputBuffer buffer)
    {
        var buf = new char[8192];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf)) > 0) buffer.Append(buf.AsSpan(0, n));
        }
        catch { /* process killed */ }
    }
}

public sealed class BackgroundShell
{
    public required string Id { get; init; }
    public required string Command { get; init; }
    public required Process Process { get; init; }
    public required OutputBuffer Output { get; init; }
    public DateTime Started { get; } = DateTime.UtcNow;
    public long ReadPos { get; set; }
}

public sealed class BashTools(HostEnv env, ILogger logger)
{
    readonly ShellSession _shell = new(env, logger);
    readonly ConcurrentDictionary<string, BackgroundShell> _bg = new();
    int _bgSeq;

    public HostTool Bash() => new()
    {
        Name = "Bash",
        Description = $"Run a shell command on host {Environment.MachineName} in a persistent session: cwd, exported variables, functions and aliases persist between calls (plain shell variables do not). Output is truncated if long. Do not use for reading/searching/editing files — use the dedicated tools.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "command":           { "type": "string" },
            "description":       { "type": "string", "description": "5-10 words: what this does" },
            "timeout_ms":        { "type": "integer", "default": 120000, "maximum": 600000 },
            "run_in_background": { "type": "boolean", "default": false }
          },
          "required": ["command"]
        }
        """),
        Annotations = new() { DestructiveHint = true, OpenWorldHint = true },
        Handler = RunAsync,
    };

    public HostTool BashOutput() => new()
    {
        Name = "BashOutput",
        Description = "Read new output from a background shell, or kill it.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "shell_id": { "type": "string" },
            "kill":     { "type": "boolean", "default": false }
          },
          "required": ["shell_id"]
        }
        """),
        Annotations = new(),
        Handler = call => Task.FromResult(Output(call)),
    };

    async Task<ToolOutcome> RunAsync(ToolCall call)
    {
        var command = call.Req("command");
        logger.LogInformation("Bash: {Description} | {Command}", call.Str("description") ?? "", command);

        var verdict = BashPolicyCheck.Evaluate(command, env.Config.Bash);
        if (verdict.Action == "deny")
            return ToolOutcome.Error($"Command blocked by policy (rule \"{verdict.Rule}\"). Change bash.deny in ~/.exec-mcp/config.json if this is intended.", "PERMISSION_DENIED");
        if (verdict.Action == "ask")
        {
            var ok = await Confirm.AskAsync(call.Server, $"Run command? (matches rule \"{verdict.Rule}\")\n\n{command}", call.Ct);
            if (ok is null && env.Config.Bash.AskWithoutElicitation == "allow") ok = true;
            if (ok != true)
                return ToolOutcome.Error(ok is null
                    ? $"Command requires confirmation (rule \"{verdict.Rule}\") but this client cannot ask the user. Allow it with bash.allow, or set bash.askWithoutElicitation to \"allow\" in ~/.exec-mcp/config.json."
                    : "User declined the command.", "PERMISSION_DENIED");
        }

        var buffer = new OutputBuffer();
        if (call.Bool("run_in_background"))
        {
            var id = $"bash_{Interlocked.Increment(ref _bgSeq)}";
            var proc = _shell.Start(command, background: true, buffer);
            _bg[id] = new BackgroundShell { Id = id, Command = command, Process = proc, Output = buffer };
            return ToolOutcome.Text($"Started in background: shell_id={id}. Use BashOutput to read its output.");
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(call.Int("timeout_ms") ?? 120_000, 1000, 600_000));
        var sw = Stopwatch.StartNew();
        var p = _shell.Start(command, background: false, buffer);
        string? status = null;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(call.Ct))
        {
            cts.CancelAfter(timeout);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                status = call.Ct.IsCancellationRequested
                    ? "Cancelled; process killed."
                    : $"Command timed out after {timeout.TotalSeconds:0.#}s and was killed (session state kept). Use run_in_background for long-running commands.";
            }
        }
        // Let the pipes drain; a background child (`cmd &`) may keep them open forever.
        var drainUntil = DateTime.UtcNow.AddMilliseconds(300);
        long last = -1;
        while (DateTime.UtcNow < drainUntil && buffer.Total != last) { last = buffer.Total; await Task.Delay(50); }

        var text = OutputUtil.Clean(buffer.From(0).Text).TrimEnd();
        text = OutputUtil.Spill(env, text, env.Config.Bash.MaxOutputChars, "bash");
        var sb = new StringBuilder(text.Length == 0 && status is null ? "(no output)" : text);
        int code = status is null ? p.ExitCode : -1;
        if (status is not null) sb.Append(sb.Length > 0 ? "\n" : "").Append(status);
        else if (code != 0) sb.Append(sb.Length > 0 ? "\n" : "").Append($"Exit code {code}");
        _ = Task.Delay(5000).ContinueWith(_ => p.Dispose());

        var outcome = ToolOutcome.Text(sb.ToString());
        if (status is not null) { outcome.IsError = true; outcome.ErrorCode = "TIMEOUT"; }
        return outcome;
    }

    ToolOutcome Output(ToolCall call)
    {
        var id = call.Req("shell_id");
        if (!_bg.TryGetValue(id, out var sh))
            return ToolOutcome.Error($"No background shell '{id}'. Active: {(_bg.IsEmpty ? "none" : string.Join(", ", _bg.Keys))}.", "NOT_FOUND");

        if (call.Bool("kill") && !sh.Process.HasExited)
        {
            try { sh.Process.Kill(entireProcessTree: true); } catch { }
            sh.Process.WaitForExit(2000);
        }

        var (text, lost) = sh.Output.From(sh.ReadPos);
        sh.ReadPos = sh.Output.Total;
        text = OutputUtil.Clean(text).TrimEnd();
        text = OutputUtil.Spill(env, text, env.Config.Bash.MaxOutputChars, id);

        var status = sh.Process.HasExited
            ? (call.Bool("kill") ? "killed" : $"exited with code {sh.Process.ExitCode}")
            : $"running ({(DateTime.UtcNow - sh.Started).TotalSeconds:0}s)";
        var sb = new StringBuilder($"[{id}: {status}]\n");
        if (lost > 0) sb.Append($"… {lost} older chars were dropped …\n");
        sb.Append(text.Length == 0 ? "(no new output)" : text);
        return ToolOutcome.Text(sb.ToString());
    }

    public void KillAll()
    {
        foreach (var sh in _bg.Values)
            try { if (!sh.Process.HasExited) sh.Process.Kill(entireProcessTree: true); } catch { }
    }
}

/// <summary>
/// Prefix rules over each simple command of a compound command line. This is a guard rail against
/// mistakes, not a security boundary: anything can be rewritten (python -c, base64 | sh, …). Use an
/// unprivileged OS user or a sandbox for hard limits.
/// </summary>
public static class BashPolicyCheck
{
    public sealed record Verdict(string Action, string? Rule);

    static readonly HashSet<string> Wrappers = ["env", "nohup", "time", "nice", "ionice", "command", "exec", "builtin", "stdbuf", "timeout", "xargs", "doas", "sudo", "su"];

    public static Verdict Evaluate(string command, BashPolicy policy)
    {
        var segments = Split(command);
        Verdict? ask = null;
        bool allAllowed = segments.Count > 0;
        foreach (var seg in segments)
        {
            var forms = Forms(seg);
            if (forms.Select(f => Match(f, policy.Deny)).FirstOrDefault(r => r is not null) is { } d) return new("deny", d);
            if (forms.Select(f => Match(f, policy.Ask)).FirstOrDefault(r => r is not null) is { } a) { ask ??= new("ask", a); allAllowed = false; continue; }
            if (Match(forms[0], policy.Allow) is null) allAllowed = false;
        }
        if (ask is not null) return ask;
        if (allAllowed) return new("allow", null);
        return new(policy.DefaultAction, policy.DefaultAction == "allow" ? null : "default");
    }

    /// <summary>
    /// The segment as written plus canonical forms: wrappers stripped (sudo, env, nohup, timeout…),
    /// /bin/rm → rm, rm with recursive+force flags in any spelling → "rm -rf …", find -delete / -exec rm → "find -delete".
    /// </summary>
    public static List<string> Forms(string segment)
    {
        var forms = new List<string> { segment };
        var words = Words(segment);
        // Strip wrappers and their options, remembering that they were there.
        int i = 0;
        while (i < words.Count)
        {
            var w = Path.GetFileName(words[i]);
            if (!Wrappers.Contains(w)) break;
            if (w is "sudo" or "doas" or "su") forms.Add(w);
            i++;
            while (i < words.Count && (words[i].StartsWith('-') || System.Text.RegularExpressions.Regex.IsMatch(words[i], @"^([A-Za-z_]\w*=|\d+[smhd]?$)"))) i++;
        }
        if (i >= words.Count) return forms;
        var cmd = Path.GetFileName(words[i]);
        var rest = words.Skip(i + 1).ToList();
        forms.Add(string.Join(' ', new[] { cmd }.Concat(rest)));

        if (cmd == "rm")
        {
            bool recursive = false, force = false;
            foreach (var a in rest.TakeWhile(a => a != "--"))
            {
                if (a is "--recursive") recursive = true;
                else if (a is "--force") force = true;
                else if (a.StartsWith('-') && !a.StartsWith("--")) { recursive |= a.Contains('r') || a.Contains('R'); force |= a.Contains('f'); }
            }
            if (recursive && force) forms.Add("rm -rf " + string.Join(' ', rest.Where(a => !a.StartsWith('-'))));
            else if (recursive) forms.Add("rm -r " + string.Join(' ', rest.Where(a => !a.StartsWith('-'))));
        }
        else if (cmd == "find" && (rest.Contains("-delete") || rest.Any(a => a is "-exec" or "-execdir") && rest.Any(a => Path.GetFileName(a) == "rm")))
            forms.Add("find -delete");
        else if (cmd == "git" && rest.Count > 0 && rest[0] == "push" && rest.Any(a => a is "-f" or "--force" or "--force-with-lease" || a.StartsWith("+")))
            forms.Add("git push --force");
        return forms;
    }

    static List<string> Words(string s)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        foreach (var c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else cur.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        return words;
    }

    static string? Match(string seg, List<string> rules)
    {
        foreach (var rule in rules)
        {
            var r = rule.Trim();
            if (r.Length == 0) continue;
            if (seg.Equals(r, StringComparison.Ordinal) ||
                (seg.StartsWith(r, StringComparison.Ordinal) && (char.IsWhiteSpace(seg[r.Length]) || !char.IsLetterOrDigit(r[^1]))))
                return r;
        }
        return null;
    }

    /// <summary>Splits on ; && || | & and newlines outside quotes, then drops leading VAR=value assignments.</summary>
    public static List<string> Split(string command)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < command.Length) { cur.Append(c); c = command[++i]; }
                cur.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; cur.Append(c); continue; }
            if (c == '\\' && i + 1 < command.Length) { cur.Append(c).Append(command[++i]); continue; }
            if (c is ';' or '|' or '&' or '\n' or '(' or ')' or '`') { Flush(); continue; }
            if (c == '$' && i + 1 < command.Length && command[i + 1] == '(') { Flush(); i++; continue; }
            cur.Append(c);
        }
        Flush();
        return parts;

        void Flush()
        {
            var s = cur.ToString().Trim();
            cur.Clear();
            while (true)
            {
                var m = System.Text.RegularExpressions.Regex.Match(s, @"^[A-Za-z_][A-Za-z0-9_]*=(?:'[^']*'|""[^""]*""|\S*)\s+");
                if (!m.Success) break;
                s = s[m.Length..];
            }
            if (s.Length > 0) parts.Add(s);
        }
    }
}
