using System.Text.Json;
using ExecMcp.Host;
using Microsoft.Extensions.Logging;

namespace ExecMcp.Tools;

/// <summary>`exec-mcp tools list | new | run`.</summary>
public static class ScriptToolsCli
{
    static readonly Dictionary<string, (string Ext, string Template)> Templates = new()
    {
        ["sh"] = (".sh", """
            #!/usr/bin/env bash
            # @tool {name}
            # @description Say what the tool does and what it returns, in one or two sentences.
            # @param path string Directory to inspect
            # @param limit integer? =20 Max lines of output
            # @readonly
            # @timeout 30s
            set -euo pipefail
            # Parameters arrive as environment variables ($path, $limit) and as JSON on stdin.
            ls -la -- "$path" | head -n "$limit"
            """),
        ["py"] = (".py", """
            #!/usr/bin/env python3
            # @tool {name}
            # @description Say what the tool does and what it returns, in one or two sentences.
            # @param text string Text to analyse
            # @param top integer? =5 How many items to return
            # @readonly
            # @output json
            import collections, json, re, sys

            args = json.load(sys.stdin)            # the same values are also in os.environ
            words = re.findall(r"\w+", args["text"].lower())
            print(json.dumps({"words": len(words), "top": collections.Counter(words).most_common(args["top"])}))
            """),
        ["js"] = (".mjs", """
            #!/usr/bin/env node
            // @tool {name}
            // @description Say what the tool does and what it returns, in one or two sentences.
            // @param url string URL to check
            // @readonly
            // @openworld
            // @timeout 15s
            const args = JSON.parse(await new Promise((r) => { let s = ""; process.stdin.on("data", (d) => (s += d)).on("end", () => r(s)); }));
            const res = await fetch(args.url, { method: "HEAD" });
            console.log(`${res.status} ${res.headers.get("content-type") ?? ""}`);
            """),
        ["ps1"] = (".ps1", """
            #!/usr/bin/env pwsh
            # @tool {name}
            # @description Say what the tool does and what it returns, in one or two sentences.
            # @param name string Service name
            # @readonly
            $in = [Console]::In.ReadToEnd() | ConvertFrom-Json
            Get-Service -Name $in.name | Format-List Name, Status, StartType
            """),
    };

    public static async Task<int> RunAsync(string[] args, HostEnv env, ILogger log)
    {
        var registry = new ToolRegistry(env, log);
        using var tools = new ScriptTools(env, registry, log);
        switch (args.ElementAtOrDefault(1))
        {
            case "list":
            {
                tools.Reload();
                Console.WriteLine($"Folders: {string.Join(", ", tools.Dirs)}");
                foreach (var t in tools.Loaded)
                {
                    var flags = string.Join(",", new[]
                    {
                        t.Annotations.ReadOnlyHint == true ? "readonly" : "destructive",
                        t.Exposure != Exposure.Eager ? t.Exposure.ToString().ToLowerInvariant() : null,
                        t.JsonOutput ? "json" : null,
                    }.OfType<string>());
                    Console.WriteLine($"✓ {t.Name} [{flags}] {t.FilePath}\n    {t.Description}");
                    foreach (var (name, schema) in t.InputSchema["properties"]!.AsObject())
                        Console.WriteLine($"    - {name}: {schema?["type"]}{(t.InputSchema["required"]?.AsArray().Any(r => r?.GetValue<string>() == name) == true ? "" : "?")} {schema?["description"]}");
                }
                foreach (var (path, error) in tools.Errors) Console.WriteLine($"✗ {path}: {error}");
                if (tools.Loaded.Count == 0 && tools.Errors.Count == 0) Console.WriteLine("No script tools yet. Create one with: exec-mcp tools new my_tool --lang sh");
                return tools.Errors.Count == 0 ? 0 : 1;
            }
            case "new":
            {
                var name = args.ElementAtOrDefault(2);
                if (name is null || name.StartsWith('-')) { Console.Error.WriteLine("usage: exec-mcp tools new <name> [--lang sh|py|js|ps1]"); return 2; }
                var li = Array.IndexOf(args, "--lang");
                var lang = li >= 0 && li + 1 < args.Length ? args[li + 1] : OperatingSystem.IsWindows() ? "ps1" : "sh";
                if (!Templates.TryGetValue(lang, out var tpl)) { Console.Error.WriteLine($"unknown --lang {lang}"); return 2; }
                var dir = Path.Combine(env.HomeDir, "tools");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name + tpl.Ext);
                if (File.Exists(path)) { Console.Error.WriteLine($"{path} already exists"); return 1; }
                await File.WriteAllTextAsync(path, tpl.Template.Replace("{name}", name) + "\n");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
                Console.WriteLine($"Created {path}\nEdit the header and body; a running exec-mcp picks the change up immediately.");
                return 0;
            }
            case "run":
            {
                tools.Reload();
                var name = args.ElementAtOrDefault(2);
                var spec = tools.Loaded.FirstOrDefault(t => t.Name == name);
                if (spec is null)
                {
                    Console.Error.WriteLine($"No script tool '{name}'. Known: {string.Join(", ", tools.Loaded.Select(t => t.Name))}");
                    return 1;
                }
                var json = args.ElementAtOrDefault(3) ?? "{}";
                var outcome = await registry.InvokeAsync(spec.Name, JsonDocument.Parse(json).RootElement, CallOrigin.Mcp, null, null, CancellationToken.None);
                Console.WriteLine(outcome.AllText());
                if (outcome.Value is not null && outcome.Structured is null) Console.WriteLine($"value: {outcome.Value.ToJsonString()}");
                return outcome.IsError ? 1 : 0;
            }
            default:
                Console.Error.WriteLine("usage: exec-mcp tools list | new <name> [--lang sh|py|js|ps1] | run <name> ['{json}']");
                return 2;
        }
    }
}
