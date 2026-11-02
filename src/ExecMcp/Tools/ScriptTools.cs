using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExecMcp.Host;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ExecMcp.Tools;

/// <summary>A tool described by the comment header of a script file.</summary>
public sealed record ScriptToolSpec(
    string Name,
    string Description,
    JsonObject InputSchema,
    ToolAnnotations Annotations,
    Exposure Exposure,
    TimeSpan Timeout,
    bool JsonOutput,
    string FilePath,
    string[] Command);   // interpreter and its arguments; the script path is appended

/// <summary>
/// Parses headers like:
/// <code>
/// # @tool disk_usage
/// # @description Disk usage of a directory, largest entries first.
/// # @param path string Directory to measure
/// # @param depth integer? =1 How deep to go
/// # @param unit enum(K|M|G)? =M Size unit
/// # @readonly
/// # @timeout 30s
/// </code>
/// Comment markers #, //, --, ; and REM are accepted, so any language works.
/// </summary>
public static partial class ScriptHeader
{
    const int MaxHeaderLines = 80;

    [GeneratedRegex(@"^\s*(?:#+|//+|--|;+|rem\b|::)?\s*@(?<key>[a-z_]+)\b\s*(?<value>.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Directive();

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+(?<type>enum\([^)]*\)|[a-z]+(?:\[\])?)(?<opt>\?)?(?:\s+=(?<def>""[^""]*""|\S+))?(?:\s+(?<desc>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Param();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{0,63}$")]
    private static partial Regex ToolName();

    static readonly Dictionary<string, string[]> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".sh"] = ["bash"], [".bash"] = ["bash"],
        [".py"] = [OperatingSystem.IsWindows() ? "python" : "python3"],
        [".js"] = ["node"], [".mjs"] = ["node"], [".cjs"] = ["node"],
        [".ps1"] = ["pwsh", "-NoProfile", "-NonInteractive", "-File"],
        [".rb"] = ["ruby"], [".pl"] = ["perl"], [".php"] = ["php"],
        [".cmd"] = ["cmd", "/d", "/c"], [".bat"] = ["cmd", "/d", "/c"],
    };

    public static (ScriptToolSpec? Spec, string? Error) Parse(string path, string text)
    {
        string? name = null;
        var description = new List<string>();
        var props = new JsonObject();
        var required = new JsonArray();
        bool readOnly = false, destructive = false, idempotent = false, openWorld = false, json = false;
        var exposure = Exposure.Eager;
        var timeout = TimeSpan.FromSeconds(60);
        bool sawTool = false;

        var lines = text.Replace("\r\n", "\n").Split('\n').Take(MaxHeaderLines).ToArray();
        foreach (var line in lines)
        {
            var m = Directive().Match(line);
            if (!m.Success) continue;
            var key = m.Groups["key"].Value.ToLowerInvariant();
            var value = m.Groups["value"].Value;
            switch (key)
            {
                case "tool":
                    sawTool = true;
                    if (value.Length > 0) name = value;
                    break;
                case "description": description.Add(value); break;
                case "param":
                    var p = Param().Match(value);
                    if (!p.Success) return (null, $"bad @param '{value}': expected `name type[?] [=default] description`");
                    var pname = p.Groups["name"].Value;
                    var (schema, err) = TypeSchema(p.Groups["type"].Value);
                    if (schema is null) return (null, $"@param {pname}: {err}");
                    if (p.Groups["desc"].Success) schema["description"] = p.Groups["desc"].Value;
                    if (p.Groups["def"].Success)
                    {
                        var def = ParseDefault(p.Groups["def"].Value, schema["type"]?.GetValue<string>());
                        if (def is null) return (null, $"@param {pname}: default '{p.Groups["def"].Value}' does not match its type");
                        schema["default"] = def;
                    }
                    else if (!p.Groups["opt"].Success) required.Add(pname);
                    props[pname] = schema;
                    break;
                case "readonly": readOnly = true; break;
                case "destructive": destructive = true; break;
                case "idempotent": idempotent = true; break;
                case "openworld": openWorld = true; break;
                case "deferred": exposure = Exposure.Deferred; break;
                case "hidden": exposure = Exposure.Hidden; break;
                case "output": json = value.Equals("json", StringComparison.OrdinalIgnoreCase); break;
                case "timeout":
                    var t = ParseDuration(value);
                    if (t is null) return (null, $"bad @timeout '{value}' (use 30s, 2m, 500ms)");
                    timeout = t.Value;
                    break;
            }
        }
        if (!sawTool) return (null, null);   // not a tool: no @tool line
        name ??= Path.GetFileNameWithoutExtension(path).Replace('-', '_');
        if (!ToolName().IsMatch(name)) return (null, $"tool name '{name}' must match [a-zA-Z][a-zA-Z0-9_]*");
        if (description.Count == 0) return (null, "@description is required (it is what the model reads)");
        if (readOnly && destructive) return (null, "@readonly and @destructive exclude each other");

        var command = Interpreter(path, lines.FirstOrDefault() ?? "");
        if (command is null) return (null, "no #! line and unknown extension: add a shebang such as #!/usr/bin/env bash");

        var inputSchema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Count > 0) inputSchema["required"] = required;
        var annotations = new ToolAnnotations
        {
            ReadOnlyHint = readOnly,
            // Unknown side effects count as destructive, so indirect calls get confirmed.
            DestructiveHint = !readOnly,
            IdempotentHint = idempotent ? true : null,
            OpenWorldHint = openWorld ? true : null,
        };
        return (new ScriptToolSpec(name, string.Join(' ', description), inputSchema, annotations, exposure, timeout, json, path, command), null);
    }

    static (JsonObject? Schema, string? Error) TypeSchema(string type)
    {
        type = type.ToLowerInvariant();
        if (type.StartsWith("enum("))
        {
            var values = type[5..^1].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return values.Length == 0 ? (null, "enum() needs values like enum(a|b)") :
                (new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) }, null);
        }
        bool array = type.EndsWith("[]");
        var baseType = array ? type[..^2] : type;
        string? jsonType = baseType switch
        {
            "string" or "path" => "string",
            "integer" or "int" => "integer",
            "number" or "float" => "number",
            "boolean" or "bool" => "boolean",
            "object" => "object",
            "array" => "array",
            _ => null,
        };
        if (jsonType is null) return (null, $"unknown type '{type}' (string, integer, number, boolean, object, array, T[], enum(a|b))");
        return array
            ? (new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = jsonType } }, null)
            : (new JsonObject { ["type"] = jsonType }, null);
    }

    static JsonNode? ParseDefault(string raw, string? type)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') raw = raw[1..^1];
        return type switch
        {
            "integer" => long.TryParse(raw, out var l) ? JsonValue.Create(l) : null,
            "number" => double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : null,
            "boolean" => bool.TryParse(raw, out var b) ? JsonValue.Create(b) : null,
            "array" or "object" => TryJson(raw),
            _ => JsonValue.Create(raw),
        };

        static JsonNode? TryJson(string s) { try { return JsonNode.Parse(s); } catch { return null; } }
    }

    public static TimeSpan? ParseDuration(string s)
    {
        var m = Regex.Match(s.Trim(), @"^(\d+(?:\.\d+)?)\s*(ms|s|m|h)?$");
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            _ => TimeSpan.FromSeconds(n),
        };
    }

    /// <summary>From the #! line (also on Windows), else by extension.</summary>
    public static string[]? Interpreter(string path, string firstLine)
    {
        if (firstLine.StartsWith("#!"))
        {
            var parts = firstLine[2..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            if (Path.GetFileName(parts[0]) == "env")
            {
                var rest = parts.Skip(1).Where(p => p != "-S").ToArray();
                return rest.Length == 0 ? null : rest;
            }
            // /bin/bash on Windows: fall back to the program name on PATH.
            if (OperatingSystem.IsWindows() && !File.Exists(parts[0])) parts[0] = Path.GetFileName(parts[0]);
            return parts;
        }
        return ByExtension.TryGetValue(Path.GetExtension(path), out var cmd) ? cmd : null;
    }
}

/// <summary>
/// Script tools: drop a file into ~/.exec-mcp/tools (or a folder from tools.dirs) and it becomes a tool,
/// with no rebuild and no restart. The folders are watched; changes reach clients via tools/list_changed.
/// </summary>
public sealed class ScriptTools(HostEnv env, ToolRegistry registry, ILogger log) : IDisposable
{
    public const string PluginKey = "@scripts";
    public const string Namespace = "tools";
    const int MaxStream = 2 * 1024 * 1024;

    readonly List<FileSystemWatcher> _watchers = [];
    readonly object _reloadLock = new();
    CancellationTokenSource? _debounce;

    public List<ScriptToolSpec> Loaded { get; private set; } = [];
    public List<(string Path, string Error)> Errors { get; private set; } = [];

    public IEnumerable<string> Dirs =>
        new[] { Path.Combine(env.HomeDir, "tools") }.Concat(env.Config.Tools.Dirs.Select(env.Resolve)).Distinct();

    public void LoadAndWatch()
    {
        Directory.CreateDirectory(Path.Combine(env.HomeDir, "tools"));
        Reload();
        foreach (var dir in Dirs.Where(Directory.Exists))
        {
            var w = new FileSystemWatcher(dir) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            w.Changed += (_, _) => Debounced();
            w.Created += (_, _) => Debounced();
            w.Deleted += (_, _) => Debounced();
            w.Renamed += (_, _) => Debounced();
            _watchers.Add(w);
        }
    }

    void Debounced()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        _ = Task.Delay(300, token).ContinueWith(t => { if (!t.IsCanceled) Reload(); }, TaskScheduler.Default);
    }

    /// <summary>Re-scans all folders and replaces the script tool set in the registry.</summary>
    public void Reload()
    {
        lock (_reloadLock)
        {
            var (specs, errors) = Scan(Dirs, registry.All().Where(t => t.Plugin != PluginKey).Select(t => t.Name).ToHashSet());
            Loaded = specs;
            Errors = errors;
            foreach (var (path, error) in errors) log.LogWarning("script tool {Path}: {Error}", path, error);
            registry.ReplacePluginTools(PluginKey, specs.Select(ToHostTool));
            log.LogInformation("script tools: {Count} loaded{Errors}", specs.Count, errors.Count > 0 ? $", {errors.Count} with errors" : "");
        }
    }

    public static (List<ScriptToolSpec>, List<(string, string)>) Scan(IEnumerable<string> dirs, HashSet<string> taken)
    {
        var specs = new List<ScriptToolSpec>();
        var errors = new List<(string, string)>();
        var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var fname = Path.GetFileName(file);
                if (fname.StartsWith('.') || fname.EndsWith('~') || Path.GetExtension(file) is ".md" or ".txt" or ".json" or ".swp") continue;
                string text;
                try
                {
                    if (new FileInfo(file).Length > 1024 * 1024) continue;
                    text = File.ReadAllText(file);
                }
                catch (Exception ex) { errors.Add((file, ex.Message)); continue; }
                var (spec, error) = ScriptHeader.Parse(file, text);
                if (error is not null) { errors.Add((file, error)); continue; }
                if (spec is null) continue;
                if (!names.Add(spec.Name)) { errors.Add((file, $"tool name '{spec.Name}' is already taken")); continue; }
                specs.Add(spec);
            }
        return (specs, errors);
    }

    HostTool ToHostTool(ScriptToolSpec spec) => new()
    {
        Name = spec.Name,
        Description = spec.Description,
        InputSchema = JsonSerializer.SerializeToElement(spec.InputSchema),
        Annotations = spec.Annotations,
        Exposure = spec.Exposure,
        Plugin = PluginKey,
        Namespace = Namespace,
        SandboxName = spec.Name,
        MaxOutputChars = env.Config.Bash.MaxOutputChars,
        Handler = call => RunAsync(spec, call, env),
    };

    public static async Task<ToolOutcome> RunAsync(ScriptToolSpec spec, ToolCall call, HostEnv env)
    {
        var psi = new ProcessStartInfo(spec.Command[0])
        {
            WorkingDirectory = env.Workspace,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Command.Skip(1)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(spec.FilePath);

        // Arguments: JSON on stdin and in EXEC_MCP_INPUT, plus one environment variable per parameter
        // (defaults applied; arrays and objects as JSON).
        var input = JsonNode.Parse(call.Args.GetRawText()) as JsonObject ?? new JsonObject();
        foreach (var (key, schema) in spec.InputSchema["properties"]!.AsObject())
            if (!input.ContainsKey(key) && schema?["default"] is { } def) input[key] = def.DeepClone();
        var inputJson = input.ToJsonString();
        foreach (var (key, value) in input)
            psi.Environment[key] = value switch
            {
                null => "",
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v => v.ToJsonString(),
                _ => value.ToJsonString(),
            };
        psi.Environment["EXEC_MCP_INPUT"] = inputJson;
        psi.Environment["EXEC_MCP_WORKSPACE"] = env.Workspace;
        psi.Environment["EXEC_MCP_TOOL"] = spec.Name;
        psi.Environment["NO_COLOR"] = "1";

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        {
            return ToolOutcome.Error($"Cannot start {string.Join(' ', spec.Command)} for {spec.FilePath}: {ex.Message}", "INTERNAL");
        }
        using (proc)
        {
            var stdout = ReadCapped(proc.StandardOutput);
            var stderr = ReadCapped(proc.StandardError);
            try
            {
                await proc.StandardInput.WriteAsync(inputJson);
                proc.StandardInput.Close();
            }
            catch (IOException) { /* the script does not read stdin */ }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(call.Ct);
            cts.CancelAfter(spec.Timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return ToolOutcome.Error(call.Ct.IsCancellationRequested ? "Cancelled." : $"{spec.Name} timed out after {spec.Timeout.TotalSeconds:0.#}s.", "TIMEOUT");
            }
            var outText = (await stdout).TrimEnd();
            var errText = (await stderr).TrimEnd();

            if (proc.ExitCode != 0)
            {
                var msg = new StringBuilder($"{spec.Name} failed (exit code {proc.ExitCode}).");
                if (errText.Length > 0) msg.Append('\n').Append(Tail(errText, 4000));
                if (outText.Length > 0) msg.Append('\n').Append(Tail(outText, 4000));
                return ToolOutcome.Error(msg.ToString(), proc.ExitCode is 2 or 64 ? "INVALID_INPUT" : "UPSTREAM");
            }

            if (spec.JsonOutput && outText.Length > 0)
            {
                JsonNode? node;
                try { node = JsonNode.Parse(outText); }
                catch (JsonException ex) { return ToolOutcome.Error($"{spec.Name} declared @output json but printed invalid JSON: {ex.Message}", "INTERNAL"); }
                var o = ToolOutcome.Text(node?.ToJsonString() ?? "null");
                o.Value = node;
                o.Structured = node as JsonObject;
                return o;
            }
            return ToolOutcome.Text(outText.Length > 0 ? outText : errText.Length > 0 ? errText : "(no output)");
        }
    }

    static async Task<string> ReadCapped(StreamReader r)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        while ((n = await r.ReadAsync(buf)) > 0)
            if (sb.Length < MaxStream) sb.Append(buf, 0, Math.Min(n, MaxStream - sb.Length));
        return sb.ToString();
    }

    static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
    }
}
