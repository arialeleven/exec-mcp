using System.Text.Json;
using ExecMcp.Host;
using ExecMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExecMcp.Tests;

/// <summary>An isolated workspace + home folder per test, with a registry of core tools.</summary>
public sealed class TestEnv : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "exec-mcp-tests", Guid.NewGuid().ToString("n"));
    public HostEnv Env { get; }
    public ToolRegistry Registry { get; }
    public FileState Files { get; } = new();
    public BashTools Bash { get; }

    public TestEnv(Action<HostConfig>? configure = null)
    {
        var config = new HostConfig();
        configure?.Invoke(config);
        Directory.CreateDirectory(Path.Combine(Root, "ws"));
        Directory.CreateDirectory(Path.Combine(Root, "home"));
        Directory.CreateDirectory(Path.Combine(Root, "run"));
        Env = new HostEnv
        {
            Workspace = Path.Combine(Root, "ws"),
            HomeDir = Path.Combine(Root, "home"),
            Config = config,
            RunDir = Path.Combine(Root, "run"),
        };
        Registry = new ToolRegistry(Env, NullLogger.Instance);
        Bash = new BashTools(Env, NullLogger.Instance);
        Registry.Register(ReadTool.Create(Env, Files) with { Namespace = "core", SandboxName = "Read" });
        Registry.Register(EditTools.Edit(Env, Files) with { Namespace = "core", SandboxName = "Edit" });
        Registry.Register(EditTools.MultiEdit(Env, Files));
        Registry.Register(EditTools.Write(Env, Files) with { Namespace = "core", SandboxName = "Write" });
        Registry.Register(SearchTools.Glob(Env));
        Registry.Register(SearchTools.Grep(Env));
        Registry.Register(Bash.Bash() with { Namespace = "core", SandboxName = "Bash" });
        Registry.Register(Bash.BashOutput());
        Registry.Register(MiscTools.TodoWrite());
        Registry.Register(MiscTools.ToolSearch(Registry));
        Registry.Register(Sandbox.ExecSandbox.Create(Env, Registry));
    }

    public string Ws(string rel) => Path.Combine(Env.Workspace, rel);

    public string WriteFile(string rel, string content)
    {
        var p = Ws(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    public Task<ToolOutcome> Call(string tool, object args, CallOrigin origin = CallOrigin.Mcp) =>
        Registry.InvokeAsync(tool, JsonSerializer.SerializeToElement(args), origin, null, null, CancellationToken.None);

    public async Task<string> Text(string tool, object args)
    {
        var o = await Call(tool, args);
        Assert.False(o.IsError, $"{tool} failed: {o.AllText()}");
        return o.AllText();
    }

    public void Dispose()
    {
        Bash.KillAll();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

public static class Requires
{
    public static bool Unix => !OperatingSystem.IsWindows();

    public static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(d => File.Exists(Path.Combine(d, exe)) || File.Exists(Path.Combine(d, exe + ".exe")));
}
