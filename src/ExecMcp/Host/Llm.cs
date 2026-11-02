using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// MCP sampling is deprecated by the 2026-07-28 revision (SEP-2577) but still the only way to reach the
// client's model for clients on earlier revisions; it is used only when the client advertises it.
#pragma warning disable MCP9005

namespace ExecMcp.Host;

/// <summary>
/// LLM access for Task (subagents) and plugin ctx.llm. Providers: Anthropic API (ANTHROPIC_API_KEY),
/// the `claude` CLI, or MCP sampling through the connected client (completions only).
/// </summary>
public sealed class Llm(HostEnv env, ILogger logger)
{
    const string FallbackBeta = "server-side-fallback-2026-07-01";

    static bool HasApiKey =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));

    static readonly Lazy<string?> ClaudeCli = new(() =>
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"])
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    });

    string Provider => env.Config.Llm.Provider.ToLowerInvariant();

    string Model(string tier) => tier == "small" ? env.Config.Llm.SmallModel : env.Config.Llm.MainModel;

    public async Task<string> CompleteAsync(string prompt, string tier, int maxTokens, McpServer? server, CancellationToken ct)
    {
        var p = Provider;
        if (p == "none") throw new ToolException("PERMISSION_DENIED", "LLM access is disabled (llm.provider = none).");
        if ((p is "auto" or "sampling") && server?.ClientCapabilities?.Sampling is not null)
            return await SampleAsync(server, prompt, tier, maxTokens, ct);
        if ((p is "auto" && HasApiKey) || p == "anthropic")
            return (await RunApiAsync(prompt, null, [], tier, maxTokens, null, null, ct)).Text;
        if ((p is "auto" or "claude-cli") && ClaudeCli.Value is not null)
            return await RunCliAsync(prompt, tier, allowedTools: "", ct);
        throw new ToolException("UPSTREAM", "No LLM provider: the client does not support sampling, ANTHROPIC_API_KEY is not set and the `claude` CLI is not installed.");
    }

    /// <summary>Runs a subagent with a fresh context over the given host tools and returns its final report.</summary>
    public async Task<string> RunAgentAsync(string prompt, string agentType, IReadOnlyList<HostTool> tools, ToolRegistry registry,
        McpServer? server, ProgressToken? progress, CancellationToken ct)
    {
        var p = Provider;
        var tier = agentType == "explore" ? "small" : "main";
        var system =
            $"You are a {(agentType == "explore" ? "read-only code exploration" : "general-purpose")} subagent working in {env.Workspace}. " +
            "Complete the task with the tools, then reply with a concise final report: findings, file paths with line numbers, and anything left undone. " +
            "Your final message is the only thing the caller sees.";
        if ((p is "auto" && HasApiKey) || p == "anthropic")
        {
            var (text, turns) = await RunApiAsync(prompt, system, tools, tier, 16000, registry, (server, progress), ct);
            logger.LogInformation("subagent finished in {Turns} turns", turns);
            return text;
        }
        if ((p is "auto" or "claude-cli") && ClaudeCli.Value is not null)
        {
            var allowed = agentType == "explore" ? "Read,Grep,Glob" : "Read,Grep,Glob,Edit,MultiEdit,Write,Bash";
            return await RunCliAsync(system + "\n\nTask:\n" + prompt, tier, allowed, ct);
        }
        throw new ToolException("UPSTREAM", "Task needs ANTHROPIC_API_KEY or the `claude` CLI on PATH (llm.provider in ~/.exec-mcp/config.json).");
    }

    async Task<(string Text, int Turns)> RunApiAsync(string prompt, string? system, IReadOnlyList<HostTool> tools, string tier, int maxTokens,
        ToolRegistry? registry, (McpServer? Server, ProgressToken? Progress)? call, CancellationToken ct)
    {
        AnthropicClient client = new();
        var model = Model(tier);
        List<BetaToolUnion> toolDefs = tools.Select(t => (BetaToolUnion)new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = ToInputSchema(t.InputSchema),
        }).ToList();

        List<BetaMessageParam> messages = [new BetaUserMessageParam { Content = prompt }];
        for (int turn = 1; turn <= env.Config.Llm.MaxTurns; turn++)
        {
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = maxTokens,
                Messages = messages,
                Betas = [FallbackBeta],
                Fallbacks = new Default(),
                OutputConfig = new BetaOutputConfig { Effort = tier == "small" ? Effort.Low : Effort.High },
            };
            if (system is not null) parameters = parameters with { System = system };
            if (toolDefs.Count > 0) parameters = parameters with { Tools = toolDefs };

            BetaMessage response = await client.Beta.Messages.Create(parameters, ct);
            if (response.StopReason == "refusal")
                return ("The model declined this task (refusal).", turn);

            List<BetaContentBlockParam> assistant = [];
            var uses = new List<BetaToolUseBlock>();
            var text = new StringBuilder();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var t)) { assistant.Add(new BetaTextBlockParam { Text = t.Text }); text.Append(t.Text); }
                else if (block.TryPickThinking(out var th)) assistant.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rt)) assistant.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickToolUse(out var tu)) { assistant.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input }); uses.Add(tu); }
                else if (block.TryPickFallback(out var fb)) assistant.Add(new BetaFallbackBlockParam { From = new BetaFallbackInfoParam { Model = fb.From.Model }, To = new BetaFallbackInfoParam { Model = fb.To.Model } });
            }
            if (uses.Count == 0 || registry is null) return (text.ToString().Trim(), turn);

            messages.Add(new BetaAssistantMessageParam { Content = assistant });
            if (call is { } c) _ = new ToolCall { Args = default, Origin = CallOrigin.Subagent, Server = c.Server, ProgressToken = c.Progress, Ct = ct }
                .ProgressAsync($"subagent turn {turn}: {string.Join(", ", uses.Select(u => u.Name))}", null);

            // Read-only calls run in parallel; anything that writes runs in order.
            var results = new BetaContentBlockParam[uses.Count];
            async Task Exec(int i)
            {
                var args = JsonSerializer.SerializeToElement(uses[i].Input);
                var outcome = await registry.InvokeAsync(uses[i].Name, args, CallOrigin.Subagent, call?.Server, null, ct);
                var body = outcome.AllText();
                if (body.Length == 0 && outcome.Content.Count > 0) body = "[non-text result omitted]";
                results[i] = new BetaToolResultBlockParam { ToolUseID = uses[i].ID, Content = body, IsError = outcome.IsError };
            }
            var readOnly = Enumerable.Range(0, uses.Count).Where(i => registry.Find(uses[i].Name)?.IsReadOnly == true).ToList();
            await Task.WhenAll(readOnly.Select(Exec));
            foreach (var i in Enumerable.Range(0, uses.Count).Except(readOnly)) await Exec(i);

            messages.Add(new BetaUserMessageParam { Content = results.ToList() });
        }
        return ($"Subagent stopped after {env.Config.Llm.MaxTurns} turns without a final report.", env.Config.Llm.MaxTurns);
    }

    static InputSchema ToInputSchema(JsonElement schema)
    {
        var props = new Dictionary<string, JsonElement>();
        if (schema.TryGetProperty("properties", out var p))
            foreach (var prop in p.EnumerateObject()) props[prop.Name] = prop.Value.Clone();
        List<string> required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
        return new InputSchema { Properties = props, Required = required };
    }

    async Task<string> SampleAsync(McpServer server, string prompt, string tier, int maxTokens, CancellationToken ct)
    {
        var result = await server.SampleAsync(new CreateMessageRequestParams
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContentBlock { Text = prompt }] }],
            MaxTokens = maxTokens,
            ModelPreferences = new ModelPreferences
            {
                Hints = [new ModelHint { Name = tier == "small" ? "haiku" : "sonnet" }],
                SpeedPriority = tier == "small" ? 0.8f : 0.3f,
                IntelligencePriority = tier == "small" ? 0.3f : 0.8f,
                CostPriority = tier == "small" ? 0.8f : 0.3f,
            },
        }, ct);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    async Task<string> RunCliAsync(string prompt, string tier, string allowedTools, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ClaudeCli.Value!)
        {
            WorkingDirectory = env.Workspace,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-p", "--output-format", "text", "--model", Model(tier) }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--allowedTools");
        psi.ArgumentList.Add(allowedTools);
        if (allowedTools.Length == 0) { psi.ArgumentList.Add("--tools"); psi.ArgumentList.Add(""); }

        using var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteAsync(prompt);
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
        if (proc.ExitCode != 0) throw new ToolException("UPSTREAM", $"claude CLI failed ({proc.ExitCode}): {(await stderr).Trim()}", retryable: true);
        return (await stdout).Trim();
    }
}
