using ExecMcp.Host;
using Microsoft.Extensions.Logging;

namespace ExecMcp.Plugins;

public sealed class PluginManager(HostEnv env, ToolRegistry registry, Llm llm, ILogger log) : IAsyncDisposable
{
    readonly List<PluginHost> _plugins = [];
    readonly List<string> _broken = [];
    readonly List<FileSystemWatcher> _watchers = [];
    readonly Approvals _approvals = new(env);

    public IReadOnlyList<PluginHost> Plugins => _plugins;
    public Approvals Approvals => _approvals;

    public async Task LoadAllAsync(bool allowStart, CancellationToken ct)
    {
        foreach (var (m, dir, error) in Discovery.Find(env))
        {
            if (m is null) { _broken.Add($"{dir}: {error}"); log.LogWarning("plugin at {Dir}: {Error}", dir, error); continue; }
            var host = new PluginHost(m, env, registry, llm, log);
            _plugins.Add(host);
        }
        // Prepare in parallel: starting plugins without tools.json can take a moment each.
        await Task.WhenAll(_plugins.Select(p => p.PrepareAsync(_approvals, allowStart, ct)));
        foreach (var p in _plugins.OrderBy(p => p.Namespace, StringComparer.Ordinal))
            if (p.Status is PluginHost.State.Ready or PluginHost.State.Running)
                registry.ReplacePluginTools(p.Manifest.Name, p.HostTools());
        if (env.Dev) Watch();
    }

    /// <summary>Lines for the server instructions about plugins that could not load (so the model can tell the user).</summary>
    public IEnumerable<string> Notes()
    {
        var problems = _plugins.Where(p => p.Status is not (PluginHost.State.Ready or PluginHost.State.Running))
            .Select(p => $"- plugin {p.Manifest.Name}: {p.Status} — {p.Message}")
            .Concat(_broken.Select(b => $"- {b}")).ToList();
        if (problems.Count == 0) yield break;
        yield return "\nPlugins not loaded (tell the user if they ask for this functionality):";
        foreach (var l in problems) yield return l;
    }

    void Watch()
    {
        foreach (var p in _plugins)
        {
            var plugin = p;
            var w = new FileSystemWatcher(p.Manifest.Dir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            CancellationTokenSource? debounce = null;
            void Changed(object _, FileSystemEventArgs e)
            {
                var rel = Path.GetRelativePath(plugin.Manifest.Dir, e.FullPath);
                if (rel.StartsWith("node_modules") || rel.Contains("__pycache__") || rel.StartsWith(".git")) return;
                debounce?.Cancel();
                debounce = new CancellationTokenSource();
                var token = debounce.Token;
                _ = Task.Delay(400, token).ContinueWith(async t =>
                {
                    if (t.IsCanceled) return;
                    log.LogInformation("dev: reloading plugin {Plugin} ({File} changed)", plugin.Manifest.Name, rel);
                    await plugin.ReloadAsync(_approvals, CancellationToken.None);
                    registry.ReplacePluginTools(plugin.Manifest.Name,
                        plugin.Status is PluginHost.State.Ready or PluginHost.State.Running ? plugin.HostTools() : []);
                }, TaskScheduler.Default);
            }
            w.Changed += Changed;
            w.Created += Changed;
            w.Deleted += Changed;
            w.Renamed += Changed;
            _watchers.Add(w);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _watchers) w.Dispose();
        await Task.WhenAll(_plugins.Select(p => p.DisposeAsync().AsTask()));
    }
}
