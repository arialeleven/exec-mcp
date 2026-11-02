using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExecMcp.Host;

namespace ExecMcp.Plugins;

public static class Discovery
{
    /// <summary>Finds plugins; on a name clash the first source wins: project, global, npm, pip.</summary>
    public static List<(PluginManifest? Manifest, string Dir, string? Error)> Find(HostEnv env)
    {
        var found = new List<(PluginManifest?, string, string?)>();
        var seen = new HashSet<string>();

        void Scan(IEnumerable<string> dirs, string source)
        {
            foreach (var dir in dirs.Where(d => File.Exists(Path.Combine(d, "plugin.json"))).OrderBy(d => d, StringComparer.Ordinal))
            {
                try
                {
                    var m = PluginManifest.Load(dir);
                    m.Source = source;
                    if (!seen.Add(m.Name)) continue;
                    found.Add((m, dir, null));
                }
                catch (Exception ex)
                {
                    found.Add((null, dir, $"plugin.json: {ex.Message}"));
                }
            }
        }

        Scan(Subdirs(Path.Combine(env.Workspace, ".exec-mcp", "plugins")), "project");
        Scan(Subdirs(Path.Combine(env.HomeDir, "plugins")), "global");
        Scan(NpmPackages(Path.Combine(env.Workspace, "node_modules")), "npm");
        if (NpmGlobalRoot() is { } g) Scan(NpmPackages(g), "npm");
        foreach (var site in PythonSitePackages())
            Scan(Subdirs(site).Where(d => Path.GetFileName(d).StartsWith("exec_mcp_plugin_")), "pip");
        return found;
    }

    static IEnumerable<string> Subdirs(string dir) => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir) : [];

    static IEnumerable<string> NpmPackages(string nodeModules)
    {
        if (!Directory.Exists(nodeModules)) yield break;
        foreach (var d in Directory.EnumerateDirectories(nodeModules))
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith("exec-mcp-plugin-")) yield return d;
            else if (name.StartsWith('@'))
                foreach (var s in Directory.EnumerateDirectories(d).Where(s => Path.GetFileName(s).StartsWith("exec-mcp-plugin-")))
                    yield return s;
        }
    }

    static string? NpmGlobalRoot() => RunQuick(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", ["root", "-g"])?.Trim();

    static IEnumerable<string> PythonSitePackages()
    {
        var output = RunQuick(OperatingSystem.IsWindows() ? "python" : "python3",
            ["-c", "import site,sys; print('\\n'.join(site.getsitepackages()+[site.getusersitepackages()]))"]);
        return output is null ? [] : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    static string? RunQuick(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardInput.Close();
            var o = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { p.Kill(true); return null; }
            return p.ExitCode == 0 ? o.Result : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// ~/.exec-mcp/approvals.json — the lockfile: which permissions the user approved for which plugin version
/// and the content hash of the plugin directory at that time.
/// </summary>
public sealed class Approvals(HostEnv env)
{
    string FilePath => Path.Combine(env.HomeDir, "approvals.json");

    public sealed class Entry
    {
        public string Version { get; set; } = "";
        public JsonNode? Permissions { get; set; }
        public string Hash { get; set; } = "";
        public DateTime ApprovedAt { get; set; }
    }

    public enum Status { Approved, New, PermissionsChanged, ContentChanged }

    Dictionary<string, Entry> Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath), PluginManifest.Json) ?? new()
        : new();

    public (Status Status, List<string> Diff) Check(PluginManifest m)
    {
        if (!Load().TryGetValue(m.Name, out var e)) return (Status.New, Describe(m.Permissions));
        var diff = Diff(e.Permissions, JsonNode.Parse(m.Permissions.Canonical()));
        if (diff.Count > 0) return (Status.PermissionsChanged, diff);
        // In dev mode files change constantly; the permission set is what matters there.
        if (!env.Dev && e.Hash != ContentHash(m.Dir)) return (Status.ContentChanged, [$"plugin files changed since approval of {e.Version} (now {m.Version})"]);
        return (Status.Approved, []);
    }

    public void Approve(PluginManifest m)
    {
        var all = Load();
        all[m.Name] = new Entry
        {
            Version = m.Version,
            Permissions = JsonNode.Parse(m.Permissions.Canonical()),
            Hash = ContentHash(m.Dir),
            ApprovedAt = DateTime.UtcNow,
        };
        Directory.CreateDirectory(env.HomeDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, PluginManifest.Json));
    }

    public static List<string> Describe(PermissionsSpec p)
    {
        var lines = new List<string>();
        foreach (var n in p.Net.Allow) lines.Add($"+ net: {n}{(n == "*" ? "  (any host!)" : "")}");
        foreach (var r in p.Fs.Read) lines.Add($"+ fs.read: {r}");
        foreach (var w in p.Fs.Write) lines.Add($"+ fs.write: {w}");
        foreach (var x in p.Exec) lines.Add($"+ exec: {x}");
        foreach (var s in p.Secrets) lines.Add($"+ secret: {s}");
        foreach (var l in p.Llm) lines.Add($"+ llm: {l}");
        if (lines.Count == 0) lines.Add("(no permissions)");
        return lines;
    }

    static List<string> Diff(JsonNode? oldP, JsonNode? newP)
    {
        static IEnumerable<string> Flatten(JsonNode? n, string prefix)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o) foreach (var x in Flatten(v, prefix.Length == 0 ? k : $"{prefix}.{k}")) yield return x;
                    break;
                case JsonArray a:
                    foreach (var v in a) yield return $"{prefix}: {v}";
                    break;
                case null: break;
                default: yield return $"{prefix}: {n}"; break;
            }
        }
        var before = Flatten(oldP, "").ToHashSet();
        var after = Flatten(newP, "").ToHashSet();
        return after.Except(before).Select(x => "+ " + x).Concat(before.Except(after).Select(x => "- " + x)).ToList();
    }

    public static string ContentHash(string dir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var skip = new[] { "node_modules", ".git", "__pycache__", ".venv" };
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar).Any(skip.Contains))
            .Where(f => Path.GetRelativePath(dir, f) != "tools.json")  // generated from the (hashed) code by `plugin build`
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var f in files)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, f).Replace('\\', '/') + "\0"));
            sha.AppendData(File.ReadAllBytes(f));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
