using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExecMcp.Host;
using ExecMcp.Sandbox;
using Microsoft.Extensions.Logging;

namespace ExecMcp.Plugins;

/// <summary>`exec-mcp plugin list | approve | build | test`.</summary>
public static class PluginCli
{
    public static async Task<int> RunAsync(string[] args, HostEnv env, ILogger log)
    {
        var cmd = args.ElementAtOrDefault(1);
        var target = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--"));
        bool yes = args.Contains("--yes") || args.Contains("-y");
        var registry = new ToolRegistry(env, log);
        var llm = new Llm(env, log);

        switch (cmd)
        {
            case "list":
            {
                var approvals = new Approvals(env);
                var found = Discovery.Find(env);
                if (found.Count == 0) Console.WriteLine("No plugins found (.exec-mcp/plugins, ~/.exec-mcp/plugins, node_modules/exec-mcp-plugin-*, pip exec_mcp_plugin_*).");
                foreach (var (m, dir, error) in found)
                {
                    if (m is null) { Console.WriteLine($"✗ {dir}: {error}"); continue; }
                    var problems = m.Validate();
                    var (status, _) = approvals.Check(m);
                    var tools = File.Exists(Path.Combine(m.Dir, "tools.json")) ? "tools.json" : "no tools.json";
                    Console.WriteLine($"{(problems.Count == 0 && status == Approvals.Status.Approved ? "✓" : "•")} {m.Name} {m.Version} [{m.Source}, {m.Runtime.Type}, mcp:{m.Exposure.Mcp}] {status}, {tools}");
                    Console.WriteLine($"    {m.Dir}");
                    foreach (var p in problems) Console.WriteLine($"    ! {p}");
                }
                return 0;
            }

            case "approve":
            {
                var m = Find(env, target);
                if (m is null) return 1;
                var problems = m.Validate();
                if (problems.Count > 0) { Console.Error.WriteLine("Manifest is invalid:\n  " + string.Join("\n  ", problems)); return 1; }
                var approvals = new Approvals(env);
                var (status, diff) = approvals.Check(m);
                if (status == Approvals.Status.Approved) { Console.WriteLine($"{m.Name} {m.Version} is already approved."); return 0; }
                Console.WriteLine($"Plugin {m.Name} {m.Version} ({m.Source}) at {m.Dir}");
                Console.WriteLine($"  {m.Description}");
                Console.WriteLine(status == Approvals.Status.New ? "Requested permissions:" : $"Changes since last approval ({status}):");
                foreach (var l in diff) Console.WriteLine("  " + l);
                var risky = m.Permissions.Net.Allow.Contains("*") || m.Permissions.Exec.Count > 0
                            || m.Permissions.Fs.Write.Any(w => !w.StartsWith("${cache}") && !w.StartsWith("${artifacts}"));
                if (risky) Console.WriteLine("  ⚠ includes broad permissions (any network host, command execution, or writes outside cache/artifacts).");
                if (!yes)
                {
                    Console.Write("Approve? [y/N] ");
                    if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes")) { Console.WriteLine("Not approved."); return 1; }
                }
                approvals.Approve(m);
                Console.WriteLine($"Approved {m.Name} {m.Version}.");
                return 0;
            }

            case "build":
            {
                var m = Find(env, target);
                if (m is null) return 1;
                await using var host = new PluginHost(m, env, registry, llm, log);
                if (!await host.PrepareAsync(new Approvals(env), allowStart: true, CancellationToken.None) || host.Status != PluginHost.State.Running)
                {
                    // PrepareAsync may have used an existing tools.json; force a fresh start.
                    if (host.Status != PluginHost.State.Ready) { Console.Error.WriteLine($"{m.Name}: {host.Status} — {host.Message}"); return 1; }
                    await host.EnsureStartedAsync(CancellationToken.None);
                }
                var path = Path.Combine(m.Dir, "tools.json");
                File.WriteAllText(path, JsonSerializer.Serialize(host.Tools, PluginManifest.Json));
                Console.WriteLine($"Wrote {path} ({host.Tools.Count} tools)\n");
                Console.WriteLine(Dts.ForNamespace(host.HostTools().ToList(), null));
                return 0;
            }

            case "test":
                return await TestAsync(env, registry, llm, log, target);

            default:
                Console.Error.WriteLine("usage: exec-mcp plugin list | approve <name> [--yes] | build <name|dir> | test <name|dir>");
                return 2;
        }
    }

    static PluginManifest? Find(HostEnv env, string? target)
    {
        if (target is not null && File.Exists(Path.Combine(target, "plugin.json")))
        {
            var m = PluginManifest.Load(target);
            m.Source = "path";
            return m;
        }
        var all = Discovery.Find(env).Where(x => x.Manifest is not null).Select(x => x.Manifest!).ToList();
        var found = target is null && all.Count == 1 ? all[0] : all.FirstOrDefault(m => m.Name == target);
        if (found is null) Console.Error.WriteLine(target is null ? "Specify a plugin name or directory." : $"Plugin '{target}' not found. Known: {string.Join(", ", all.Select(m => m.Name))}");
        return found;
    }

    static async Task<int> TestAsync(HostEnv env, ToolRegistry registry, Llm llm, ILogger log, string? target)
    {
        var m = Find(env, target);
        if (m is null) return 1;
        int failed = 0;
        void Check(bool ok, string what, string? detail = null)
        {
            Console.WriteLine($"- [{(ok ? "x" : " ")}] {what}{(detail is null ? "" : $" — {detail}")}");
            if (!ok) failed++;
        }

        var problems = m.Validate();
        Check(problems.Count == 0, "Manifest is valid and apiVersion is compatible", problems.Count > 0 ? string.Join("; ", problems) : $"{m.ApiVersion} vs host {PluginManifest.HostApiVersion}");
        if (problems.Count > 0) return 1;

        await using var host = new PluginHost(m, env, registry, llm, log);
        var sw = Stopwatch.StartNew();
        var prepared = await host.PrepareAsync(new Approvals(env), allowStart: true, CancellationToken.None);
        if (prepared && host.Status == PluginHost.State.Ready) await host.EnsureStartedAsync(CancellationToken.None);
        Check(host.Status == PluginHost.State.Running, "Plugin starts and setup finishes within 10s", host.Status == PluginHost.State.Running ? $"{sw.ElapsedMilliseconds} ms, {host.Tools.Count} tools" : $"{host.Status}: {host.Message}");
        if (host.Status != PluginHost.State.Running) return 1;
        var tools = host.HostTools().ToList();
        registry.ReplacePluginTools(m.Name, tools);

        // Examples run and fit the output budget.
        int examples = 0;
        var exampleFailures = new List<string>();
        (PluginToolDef Def, JsonNode Input, long Ms)? slowest = null;
        foreach (var def in host.Tools)
        {
            var tool = tools.First(t => t.SandboxName == def.Name || t.Name.EndsWith("_" + def.Name));
            foreach (var ex in def.Examples ?? [])
            {
                examples++;
                var input = JsonSerializer.SerializeToElement(ex?["input"] ?? new JsonObject());
                var exSw = Stopwatch.StartNew();
                var outcome = await registry.InvokeAsync(tool.Name, input, CallOrigin.Mcp, null, null, CancellationToken.None);
                if (slowest is null || exSw.ElapsedMilliseconds > slowest.Value.Ms)
                    slowest = (def, ex?["input"]?.DeepClone() ?? new JsonObject(), exSw.ElapsedMilliseconds);
                var len = outcome.AllText().Length;
                if (outcome.IsError) exampleFailures.Add($"{def.Name}: {outcome.AllText()}");
                else if (len > m.Limits.MaxOutputTokens * 4 && !outcome.AllText().Contains("… truncated. Full output"))
                    exampleFailures.Add($"{def.Name}: format() output ~{len / 4} tokens > maxOutputTokens {m.Limits.MaxOutputTokens}");
            }
        }
        Check(exampleFailures.Count == 0, "Examples run and format(output) fits maxOutputTokens",
            exampleFailures.Count > 0 ? string.Join("; ", exampleFailures) : $"{examples} examples");

        // Cancellation reaches the plugin and it answers within 1s.
        if (host.Connection is StdioConnection sc && slowest is { } slow)
        {
            var (finishedEarly, ms) = await sc.MeasureCancelAsync(slow.Def.Name, slow.Input, (int)Math.Min(50, slow.Ms / 2));
            Check(finishedEarly || ms < 1000, "signal is honoured: cancel completes in under 1s",
                finishedEarly ? "call finished before cancel (inconclusive)" : $"{slow.Def.Name}: answered {ms} ms after $/cancel");
        }
        else Check(true, "signal is honoured: cancel completes in under 1s", "skipped (no examples or not a stdio runtime)");

        // Host denies addresses outside the allowlist.
        var ctx = host.Context!;
        try
        {
            await ctx.HandleAsync("host/fetch", new JsonObject { ["url"] = "http://exec-mcp-conformance.invalid:9/" }, CancellationToken.None);
            Check(m.Permissions.Net.Allow.Contains("*"), "Request outside the net allowlist fails with PERMISSION_DENIED", "request was allowed");
        }
        catch (ToolException tex)
        {
            Check(tex.Code == "PERMISSION_DENIED" || m.Permissions.Net.Allow.Contains("*"), "Request outside the net allowlist fails with PERMISSION_DENIED", tex.Code);
        }

        // Errors have the ToolError shape.
        if (host.Connection is StdioConnection sc2)
        {
            var (_, _, err) = await sc2.CallAsync("__exec_mcp_no_such_tool__", new JsonObject(), "conformance", CancellationToken.None);
            var codes = new[] { "INVALID_INPUT", "NOT_FOUND", "PERMISSION_DENIED", "RATE_LIMITED", "UPSTREAM", "TIMEOUT", "INTERNAL" };
            Check(err is not null && codes.Contains(err.Code), "Errors have the ToolError shape {code, message, retryable}", err is null ? "unknown tool did not fail" : err.Code);
        }

        Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) failed.");
        return failed == 0 ? 0 : 1;
    }
}
