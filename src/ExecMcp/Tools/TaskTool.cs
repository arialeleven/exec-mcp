using ExecMcp.Host;

namespace ExecMcp.Tools;

public static class TaskTool
{
    static readonly string[] ExploreTools = ["Read", "Grep", "Glob"];
    static readonly string[] GeneralTools = ["Read", "Grep", "Glob", "Edit", "MultiEdit", "Write", "Bash", "BashOutput", "TodoWrite"];

    public static HostTool Create(ToolRegistry registry, Llm llm) => new()
    {
        Name = "Task",
        Description = "Launch a subagent with a fresh context for a self-contained task (broad code search, research). It returns only its final report. Write a complete, standalone prompt.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "description": { "type": "string", "description": "3-5 words" },
            "prompt":      { "type": "string" },
            "agent_type":  { "type": "string", "enum": ["explore", "general"], "default": "general" }
          },
          "required": ["description", "prompt"]
        }
        """),
        Annotations = new(),
        Handler = async call =>
        {
            var type = call.Str("agent_type") ?? "general";
            var names = type == "explore" ? ExploreTools : GeneralTools;
            var tools = names.Select(registry.Find).OfType<HostTool>().ToList();
            await call.ProgressAsync($"subagent ({type}): {call.Str("description")}", null);
            var report = await llm.RunAgentAsync(call.Req("prompt"), type, tools, registry, call.Server, call.ProgressToken, call.Ct);
            var outcome = ToolOutcome.Text(report.Length == 0 ? "(subagent returned no report)" : report);
            outcome.Untrusted = true;  // the report is built from files and command output the subagent read
            return outcome;
        },
    };
}
