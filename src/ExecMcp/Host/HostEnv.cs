using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExecMcp.Host;

/// <summary>Paths and configuration shared by every part of the host.</summary>
public sealed class HostEnv
{
    public required string Workspace { get; init; }
    /// <summary>~/.exec-mcp — user-level config, approvals, plugin state.</summary>
    public required string HomeDir { get; init; }
    public required HostConfig Config { get; init; }
    public bool Dev { get; init; }

    public string ArtifactsDir => Path.Combine(Workspace, ".exec-mcp", "artifacts");
    public string CacheRoot => Path.Combine(HomeDir, "cache");
    public string StateRoot => Path.Combine(HomeDir, "state");
    public string RunDir { get; init; } = Path.Combine(Path.GetTempPath(), $"exec-mcp-{Environment.ProcessId}");

    public static HostEnv Create(string? workspace, bool dev)
    {
        var ws = Path.GetFullPath(workspace ?? Environment.GetEnvironmentVariable("EXEC_MCP_WORKSPACE") ?? Environment.CurrentDirectory);
        var home = Environment.GetEnvironmentVariable("EXEC_MCP_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".exec-mcp");
        var config = HostConfig.Load(Path.Combine(home, "config.json"), Path.Combine(ws, ".exec-mcp", "config.json"));
        var env = new HostEnv { Workspace = ws, HomeDir = home, Config = config, Dev = dev };
        Directory.CreateDirectory(env.RunDir);
        return env;
    }

    /// <summary>Absolute path; relative paths resolve against the workspace, ~ expands to the user's home.</summary>
    public string Resolve(string path)
    {
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 1 ? path[2..] : "");
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Workspace, path));
    }

    /// <summary>Workspace-relative path when inside the workspace (fewer tokens), absolute otherwise.</summary>
    public string Display(string fullPath)
    {
        var rel = Path.GetRelativePath(Workspace, fullPath);
        return rel.StartsWith("..") || Path.IsPathRooted(rel) ? fullPath : rel.Replace('\\', '/');
    }

    /// <summary>Files the file tools refuse to read or write: exec-mcp's own secrets plus tools.protected globs.</summary>
    public bool IsProtected(string fullPath)
    {
        _protected ??= new[] { "http-password", "oauth-state.json", "approvals.json", "secrets.json" }
            .Select(f => Path.Combine(HomeDir, f))
            .Concat(Config.Tools.Protected.Select(Resolve))
            .Select(g => new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(g.Replace('\\', '/'))
                .Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
                OperatingSystem.IsLinux() ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToArray();
        var p = fullPath.Replace('\\', '/');
        return _protected.Any(r => r.IsMatch(p));
    }

    System.Text.RegularExpressions.Regex[]? _protected;

    public bool InWorkspace(string fullPath)
    {
        var rel = Path.GetRelativePath(Workspace, fullPath);
        return !rel.StartsWith("..") && !Path.IsPathRooted(rel);
    }

    public string NewArtifactPath(string prefix, string ext)
    {
        Directory.CreateDirectory(ArtifactsDir);
        var ignore = Path.Combine(Workspace, ".exec-mcp", ".gitignore");
        if (!File.Exists(ignore)) File.WriteAllText(ignore, "artifacts/\n");
        return Path.Combine(ArtifactsDir, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}{ext}");
    }
}

public sealed class HostConfig
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public BashPolicy Bash { get; set; } = new();
    /// <summary>When the host itself asks before destructive tools: never | indirect (sandbox/subagent calls) | always.</summary>
    public string ConfirmDestructive { get; set; } = "indirect";
    public bool UntrustedWrap { get; set; } = true;
    public LlmConfig Llm { get; set; } = new();
    public SandboxConfig Sandbox { get; set; } = new();
    public Dictionary<string, PluginUserConfig> Plugins { get; set; } = new();
    public ToolsConfig Tools { get; set; } = new();

    public static HostConfig Load(string userPath, string projectPath)
    {
        var cfg = File.Exists(userPath)
            ? JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(userPath), Json) ?? new()
            : new HostConfig();

        // Project config may only tune plugin settings. Permissions, secrets and policies stay
        // user-level so a cloned repository cannot loosen them.
        if (File.Exists(projectPath))
        {
            var project = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(projectPath), Json);
            foreach (var (name, pc) in project?.Plugins ?? [])
            {
                if (pc.Config is null) continue;
                var target = cfg.Plugins.TryGetValue(name, out var existing) ? existing : cfg.Plugins[name] = new();
                target.Config ??= new JsonObject();
                foreach (var (k, v) in pc.Config) target.Config[k] = v?.DeepClone();
            }
        }
        return cfg;
    }
}

public sealed class BashPolicy
{
    /// <summary>allow | ask | deny for commands that match no rule.</summary>
    public string DefaultAction { get; set; } = "allow";
    public List<string> Allow { get; set; } = [];
    public List<string> Ask { get; set; } =
    [
        "rm -rf", "find -delete", "git push --force", "git push -f", "git reset --hard", "sudo",
        "curl", "wget", "ssh", "scp", "nc", "mkfs", "dd", "shutdown", "reboot",
    ];
    public List<string> Deny { get; set; } = [];
    /// <summary>What "ask" means when the client cannot ask the user (claude.ai has no elicitation): deny | allow.</summary>
    public string AskWithoutElicitation { get; set; } = "deny";
    public int MaxOutputChars { get; set; } = 30_000;
    /// <summary>Shell override: bash, zsh, sh, pwsh, powershell. Default: bash, or PowerShell on Windows without bash.</summary>
    public string? Shell { get; set; }
}

public sealed class ToolsConfig
{
    /// <summary>Built-in tools to leave out, e.g. ["TodoWrite", "Task"].</summary>
    public List<string> Disabled { get; set; } = [];
    /// <summary>Extra folders with script tools (~/.exec-mcp/tools is always scanned).</summary>
    public List<string> Dirs { get; set; } = [];
    /// <summary>Files the file tools refuse to touch (globs; ~ expands). exec-mcp's own secrets are always included.</summary>
    public List<string> Protected { get; set; } = [];
}

public sealed class LlmConfig
{
    /// <summary>auto | anthropic | claude-cli | sampling | none.</summary>
    public string Provider { get; set; } = "auto";
    public string MainModel { get; set; } = "claude-opus-5-5";
    public string SmallModel { get; set; } = "claude-haiku-5-5";
    public int MaxTurns { get; set; } = 50;
}

public sealed class SandboxConfig
{
    public int TimeoutMs { get; set; } = 60_000;
    public int MemoryMb { get; set; } = 256;
    public int MaxOutputChars { get; set; } = 30_000;
}

public sealed class PluginUserConfig
{
    public bool Enabled { get; set; } = true;
    public JsonObject? Config { get; set; }
    public Dictionary<string, string> Secrets { get; set; } = new();
}
