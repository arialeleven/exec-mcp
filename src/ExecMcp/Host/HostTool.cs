using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExecMcp.Host;

public enum Exposure { Eager, Deferred, Hidden }

/// <summary>Who triggered a call. Only direct MCP calls were approved by the client UI.</summary>
public enum CallOrigin { Mcp, Sandbox, Subagent }

/// <summary>A tool as the host sees it: core tools and plugin tools share this shape.</summary>
public sealed record HostTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement InputSchema { get; init; }
    public JsonElement? OutputSchema { get; init; }
    public ToolAnnotations Annotations { get; init; } = new();
    public Exposure Exposure { get; init; } = Exposure.Eager;
    /// <summary>Plugin name, or null for core tools.</summary>
    public string? Plugin { get; init; }
    /// <summary>Sandbox namespace and function name (db.query). Null for tools not projected into the sandbox.</summary>
    public string? Namespace { get; init; }
    public string? SandboxName { get; init; }
    /// <summary>Text above this size is spilled to an artifact. 0 disables the generic spill (tool truncates itself).</summary>
    public int MaxOutputChars { get; init; }
    /// <summary>Latency hint shown in sandbox typings (e.g. "~200ms").</summary>
    public string? CostHint { get; init; }
    public required Func<ToolCall, Task<ToolOutcome>> Handler { get; init; }
    /// <summary>When set and false, the tool is left out of tools/list (still callable).</summary>
    public Func<bool>? Visible { get; init; }

    public bool IsReadOnly => Annotations.ReadOnlyHint == true;
    public bool IsDestructive => Annotations.DestructiveHint == true;
    public bool IsIdempotent => Annotations.IdempotentHint == true;
    public bool IsOpenWorld => Annotations.OpenWorldHint == true;

    public string OneLine
    {
        get
        {
            var d = Description.Trim();
            var i = d.IndexOf(". ", StringComparison.Ordinal);
            return i > 0 ? d[..(i + 1)] : d;
        }
    }

    public Tool ToMcp() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema,
        OutputSchema = OutputSchema,
        Annotations = Annotations,
    };
}

public sealed class ToolCall
{
    public required JsonElement Args { get; init; }
    public required CallOrigin Origin { get; init; }
    public McpServer? Server { get; init; }
    public ProgressToken? ProgressToken { get; init; }
    public CancellationToken Ct { get; init; }

    public string? Str(string name) =>
        Args.ValueKind == JsonValueKind.Object && Args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public string Req(string name) => Str(name) ?? throw new ToolException("INVALID_INPUT", $"{name}: required");

    public int? Int(string name) =>
        Args.ValueKind == JsonValueKind.Object && Args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public bool Bool(string name, bool def = false) =>
        Args.ValueKind == JsonValueKind.Object && Args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    public JsonElement? Get(string name) =>
        Args.ValueKind == JsonValueKind.Object && Args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public async Task ProgressAsync(string? message, double? ratio)
    {
        if (Server is null || ProgressToken is null) return;
        try
        {
            await Server.NotifyProgressAsync(ProgressToken.Value,
                new() { Progress = (float)((ratio ?? 0) * 100), Total = ratio is null ? null : 100, Message = message }, cancellationToken: Ct);
        }
        catch { /* progress is best-effort */ }
    }
}

public sealed class ToolOutcome
{
    public List<ContentBlock> Content { get; init; } = [];
    public JsonNode? Structured { get; set; }
    /// <summary>Raw tool output for the sandbox (may be any JSON value; Structured must be an object).</summary>
    public JsonNode? Value { get; set; }
    public bool IsError { get; set; }
    /// <summary>This particular result carries third-party text (e.g. a file outside the workspace).</summary>
    public bool Untrusted { get; set; }
    public string? ErrorCode { get; set; }
    public bool Retryable { get; set; }
    public int? RetryAfterMs { get; set; }

    public static ToolOutcome Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    public static ToolOutcome Error(string message, string code = "INVALID_INPUT", bool retryable = false, int? retryAfterMs = null) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
        ErrorCode = code,
        Retryable = retryable,
        RetryAfterMs = retryAfterMs,
    };

    public string AllText() => string.Join("\n", Content.OfType<TextContentBlock>().Select(t => t.Text));

    public CallToolResult ToMcp() => new()
    {
        Content = Content,
        StructuredContent = Structured is null ? null : JsonSerializer.SerializeToElement(Structured),
        IsError = IsError ? true : null,
    };
}

/// <summary>Thrown by handlers; the registry turns it into an isError result with a hint.</summary>
public sealed class ToolException(string code, string message, bool retryable = false, int? retryAfterMs = null) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int? RetryAfterMs { get; } = retryAfterMs;
}
