using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExecMcp.Host;

/// <summary>
/// Single entry point for every tool call (MCP, sandbox, subagent): schema validation,
/// confirmation policy, retry, redaction, spill-to-disk and untrusted-content wrapping.
/// </summary>
public sealed class ToolRegistry(HostEnv env, ILogger logger)
{
    readonly object _lock = new();
    readonly List<HostTool> _tools = [];
    readonly HashSet<string> _activated = [];
    readonly Dictionary<string, JsonSchema> _schemas = [];
    readonly Dictionary<string, string> _disabled = [];   // tool-name prefix -> reason
    readonly List<McpServer> _servers = [];   // one per MCP session (HTTP mode can have several)

    public HostEnv Env => env;
    public McpServer? Server { get { lock (_servers) return _servers.LastOrDefault(); } }

    public void AttachServer(McpServer server)
    {
        // Stateless HTTP requests get a throwaway server each time; only real sessions can receive notifications.
        if (server.SessionId is null && _servers.Count > 0) return;
        lock (_servers)
        {
            if (_servers.Contains(server)) return;
            _servers.Add(server);
            if (_servers.Count > 32) _servers.RemoveAt(0);
        }
    }

    public void Register(HostTool tool)
    {
        lock (_lock)
        {
            _tools.RemoveAll(t => t.Name == tool.Name);
            _tools.Add(tool);
            _schemas.Remove(tool.Name);
        }
    }

    /// <summary>Replaces all tools of a plugin (hot reload, crash disable). Order stays stable: core first, then plugins by name.</summary>
    public void ReplacePluginTools(string plugin, IEnumerable<HostTool> tools)
    {
        lock (_lock)
        {
            _tools.RemoveAll(t => t.Plugin == plugin);
            _tools.AddRange(tools);
            // Stable order keeps the prompt cache valid: core tools as registered, then plugin tools by name.
            var ordered = _tools.Where(t => t.Plugin is null)
                .Concat(_tools.Where(t => t.Plugin is not null).OrderBy(t => t.Name, StringComparer.Ordinal)).ToList();
            _tools.Clear();
            _tools.AddRange(ordered);
            _schemas.Clear();
        }
        _ = NotifyListChangedAsync();
    }

    /// <summary>Remembers why a plugin's tools disappeared, so a late call gets a useful error.</summary>
    public void MarkDisabled(string toolPrefix, string reason)
    {
        lock (_lock) _disabled[toolPrefix] = reason;
    }

    public HostTool? Find(string name)
    {
        lock (_lock) return _tools.FirstOrDefault(t => t.Name == name);
    }

    public IReadOnlyList<HostTool> All()
    {
        lock (_lock) return _tools.ToList();
    }

    /// <summary>What tools/list returns: eager tools plus deferred ones loaded via ToolSearch.</summary>
    public IReadOnlyList<HostTool> Listed()
    {
        lock (_lock)
            return _tools.Where(t => (t.Exposure == Exposure.Eager || (t.Exposure == Exposure.Deferred && _activated.Contains(t.Name)))
                                     && t.Visible?.Invoke() != false).ToList();
    }

    public IReadOnlyList<HostTool> Deferred()
    {
        lock (_lock) return _tools.Where(t => t.Exposure == Exposure.Deferred).ToList();
    }

    public bool Activate(IEnumerable<string> names)
    {
        bool changed = false;
        lock (_lock)
            foreach (var n in names)
                if (_tools.Any(t => t.Name == n && t.Exposure == Exposure.Deferred))
                    changed |= _activated.Add(n);
        if (changed) _ = NotifyListChangedAsync();
        return changed;
    }

    async Task NotifyListChangedAsync()
    {
        List<McpServer> servers;
        lock (_servers) servers = _servers.ToList();
        foreach (var s in servers)
        {
            try { await s.SendNotificationAsync(NotificationMethods.ToolListChangedNotification); }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "list_changed notification failed; dropping session");
                lock (_servers) _servers.Remove(s);
            }
        }
    }

    public async Task<ToolOutcome> InvokeAsync(string name, JsonElement args, CallOrigin origin, McpServer? server,
        ProgressToken? progress, CancellationToken ct)
    {
        if (server is not null) AttachServer(server);
        var tool = Find(name);
        if (tool is null)
        {
            string? why;
            lock (_lock) why = _disabled.FirstOrDefault(d => name.StartsWith(d.Key, StringComparison.Ordinal)).Value;
            if (why is not null) return ToolOutcome.Error(why, "UPSTREAM");
            var near = OutputUtil.Closest(name, All().Select(t => t.Name)).ToList();
            return ToolOutcome.Error($"Unknown tool '{name}'." + (near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : ""), "NOT_FOUND");
        }
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) args = JsonDocument.Parse("{}").RootElement;

        var invalid = Validate(tool, args);
        if (invalid is not null) return ToolOutcome.Error(invalid, "INVALID_INPUT");

        var call = new ToolCall { Args = args, Origin = origin, Server = server ?? Server, ProgressToken = progress, Ct = ct };

        if (tool.IsDestructive && NeedsConfirmation(origin))
        {
            var ok = await Confirm.AskAsync(call.Server, $"Allow {tool.Name} ({origin} call)?\n{Summarize(args)}", ct);
            if (ok != true)
                return ToolOutcome.Error(ok is null
                    ? $"{tool.Name} needs user confirmation for {origin.ToString().ToLowerInvariant()} calls, and this client cannot ask. Set \"confirmDestructive\" in ~/.exec-mcp/config.json or call the tool directly."
                    : "User declined.", "PERMISSION_DENIED");
        }

        var outcome = await RunAsync(tool, call);
        if (outcome.IsError && outcome.Retryable && (tool.IsReadOnly || tool.IsIdempotent) && !ct.IsCancellationRequested)
        {
            await Task.Delay(Math.Clamp(outcome.RetryAfterMs ?? 500, 0, 5000), ct);
            logger.LogInformation("retrying {Tool} after retryable {Code}", tool.Name, outcome.ErrorCode);
            outcome = await RunAsync(tool, call);
        }
        return PostProcess(tool, outcome);
    }

    bool NeedsConfirmation(CallOrigin origin) => env.Config.ConfirmDestructive switch
    {
        "always" => true,
        "never" => false,
        _ => origin != CallOrigin.Mcp,
    };

    async Task<ToolOutcome> RunAsync(HostTool tool, ToolCall call)
    {
        try
        {
            return await tool.Handler(call);
        }
        catch (ToolException ex)
        {
            return ToolOutcome.Error(ex.Message, ex.Code, ex.Retryable, ex.RetryAfterMs);
        }
        catch (OperationCanceledException) when (call.Ct.IsCancellationRequested)
        {
            return ToolOutcome.Error("Cancelled.", "TIMEOUT");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "tool {Tool} failed", tool.Name);
            return ToolOutcome.Error($"Internal error in {tool.Name}: {ex.Message}", "INTERNAL");
        }
    }

    ToolOutcome PostProcess(HostTool tool, ToolOutcome o)
    {
        for (int i = 0; i < o.Content.Count; i++)
        {
            if (o.Content[i] is not TextContentBlock tb) continue;
            var text = Secrets.Redact(tb.Text);
            if (tool.Plugin is not null) text = OutputUtil.Clean(text);
            if (tool.MaxOutputChars > 0 && !o.IsError) text = OutputUtil.Spill(env, text, tool.MaxOutputChars, tool.Name);
            if ((tool.IsOpenWorld || o.Untrusted) && env.Config.UntrustedWrap && !o.IsError && !text.StartsWith("<untrusted "))
                text = $"<untrusted source=\"{tool.Name}\">\n{text}\n</untrusted>";
            o.Content[i] = new TextContentBlock { Text = text };
        }
        if (o.Structured is not null) o.Structured = JsonNode.Parse(Secrets.Redact(o.Structured.ToJsonString()));
        if (o.Value is not null) o.Value = JsonNode.Parse(Secrets.Redact(o.Value.ToJsonString()));
        return o;
    }

    string? Validate(HostTool tool, JsonElement args)
    {
        JsonSchema schema;
        lock (_lock)
        {
            if (!_schemas.TryGetValue(tool.Name, out schema!))
                _schemas[tool.Name] = schema = JsonSchema.FromText(tool.InputSchema.GetRawText());
        }
        var result = schema.Evaluate(args, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return null;
        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Where(e => e.Key != "properties").Select(e => (loc: d.InstanceLocation.ToString().TrimStart('/').Replace('/', '.'), msg: e.Value)))
            .Distinct()
            .Take(5)
            .Select(e => string.IsNullOrEmpty(e.loc) ? e.msg : $"{e.loc}: {e.msg}");
        var msg = string.Join("; ", errors);
        return string.IsNullOrEmpty(msg) ? "Input does not match the schema." : msg;
    }

    static string Summarize(JsonElement args)
    {
        var s = args.GetRawText();
        return s.Length > 600 ? s[..600] + "…" : s;
    }
}

/// <summary>Asks the human through MCP elicitation. Returns null when the client cannot ask.</summary>
public static class Confirm
{
    public static async Task<bool?> AskAsync(McpServer? server, string message, CancellationToken ct)
    {
        if (server?.ClientCapabilities?.Elicitation is null) return null;
        try
        {
            var res = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = message,
                RequestedSchema = new()
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        ["approve"] = new ElicitRequestParams.BooleanSchema { Description = "Allow this action", Default = false },
                    },
                    Required = ["approve"],
                },
            }, ct);
            return res.IsAccepted && res.Content is not null && res.Content.TryGetValue("approve", out var v)
                   && v.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return null;
        }
    }
}
