using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExecMcp.Host;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace ExecMcp.Sandbox;

/// <summary>
/// Code mode: JavaScript in a Jint sandbox (no CLR, fs or network access of its own). Plugin tools are
/// exposed as namespaces (db.query) and go through the same registry: permissions, limits, confirmations.
/// Only what the script prints reaches the model.
/// </summary>
public static class ExecSandbox
{
    public static HostTool Create(HostEnv env, ToolRegistry registry) => new()
    {
        Name = "exec",
        Description = "Run JavaScript in a sandbox and return what it prints (console.log). Tools are namespaces of async functions (core.Read, core.Bash, plugin namespaces); help(\"ns\") prints their TypeScript signatures. Use it to chain many tool calls and print only the result.",
        InputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "code":       { "type": "string", "description": "JavaScript (top-level await allowed)" },
            "timeout_ms": { "type": "integer", "minimum": 100, "maximum": 600000 }
          },
          "required": ["code"]
        }
        """).RootElement.Clone(),
        Annotations = new() { DestructiveHint = true, OpenWorldHint = false },
        Handler = call => Task.Run(() => Run(env, registry, call), call.Ct),
    };

    static ToolOutcome Run(HostEnv env, ToolRegistry registry, ToolCall call)
    {
        var cfg = env.Config.Sandbox;
        var timeout = TimeSpan.FromMilliseconds(call.Int("timeout_ms") ?? cfg.TimeoutMs);
        var output = new StringBuilder();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(call.Ct);
        cts.CancelAfter(timeout);

        var engine = new Engine(o => o
            .LimitMemory(cfg.MemoryMb * 1024L * 1024L)
            .LimitRecursion(256)
            .CancellationToken(cts.Token)
            .Strict(false));

        JsValue Print(JsValue _, JsValue[] args)
        {
            lock (output)
            {
                output.Append(string.Join(" ", args.Select(a => Show(engine, a)))).Append('\n');
                if (output.Length > cfg.MaxOutputChars * 4) throw new JavaScriptException("output limit exceeded");
            }
            return JsValue.Undefined;
        }

        engine.SetValue("print", new Jint.Runtime.Interop.ClrFunction(engine, "print", Print));
        engine.Execute("var console = { log: (...a) => print(...a), info: (...a) => print(...a), warn: (...a) => print(...a), error: (...a) => print(...a) };");

        var tools = registry.All().Where(t => t.Namespace is not null && t.SandboxName is not null).ToList();
        engine.SetValue("help", new Jint.Runtime.Interop.ClrFunction(engine, "help", (_, a) =>
            new JsString(Dts.ForNamespace(tools, a.Length == 0 || a[0].IsUndefined() ? null : a[0].ToString()))));

        bool calledTools = false;
        bool openWorld = false;
        // The bridge to the host is never a global: it is handed to a setup closure that builds the namespaces.
        var bridge = new Jint.Runtime.Interop.ClrFunction(engine, "call", (_, a) =>
        {
            var name = a.Length > 0 ? a[0].ToString() : "";
            var json = a.Length > 1 && a[1].IsString() ? a[1].AsString() : "{}";
            ToolOutcome outcome;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var tool = registry.Find(name);
                calledTools = true;
                openWorld |= tool?.IsOpenWorld == true;
                outcome = registry.InvokeAsync(name, doc.RootElement.Clone(), CallOrigin.Sandbox, call.Server, null, cts.Token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not JavaScriptException)
            {
                // Host-side failures become ordinary JS errors, so try/catch in the script works.
                throw new JavaScriptException(engine.Intrinsics.Error.Construct($"[INTERNAL] {name}: {ex.Message}"));
            }
            if (outcome.IsError)
                throw new JavaScriptException(engine.Intrinsics.Error.Construct($"[{outcome.ErrorCode}] {outcome.AllText()}"));
            var value = outcome.Value ?? outcome.Structured ?? JsonValue.Create(outcome.AllText());
            return new Jint.Native.Json.JsonParser(engine).Parse(value?.ToJsonString() ?? "null");
        });

        var setup = new StringBuilder("(function (call) {\n");
        foreach (var ns in tools.GroupBy(t => t.Namespace!))
        {
            setup.Append($"  globalThis.{ns.Key} = Object.freeze({{");
            foreach (var t in ns)
                setup.Append($"{JsonSerializer.Serialize(t.SandboxName)}: async (i) => call({JsonSerializer.Serialize(t.Name)}, JSON.stringify(i ?? {{}})),");
            setup.Append("});\n");
        }
        setup.Append("})");
        engine.Invoke(engine.Evaluate(setup.ToString()), bridge);

        try
        {
            // Wrap in an async function so top-level await works; surface the returned value too.
            var wrapped = $"(async () => {{{call.Req("code")}\n}})()";
            var result = engine.Evaluate(wrapped).UnwrapIfPromise();
            if (!result.IsUndefined()) Print(JsValue.Undefined, [result]);
        }
        catch (OperationCanceledException)
        {
            return Finish(env, output, $"Script stopped: timeout after {timeout.TotalMilliseconds:0} ms", "TIMEOUT");
        }
        catch (ExecutionCanceledException)
        {
            return Finish(env, output, $"Script stopped: timeout after {timeout.TotalMilliseconds:0} ms", "TIMEOUT");
        }
        catch (JavaScriptException ex)
        {
            return Finish(env, output, $"Uncaught {ex.Error}{(ex.Location.Start.Line > 0 && !ex.Message.Contains("<anonymous>") ? $" (line {ex.Location.Start.Line})" : "")}", "INVALID_INPUT");
        }
        catch (PromiseRejectedException ex)
        {
            return Finish(env, output, $"Uncaught (in promise) {Show(engine, ex.RejectedValue)}", "INVALID_INPUT");
        }
        catch (MemoryLimitExceededException)
        {
            return Finish(env, output, $"Script stopped: memory limit {cfg.MemoryMb} MB exceeded", "INVALID_INPUT");
        }
        catch (RecursionDepthOverflowException)
        {
            return Finish(env, output, "Script stopped: recursion too deep", "INVALID_INPUT");
        }
        catch (Exception ex)
        {
            // Never lose what the script already printed.
            return Finish(env, output, $"Script failed: {ex.GetType().Name}: {ex.Message}", "INTERNAL");
        }
        return Finish(env, output, null, null, calledTools && openWorld);
    }

    static ToolOutcome Finish(HostEnv env, StringBuilder output, string? error, string? code, bool untrusted = false)
    {
        string text;
        lock (output) text = output.ToString().TrimEnd();
        text = OutputUtil.Spill(env, text, env.Config.Sandbox.MaxOutputChars, "exec");
        if (error is null)
        {
            text = text.Length == 0 ? "(no output — use console.log)" : text;
            // Output built from open-world tools (shell, network plugins) carries third-party text.
            if (untrusted && env.Config.UntrustedWrap) text = $"<untrusted source=\"exec\">\n{text}\n</untrusted>";
            return ToolOutcome.Text(text);
        }
        return ToolOutcome.Error((text.Length > 0 ? text + "\n" : "") + error, code!);
    }

    static string Show(Engine engine, JsValue v)
    {
        if (v.IsString()) return v.AsString();
        if (v.IsUndefined()) return "undefined";
        if (v is Jint.Native.Error.ErrorInstance err) return err.ToString();
        try
        {
            var json = new Jint.Native.Json.JsonSerializer(engine).Serialize(v, JsValue.Undefined, JsValue.Undefined);
            return json.IsUndefined() ? v.ToString() : json.AsString();
        }
        catch { return v.ToString(); }
    }
}

/// <summary>Generates TypeScript declarations for sandbox namespaces from JSON Schemas.</summary>
public static class Dts
{
    public static string ForNamespace(IEnumerable<HostTool> tools, string? ns)
    {
        var groups = tools.Where(t => ns is null || t.Namespace == ns).GroupBy(t => t.Namespace!).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (groups.Count == 0)
            return ns is null ? "No sandbox namespaces are installed." : $"Unknown namespace '{ns}'. Available: {string.Join(", ", tools.Select(t => t.Namespace).Distinct())}";
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            sb.Append($"declare namespace {g.Key} {{\n");
            foreach (var t in g)
            {
                sb.Append($"  /** {t.Description.Replace("*/", "*\\/")}{(t.CostHint is null ? "" : " " + t.CostHint)} */\n");
                var input = TypeOf(JsonNode.Parse(t.InputSchema.GetRawText()), "  ");
                var output = t.OutputSchema is { } os ? TypeOf(JsonNode.Parse(os.GetRawText()), "  ") : "unknown";
                sb.Append($"  function {t.SandboxName}(i: {input}):\n    Promise<{output}>;\n");
            }
            sb.Append("}\n");
        }
        return sb.ToString().TrimEnd();
    }

    public static string TypeOf(JsonNode? schema, string indent = "")
    {
        if (schema is not JsonObject s) return "unknown";
        if (s["enum"] is JsonArray e) return string.Join(" | ", e.Select(x => x?.ToJsonString() ?? "null"));
        if (s["const"] is { } c) return c.ToJsonString();
        if (s["anyOf"] is JsonArray any) return string.Join(" | ", any.Select(x => TypeOf(x, indent)));
        if (s["oneOf"] is JsonArray one) return string.Join(" | ", one.Select(x => TypeOf(x, indent)));
        var type = s["type"];
        if (type is JsonArray types)
            return string.Join(" | ", types.Select(t => TypeOf(new JsonObject { ["type"] = t?.DeepClone(), ["items"] = s["items"]?.DeepClone(), ["properties"] = s["properties"]?.DeepClone(), ["required"] = s["required"]?.DeepClone() }, indent)));
        switch (type?.GetValue<string>())
        {
            case "string": return "string";
            case "integer" or "number": return "number";
            case "boolean": return "boolean";
            case "null": return "null";
            case "array":
                var item = TypeOf(s["items"], indent);
                return item.Contains(' ') && !item.StartsWith('{') ? $"({item})[]" : $"{item}[]";
            case "object" or null when s["properties"] is JsonObject props:
                var required = (s["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet() ?? [];
                var parts = props.Select(kv => $"{kv.Key}{(required.Contains(kv.Key) ? "" : "?")}: {TypeOf(kv.Value, indent)}");
                var body = string.Join("; ", parts);
                return body.Length == 0 ? "{}" : "{ " + body + " }";
            case "object":
                return "Record<string, unknown>";
            default:
                return "unknown";
        }
    }
}
