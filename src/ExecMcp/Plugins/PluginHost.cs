using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExecMcp.Host;
using Json.Schema;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ExecMcp.Plugins;

/// <summary>A tool as a plugin declares it (initialize result or tools.json).</summary>
public sealed class PluginToolDef
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonNode? InputSchema { get; set; }
    public JsonNode? OutputSchema { get; set; }
    public ToolAnnotations? Annotations { get; set; }
    public JsonObject? Cost { get; set; }
    public JsonArray? Examples { get; set; }
}

interface IPluginConnection : IAsyncDisposable
{
    Task<List<PluginToolDef>> StartAsync(JsonObject config, CancellationToken ct);
    Task<(JsonNode? Output, string? Text, ToolException? Error)> CallAsync(string tool, JsonNode input, string callId, CancellationToken ct);
    event Action<int>? Exited;
}

/// <summary>Lifecycle of one plugin: discover → manifest → config → approve → load → setup → ready → dispose.</summary>
public sealed class PluginHost : IAsyncDisposable
{
    public enum State { Invalid, NotApproved, Ready, Running, Disabled, Error }

    readonly HostEnv _env;
    readonly ToolRegistry _registry;
    readonly Llm _llm;
    readonly ILogger _log;
    readonly SemaphoreSlim _startLock = new(1, 1);
    readonly ConcurrentDictionary<string, ToolCall> _active = new();
    readonly Queue<DateTime> _rate = new();
    SemaphoreSlim _concurrency;
    IPluginConnection? _conn;
    PluginContextHost? _ctx;
    int _crashes;
    DateTime _restartNotBefore;
    bool _stopping;

    public PluginManifest Manifest { get; private set; }
    public State Status { get; private set; } = State.Invalid;
    public string Message { get; private set; } = "";
    public List<PluginToolDef> Tools { get; private set; } = [];
    public JsonObject Config { get; private set; } = new();
    internal IPluginConnection? Connection => _conn;
    public PluginContextHost? Context => _ctx;

    public PluginHost(PluginManifest m, HostEnv env, ToolRegistry registry, Llm llm, ILogger log)
    {
        Manifest = m;
        _env = env;
        _registry = registry;
        _llm = llm;
        _log = log;
        _concurrency = new SemaphoreSlim(m.Limits.Concurrency);
    }

    public string Namespace => Manifest.Namespace.Replace('-', '_');

    /// <summary>Validates manifest/config/secrets/approval and gets the tool list (tools.json or by starting the plugin).</summary>
    public async Task<bool> PrepareAsync(Approvals approvals, bool allowStart, CancellationToken ct)
    {
        var errors = Manifest.Validate();
        if (errors.Count > 0) return Fail(State.Invalid, string.Join("; ", errors));

        if (_env.Config.Plugins.TryGetValue(Manifest.Name, out var uc) && !uc.Enabled)
            return Fail(State.Disabled, "disabled in config");

        var (cfg, cfgError) = BuildConfig(Manifest, _env);
        if (cfgError is not null) return Fail(State.Invalid, cfgError);
        Config = cfg!;

        var (status, diff) = approvals.Check(Manifest);
        if (status != Approvals.Status.Approved)
            return Fail(State.NotApproved, status switch
            {
                Approvals.Status.New => "not approved yet",
                Approvals.Status.PermissionsChanged => "permissions changed since approval: " + string.Join(", ", diff),
                _ => diff.FirstOrDefault() ?? "changed",
            } + $" — run `exec-mcp plugin approve {Manifest.Name}`");

        _ctx = new PluginContextHost(Manifest, _env, _registry, _llm, id => _active.TryGetValue(id, out var c) ? c : null);
        foreach (var s in Manifest.Permissions.Secrets)
            if (string.IsNullOrEmpty(_ctx.SecretValue(s)))
                return Fail(State.Error, $"secret {s} is not set (environment variable or plugins.{Manifest.Name}.secrets in ~/.exec-mcp/config.json)");

        var toolsJson = Path.Combine(Manifest.Dir, "tools.json");
        if (File.Exists(toolsJson) && !_env.Dev)
        {
            Tools = JsonSerializer.Deserialize<List<PluginToolDef>>(File.ReadAllText(toolsJson), PluginManifest.Json) ?? [];
            Status = State.Ready;
            return true;
        }
        if (!allowStart) return Fail(State.Error, "no tools.json; run `exec-mcp plugin build`");
        try
        {
            await EnsureStartedAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            return Fail(State.Error, ex.Message);
        }
    }

    bool Fail(State s, string msg)
    {
        Status = s;
        Message = msg;
        _log.LogWarning("plugin {Plugin}: {Message}", Manifest.Name, msg);
        return false;
    }

    /// <summary>Config from ~/.exec-mcp/config.json (+ project overrides) with schema defaults, validated by manifest.config.</summary>
    public static (JsonObject? Config, string? Error) BuildConfig(PluginManifest m, HostEnv env)
    {
        var cfg = env.Config.Plugins.TryGetValue(m.Name, out var uc) && uc.Config is not null
            ? (JsonObject)uc.Config.DeepClone()
            : new JsonObject();
        if (m.Config is null) return (cfg, null);
        if (m.Config["properties"] is JsonObject props)
            foreach (var (k, v) in props)
                if (!cfg.ContainsKey(k) && v?["default"] is { } def) cfg[k] = def.DeepClone();
        var schema = JsonSchema.FromText(m.Config.ToJsonString());
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(cfg), new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return (cfg, null);
        var msgs = (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Where(e => e.Key != "properties").Select(e => $"config{d.InstanceLocation.ToString().Replace('/', '.')}: {e.Value}"));
        return (null, string.Join("; ", msgs));
    }

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_conn is not null) return;
        await _startLock.WaitAsync(ct);
        try
        {
            if (_conn is not null) return;
            if (Status == State.Disabled) throw new ToolException("UPSTREAM", $"plugin {Manifest.Name} is disabled: {Message}");
            var wait = _restartNotBefore - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            var conn = CreateConnection();
            conn.Exited += code => OnExited(conn, code);
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            setup.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                Tools = await conn.StartAsync(Config, setup.Token);
            }
            catch (Exception ex)
            {
                await conn.DisposeAsync();
                var msg = setup.IsCancellationRequested && !ct.IsCancellationRequested ? "setup timed out after 10s" : ex.Message;
                throw new ToolException("UPSTREAM", $"plugin {Manifest.Name} failed to start: {msg}", retryable: true);
            }
            _conn = conn;
            Status = State.Running;
            Message = "";
            _log.LogInformation("plugin {Plugin} started with {Count} tools", Manifest.Name, Tools.Count);
        }
        finally { _startLock.Release(); }
    }

    IPluginConnection CreateConnection()
    {
        var m = Manifest;
        if (m.Runtime.Type == "mcp") return new McpConnection(m, _ctx!, _log);

        ProcessStartInfo psi;
        var sdk = Path.Combine(AppContext.BaseDirectory, "sdk");
        switch (m.Runtime.Type)
        {
            case "node":
                psi = new ProcessStartInfo(Which("node") ?? throw new ToolException("UPSTREAM", "node is not installed"));
                psi.ArgumentList.Add($"--max-old-space-size={m.Limits.MemoryMb}");
                psi.ArgumentList.Add(Path.Combine(sdk, "node", "runner.mjs"));
                psi.ArgumentList.Add(Path.Combine(m.Dir, m.Runtime.Entry!));
                break;
            case "python":
                psi = new ProcessStartInfo(Which(OperatingSystem.IsWindows() ? "python" : "python3") ?? throw new ToolException("UPSTREAM", "python is not installed"));
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(Path.Combine(sdk, "python", "exec_mcp_plugin", "runner.py"));
                psi.ArgumentList.Add(Path.Combine(m.Dir, m.Runtime.Entry!));
                psi.Environment["PYTHONPATH"] = Path.Combine(sdk, "python") + Path.PathSeparator + m.Dir;
                psi.Environment["EXEC_MCP_MEMORY_MB"] = m.Limits.MemoryMb.ToString();
                break;
            default: // stdio
                var cmd = m.Runtime.Command ?? m.Runtime.Entry!;
                psi = new ProcessStartInfo(File.Exists(Path.Combine(m.Dir, cmd)) ? Path.Combine(m.Dir, cmd) : cmd);
                foreach (var a in m.Runtime.Args) psi.ArgumentList.Add(_ctx!.Expand(a));
                break;
        }
        psi.WorkingDirectory = m.Dir;
        foreach (var (k, v) in m.Runtime.Env) psi.Environment[k] = _ctx!.Expand(v);
        // Secrets reach the plugin only through ctx.secrets, never through the environment.
        foreach (var s in m.Permissions.Secrets) psi.Environment.Remove(s);
        return new StdioConnection(psi, m, _ctx!, _log, (id, msg, ratio) =>
        {
            if (_active.TryGetValue(id, out var call)) _ = call.ProgressAsync(msg, ratio);
        });
    }

    static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : [""])
                if (File.Exists(Path.Combine(dir, name + ext))) return Path.Combine(dir, name + ext);
        return null;
    }

    void OnExited(IPluginConnection conn, int code)
    {
        if (_stopping || !ReferenceEquals(conn, _conn)) return;
        _conn = null;
        _crashes++;
        _log.LogWarning("plugin {Plugin} exited unexpectedly (code {Code}), crash {N}/3", Manifest.Name, code, _crashes);
        if (_crashes >= 3)
        {
            Status = State.Disabled;
            Message = $"disabled after {_crashes} consecutive crashes";
            _registry.MarkDisabled(Namespace + "_", $"Plugin {Manifest.Name} was {Message} (exit code {code}). Tell the user; it is re-enabled when exec-mcp restarts.");
            _registry.ReplacePluginTools(Manifest.Name, []);
            _log.LogError("plugin {Plugin} {Message}", Manifest.Name, Message);
        }
        else
        {
            Status = State.Ready;
            _restartNotBefore = DateTime.UtcNow.AddSeconds(Math.Pow(2, _crashes - 1));  // 1s, 2s backoff
        }
        _ = conn.DisposeAsync();
    }

    public IEnumerable<HostTool> HostTools()
    {
        var m = Manifest;
        var exposure = m.Exposure.Mcp switch { "eager" => Exposure.Eager, "hidden" => Exposure.Hidden, _ => Exposure.Deferred };
        foreach (var t in Tools)
        {
            var def = t;
            yield return new HostTool
            {
                Name = $"{Namespace}_{def.Name}",
                Description = def.Description,
                InputSchema = JsonSerializer.SerializeToElement(def.InputSchema ?? new JsonObject { ["type"] = "object" }),
                OutputSchema = def.OutputSchema is null ? null : JsonSerializer.SerializeToElement(def.OutputSchema),
                Annotations = def.Annotations ?? new ToolAnnotations(),
                Exposure = exposure,
                Plugin = m.Name,
                Namespace = m.Exposure.Sandbox ? Namespace : null,
                SandboxName = m.Exposure.Sandbox ? def.Name : null,
                MaxOutputChars = m.Limits.MaxOutputTokens * 4,
                CostHint = def.Cost?["latencyMs"] is { } lat ? $"~{lat}ms" : null,
                Handler = call => CallAsync(def, call),
            };
        }
    }

    async Task<ToolOutcome> CallAsync(PluginToolDef def, ToolCall call)
    {
        // Rate limit: sliding one-minute window.
        lock (_rate)
        {
            while (_rate.Count > 0 && _rate.Peek() < DateTime.UtcNow.AddMinutes(-1)) _rate.Dequeue();
            if (_rate.Count >= Manifest.Limits.RatePerMin)
            {
                var retry = (int)(_rate.Peek().AddMinutes(1) - DateTime.UtcNow).TotalMilliseconds;
                return ToolOutcome.Error($"Rate limit of {Manifest.Limits.RatePerMin}/min reached for plugin {Manifest.Name}.", "RATE_LIMITED", true, retry);
            }
            _rate.Enqueue(DateTime.UtcNow);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Ct);
        timeout.CancelAfter(Manifest.Limits.TimeoutMs);
        if (!await _concurrency.WaitAsync(Manifest.Limits.TimeoutMs, call.Ct))
            return ToolOutcome.Error($"Plugin {Manifest.Name} is busy (concurrency {Manifest.Limits.Concurrency}).", "RATE_LIMITED", true, 1000);
        var callId = Guid.NewGuid().ToString("n");
        try
        {
            await EnsureStartedAsync(timeout.Token);
            _active[callId] = call;
            var input = JsonNode.Parse(call.Args.GetRawText())!;
            var (output, text, error) = await _conn!.CallAsync(def.Name, input, callId, timeout.Token);
            if (error is not null) return ToolOutcome.Error(error.Message, error.Code, error.Retryable, error.RetryAfterMs);
            _crashes = 0;
            text ??= output is JsonValue v && v.TryGetValue<string>(out var s) ? s : output?.ToJsonString() ?? "null";
            return new ToolOutcome
            {
                Content = [new TextContentBlock { Text = text }],
                Structured = output as JsonObject,
                Value = output,
            };
        }
        catch (OperationCanceledException) when (!call.Ct.IsCancellationRequested)
        {
            return ToolOutcome.Error($"{Manifest.Name}.{def.Name} timed out after {Manifest.Limits.TimeoutMs} ms.", "TIMEOUT");
        }
        catch (RpcException ex)
        {
            return ToolOutcome.Error($"Plugin {Manifest.Name} failed: {ex.Message}", "UPSTREAM", retryable: Status != State.Disabled);
        }
        finally
        {
            _active.TryRemove(callId, out _);
            _concurrency.Release();
        }
    }

    /// <summary>Hot reload: re-read the manifest, restart, and return the new tool set.</summary>
    public async Task ReloadAsync(Approvals approvals, CancellationToken ct)
    {
        await StopAsync();
        _crashes = 0;
        _restartNotBefore = default;
        try { Manifest = PluginManifest.Load(Manifest.Dir); }
        catch (Exception ex) { Fail(State.Invalid, ex.Message); return; }
        _concurrency = new SemaphoreSlim(Manifest.Limits.Concurrency);
        Tools = [];
        await PrepareAsync(approvals, allowStart: true, ct);
    }

    public async Task StopAsync()
    {
        _stopping = true;
        var c = _conn;
        _conn = null;
        if (c is not null) await c.DisposeAsync();
        _stopping = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>node / python / stdio runtimes: the exec-mcp plugin protocol over stdio.</summary>
sealed class StdioConnection : IPluginConnection
{
    readonly StdioRpc _rpc;
    readonly PluginManifest _m;

    public event Action<int>? Exited;

    public StdioConnection(ProcessStartInfo psi, PluginManifest m, PluginContextHost ctx, ILogger log, Action<string, string?, double?> progress)
    {
        _m = m;
        _rpc = new StdioRpc(psi, m.Name, log)
        {
            OnRequest = ctx.HandleAsync,
            OnNotification = (method, p) =>
            {
                if (method == "$/progress" && p?["callId"]?.GetValue<string>() is { } id)
                    progress(id, p["message"]?.GetValue<string>(), p["ratio"]?.GetValue<double>());
            },
        };
        _rpc.Exited += code => Exited?.Invoke(code);
    }

    public async Task<List<PluginToolDef>> StartAsync(JsonObject config, CancellationToken ct)
    {
        var result = await _rpc.RequestAsync("initialize", new JsonObject
        {
            ["apiVersion"] = PluginManifest.HostApiVersion,
            ["name"] = _m.Name,
            ["config"] = config.DeepClone(),
            ["permissions"] = JsonNode.Parse(_m.Permissions.Canonical()),
        }, ct);
        return result?["tools"]?.Deserialize<List<PluginToolDef>>(PluginManifest.Json) ?? [];
    }

    public async Task<(JsonNode?, string?, ToolException?)> CallAsync(string tool, JsonNode input, string callId, CancellationToken ct)
    {
        JsonNode? result;
        try
        {
            result = await _rpc.RequestAsync("tools/call", new JsonObject { ["callId"] = callId, ["name"] = tool, ["input"] = input }, ct);
        }
        catch (OperationCanceledException)
        {
            // Tell the plugin; it must abort via ctx.signal. The host does not wait for it.
            _ = _rpc.NotifyAsync("$/cancel", new JsonObject { ["callId"] = callId });
            throw;
        }
        catch (RpcException ex) when (ex.Data2 is JsonObject d)
        {
            return (null, null, ToError(d, ex.Message));
        }
        if (result?["error"] is JsonObject err) return (null, null, ToError(err, err["message"]?.GetValue<string>() ?? "error"));
        return (result?["output"]?.DeepClone(), result?["text"]?.GetValue<string>(), null);
    }

    /// <summary>Conformance: starts a call, cancels it after a delay and measures how long the plugin takes to answer.</summary>
    public async Task<(bool FinishedEarly, long Ms)> MeasureCancelAsync(string tool, JsonNode input, int cancelAfterMs)
    {
        var callId = "cancel-test";
        var task = _rpc.RequestAsync("tools/call", new JsonObject { ["callId"] = callId, ["name"] = tool, ["input"] = input }, CancellationToken.None);
        if (await Task.WhenAny(task, Task.Delay(cancelAfterMs)) == task) return (true, 0);
        var sw = Stopwatch.StartNew();
        await _rpc.NotifyAsync("$/cancel", new JsonObject { ["callId"] = callId });
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        return (false, sw.ElapsedMilliseconds);
    }

    static ToolException ToError(JsonObject e, string message) => new(
        e["code"]?.GetValue<string>() ?? "INTERNAL",
        e["message"]?.GetValue<string>() ?? message,
        e["retryable"]?.GetValue<bool>() ?? false,
        e["retryAfterMs"]?.GetValue<int>());

    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
}

/// <summary>runtime: mcp — wraps an existing MCP server. Permissions apply at the process level only (secrets via env).</summary>
sealed class McpConnection(PluginManifest m, PluginContextHost ctx, ILogger log) : IPluginConnection
{
    McpClient? _client;

    public event Action<int>? Exited;

    public async Task<List<PluginToolDef>> StartAsync(JsonObject config, CancellationToken ct)
    {
        var env = m.Runtime.Env.ToDictionary(kv => kv.Key, kv => (string?)ctx.Expand(kv.Value));
        foreach (var s in m.Permissions.Secrets) env[s] = ctx.SecretValue(s);
        var cmd = m.Runtime.Command ?? m.Runtime.Entry!;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = m.Name,
            Command = File.Exists(Path.Combine(m.Dir, cmd)) ? Path.Combine(m.Dir, cmd) : cmd,
            Arguments = m.Runtime.Args.Select(ctx.Expand).ToList(),
            WorkingDirectory = m.Dir,
            EnvironmentVariables = env,
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var tools = await _client.ListToolsAsync(cancellationToken: ct);
        return tools.Select(t => new PluginToolDef
        {
            Name = t.Name.Replace('-', '_').Replace('.', '_'),
            Description = t.Description ?? "",
            InputSchema = JsonNode.Parse(t.ProtocolTool.InputSchema.GetRawText()),
            OutputSchema = t.ProtocolTool.OutputSchema is { } os ? JsonNode.Parse(os.GetRawText()) : null,
            Annotations = t.ProtocolTool.Annotations,
            Cost = new JsonObject { ["originalName"] = t.Name },
        }).ToList();
    }

    public async Task<(JsonNode?, string?, ToolException?)> CallAsync(string tool, JsonNode input, string callId, CancellationToken ct)
    {
        var original = tool;
        var tools = await _client!.ListToolsAsync(cancellationToken: ct);
        original = tools.FirstOrDefault(t => t.Name.Replace('-', '_').Replace('.', '_') == tool)?.Name ?? tool;
        var args = input.Deserialize<Dictionary<string, object?>>() ?? [];
        CallToolResult result;
        try { result = await _client.CallToolAsync(original, args, cancellationToken: ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is IOException or ObjectDisposedException) Exited?.Invoke(-1);
            return (null, null, new ToolException("UPSTREAM", ex.Message, retryable: true));
        }
        var text = string.Join("\n", result.Content.Select(c => c is TextContentBlock t ? t.Text : $"[{c.Type}]"));
        if (result.IsError == true) return (null, null, new ToolException("UPSTREAM", text));
        JsonNode? output = result.StructuredContent is { } sc ? JsonNode.Parse(sc.GetRawText()) : JsonValue.Create(text);
        log.LogDebug("mcp plugin {Plugin} {Tool} ok", m.Name, tool);
        return (output, text, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
