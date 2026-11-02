using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ExecMcp.Host;
using Microsoft.Extensions.FileSystemGlobbing;

namespace ExecMcp.Tools;

/// <summary>Managed file walk that honours .gitignore files; used when ripgrep is not installed.</summary>
public static class FileWalker
{
    sealed record Rules(string Dir, Ignore.Ignore Ignore);

    public static IEnumerable<string> Enumerate(string root, string workspace, CancellationToken ct)
    {
        var inherited = new List<Rules>();
        // .gitignore files above the search root still apply (up to the repository root).
        for (var d = Directory.GetParent(root); d is not null; d = d.Parent)
        {
            var r = Load(d.FullName);
            if (r is not null) inherited.Insert(0, r);
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) break;
        }
        return Walk(root, inherited, new HashSet<string>(), ct);
    }

    static IEnumerable<string> Walk(string dir, List<Rules> rules, HashSet<string> visited, CancellationToken ct)
    {
        // Follow symlinked directories (like rg --follow), but never re-enter a directory that is already
        // an ancestor of this path: that is a cycle.
        string real;
        try { real = new DirectoryInfo(dir).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(dir); }
        catch { yield break; }
        if (!visited.Add(real)) yield break;
        try
        {
        ct.ThrowIfCancellationRequested();
        var own = Load(dir);
        var active = own is null ? rules : [.. rules, own];
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (IOException) { yield break; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            bool isDir = Directory.Exists(entry);
            if (isDir && name == ".git") continue;
            if (IsIgnored(entry, isDir, active)) continue;
            if (isDir)
            {
                foreach (var f in Walk(entry, active, visited, ct)) yield return f;
            }
            else yield return entry;
        }
        }
        finally { visited.Remove(real); }
    }

    static bool IsIgnored(string path, bool isDir, List<Rules> rules)
    {
        foreach (var r in rules)
        {
            var rel = Path.GetRelativePath(r.Dir, path).Replace('\\', '/');
            if (r.Ignore.IsIgnored(isDir ? rel + "/" : rel)) return true;
        }
        return false;
    }

    static Rules? Load(string dir)
    {
        var f = Path.Combine(dir, ".gitignore");
        if (!File.Exists(f)) return null;
        var ig = new Ignore.Ignore();
        foreach (var line in File.ReadAllLines(f))
            if (line.Trim().Length > 0 && !line.TrimStart().StartsWith('#')) ig.Add(line.TrimEnd());
        return new Rules(dir, ig);
    }
}

internal static class Ripgrep
{
    static readonly Lazy<string?> PathLazy = new(() =>
    {
        var env = Environment.GetEnvironmentVariable("EXEC_MCP_RG");
        if (env == "none") return null;   // force the managed implementation (tests, odd platforms)
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var exe = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    });

    public static string? Exe => PathLazy.Value;

    public static async Task<(int Code, string Out, string Err)> RunAsync(string cwd, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Exe!) { WorkingDirectory = cwd, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();  // never let a child read the MCP stdin
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await o, await e);
    }
}

public static class SearchTools
{
    const int GlobLimit = 100;

    public static HostTool Glob(HostEnv env) => new()
    {
        Name = "Glob",
        Description = "Find files by glob pattern (e.g. `src/**/*.ts`). Respects .gitignore. Results sorted by modification time, newest first.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string" },
            "path":    { "type": "string", "description": "Directory to search (default: cwd)" }
          },
          "required": ["pattern"]
        }
        """),
        Annotations = new() { ReadOnlyHint = true },
        Handler = async call =>
        {
            var root = env.Resolve(call.Str("path") ?? env.Workspace);
            if (!Directory.Exists(root)) return ToolOutcome.Error(PathHints.NotFound(root, env), "NOT_FOUND");
            var pattern = call.Req("pattern").Replace('\\', '/');
            if (Path.IsPathRooted(pattern))
            {
                var rel = Path.GetRelativePath(root, pattern).Replace('\\', '/');
                if (rel.StartsWith("..")) return ToolOutcome.Error("pattern is outside path; pass the directory as path and a relative pattern.");
                pattern = rel;
            }

            var files = await ListFilesAsync(root, env, call.Ct);
            var matcher = new Matcher(OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(pattern);
            var matched = matcher.Match(root, files).Files
                .Select(f => Path.GetFullPath(Path.Combine(root, f.Path)))
                .Select(f => (f, t: File.GetLastWriteTimeUtc(f)))
                .OrderByDescending(x => x.t).Select(x => x.f).ToList();

            if (matched.Count == 0) return ToolOutcome.Text("No files matched.");
            var sb = new StringBuilder(string.Join("\n", matched.Take(GlobLimit).Select(env.Display)));
            if (matched.Count > GlobLimit) sb.Append($"\n… {matched.Count - GlobLimit} more, refine the pattern");
            return ToolOutcome.Text(sb.ToString());
        },
    };

    /// <summary>All non-ignored files under root, relative to root.</summary>
    static async Task<List<string>> ListFilesAsync(string root, HostEnv env, CancellationToken ct)
    {
        if (Ripgrep.Exe is not null)
        {
            var (code, output, _) = await Ripgrep.RunAsync(root, ["--files", "--hidden", "--follow", "--no-require-git", "--no-messages", "--glob", "!.git"], ct);
            if (code is 0 or 1)
                return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        }
        return FileWalker.Enumerate(root, env.Workspace, ct).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
    }

    public static HostTool Grep(HostEnv env) => new()
    {
        Name = "Grep",
        Description = "Search file contents with ripgrep regex. Default output: matching file paths. Use output_mode=content for matching lines.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern":     { "type": "string" },
            "path":        { "type": "string" },
            "glob":        { "type": "string", "description": "File filter, e.g. `*.py`" },
            "output_mode": { "type": "string", "enum": ["files_with_matches", "content", "count"], "default": "files_with_matches" },
            "context":     { "type": "integer", "description": "Lines of context (content mode)" },
            "ignore_case": { "type": "boolean", "default": false },
            "multiline":   { "type": "boolean", "default": false },
            "head_limit":  { "type": "integer", "description": "Max results" }
          },
          "required": ["pattern"]
        }
        """),
        Annotations = new() { ReadOnlyHint = true },
        Handler = call => GrepAsync(env, call),
    };

    static async Task<ToolOutcome> GrepAsync(HostEnv env, ToolCall call)
    {
        var target = env.Resolve(call.Str("path") ?? env.Workspace);
        if (!File.Exists(target) && !Directory.Exists(target)) return ToolOutcome.Error(PathHints.NotFound(target, env), "NOT_FOUND");
        var mode = call.Str("output_mode") ?? "files_with_matches";
        var limit = call.Int("head_limit") ?? (mode == "content" ? 300 : 100);
        var opts = new GrepOptions(call.Req("pattern"), call.Str("glob"), mode, Math.Max(0, call.Int("context") ?? 0),
            call.Bool("ignore_case"), call.Bool("multiline"));

        List<string> results;
        if (Ripgrep.Exe is not null)
        {
            var (code, output, err) = await Ripgrep.RunAsync(env.Workspace, RgArgs(opts, env.Display(target)), call.Ct);
            if (code == 2 && output.Length == 0)
                return ToolOutcome.Error($"ripgrep error: {err.Trim()}" + (err.Contains("regex") ? " Escape special characters or check the regex syntax." : ""));
            results = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))
                .Select(l => l.StartsWith("./") ? l[2..] : l).ToList();
        }
        else
        {
            try { results = ManagedGrep(env, target, opts, call.Ct); }
            catch (ArgumentException ex) { return ToolOutcome.Error($"Invalid regex: {ex.Message}"); }
        }

        if (results.Count == 0) return ToolOutcome.Text("No matches.");
        if (mode == "files_with_matches")
            results = results.Select(r => (r, t: File.GetLastWriteTimeUtc(env.Resolve(r)))).OrderByDescending(x => x.t).Select(x => x.r).ToList();

        var sb = new StringBuilder(string.Join("\n", results.Take(limit)));
        if (results.Count > limit) sb.Append($"\n… {results.Count - limit} more, refine the pattern");
        var outcome = ToolOutcome.Text(sb.ToString());
        outcome.Untrusted = mode == "content" && !env.InWorkspace(target);
        return outcome;
    }

    sealed record GrepOptions(string Pattern, string? Glob, string Mode, int Context, bool IgnoreCase, bool Multiline);

    static List<string> RgArgs(GrepOptions o, string target)
    {
        var a = new List<string> { "--no-config", "--color", "never", "--hidden", "--follow", "--no-require-git", "--glob", "!.git", "--max-columns", "500", "--max-columns-preview" };
        if (o.IgnoreCase) a.Add("--ignore-case");
        if (o.Multiline) { a.Add("--multiline"); a.Add("--multiline-dotall"); }
        if (o.Glob is not null) { a.Add("--glob"); a.Add(o.Glob); }
        switch (o.Mode)
        {
            case "content":
                a.AddRange(["--line-number", "--no-heading", "--with-filename"]);
                if (o.Context > 0) { a.Add("--context"); a.Add(o.Context.ToString()); }
                break;
            case "count": a.AddRange(["--count", "--with-filename"]); break;
            default: a.Add("--files-with-matches"); break;
        }
        a.Add("--regexp");
        a.Add(o.Pattern);
        a.Add(target);
        return a;
    }

    static List<string> ManagedGrep(HostEnv env, string target, GrepOptions o, CancellationToken ct)
    {
        var options = RegexOptions.CultureInvariant | (o.IgnoreCase ? RegexOptions.IgnoreCase : 0) | (o.Multiline ? RegexOptions.Singleline | RegexOptions.Multiline : 0);
        var rx = new Regex(o.Pattern, options, TimeSpan.FromSeconds(2));
        Matcher? glob = null;
        if (o.Glob is not null)
        {
            glob = new Matcher(StringComparison.OrdinalIgnoreCase);
            glob.AddInclude(o.Glob.Contains('/') ? o.Glob : "**/" + o.Glob);
        }
        var files = File.Exists(target) ? [target] : FileWalker.Enumerate(target, env.Workspace, ct);
        var results = new List<string>();
        foreach (var file in files)
        {
            if (glob is not null && Directory.Exists(target) && !glob.Match(target, Path.GetRelativePath(target, file)).HasMatches) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); } catch { continue; }
            if (TextFile.LooksBinary(bytes)) continue;
            var text = TextFile.Decode(bytes).Text;
            var display = env.Display(file);

            if (o.Multiline)
            {
                var ms = rx.Matches(text);
                if (ms.Count == 0) continue;
                if (o.Mode == "files_with_matches") results.Add(display);
                else if (o.Mode == "count") results.Add($"{display}:{ms.Count}");
                else foreach (Match m in ms)
                {
                    int line = EditEngine.LineOf(text, m.Index);
                    foreach (var (l, i) in m.Value.Split('\n').Select((l, i) => (l, i))) results.Add($"{display}:{line + i}:{l}");
                }
                continue;
            }

            var lines = text.Split('\n');
            var hits = new List<int>();
            for (int i = 0; i < lines.Length; i++) if (rx.IsMatch(lines[i])) hits.Add(i);
            if (hits.Count == 0) continue;
            if (o.Mode == "files_with_matches") { results.Add(display); continue; }
            if (o.Mode == "count") { results.Add($"{display}:{hits.Count}"); continue; }

            int lastPrinted = -1;
            var hitSet = hits.ToHashSet();
            foreach (var h in hits)
            {
                int from = Math.Max(0, h - o.Context), to = Math.Min(lines.Length - 1, h + o.Context);
                if (lastPrinted >= 0 && from > lastPrinted + 1 && o.Context > 0) results.Add("--");
                for (int i = Math.Max(from, lastPrinted + 1); i <= to; i++)
                {
                    var l = lines[i].Length > 500 ? lines[i][..500] + "…" : lines[i];
                    results.Add($"{display}{(hitSet.Contains(i) ? ':' : '-')}{i + 1}{(hitSet.Contains(i) ? ':' : '-')}{l}");
                }
                lastPrinted = to;
            }
        }
        return results;
    }
}
