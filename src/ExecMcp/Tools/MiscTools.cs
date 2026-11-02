using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExecMcp.Host;

namespace ExecMcp.Tools;

public static class MiscTools
{
    public static HostTool TodoWrite()
    {
        var todos = new List<(string Content, string Status)>();
        return new HostTool
        {
            Name = "TodoWrite",
            Description = "Replace the task list. Use for tasks with 3+ steps. Exactly one item in_progress at a time; mark completed immediately after finishing.",
            InputSchema = Schema.Parse("""
            {
              "type": "object",
              "properties": {
                "todos": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "content": { "type": "string" },
                      "status":  { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                    },
                    "required": ["content", "status"]
                  }
                }
              },
              "required": ["todos"]
            }
            """),
            Annotations = new() { ReadOnlyHint = true },
            Handler = call =>
            {
                var items = call.Get("todos")!.Value.EnumerateArray()
                    .Select(t => (t.GetProperty("content").GetString()!, t.GetProperty("status").GetString()!)).ToList();
                int inProgress = items.Count(t => t.Item2 == "in_progress");
                if (inProgress > 1)
                    return Task.FromResult(ToolOutcome.Error($"{inProgress} items are in_progress; keep exactly one in_progress at a time."));
                lock (todos) { todos.Clear(); todos.AddRange(items); }
                var sb = new StringBuilder($"Todos updated ({items.Count(t => t.Item2 == "completed")}/{items.Count} done):\n");
                foreach (var (c, s) in items) sb.Append(s switch { "completed" => "[x] ", "in_progress" => "[>] ", _ => "[ ] " }).Append(c).Append('\n');
                var outcome = ToolOutcome.Text(sb.ToString().TrimEnd());
                outcome.Structured = new JsonObject
                {
                    ["todos"] = new JsonArray(items.Select(t => (JsonNode)new JsonObject { ["content"] = t.Item1, ["status"] = t.Item2 }).ToArray()),
                };
                return Task.FromResult(outcome);
            },
        };
    }

    public static HostTool ToolSearch(ToolRegistry registry) => new()
    {
        Name = "ToolSearch",
        Description = "Load full schemas of deferred tools (listed by name only in the system prompt). Query `select:A,B` or keywords.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "query":       { "type": "string" },
            "max_results": { "type": "integer", "default": 5 }
          },
          "required": ["query"]
        }
        """),
        Annotations = new() { ReadOnlyHint = true },
        Handler = call =>
        {
            var query = call.Req("query").Trim();
            var max = Math.Clamp(call.Int("max_results") ?? 5, 1, 50);
            var deferred = registry.Deferred();
            List<HostTool> found;
            if (query.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
            {
                var names = query[7..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                found = deferred.Where(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                var missing = names.Where(n => !found.Any(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (found.Count == 0)
                    return Task.FromResult(ToolOutcome.Error($"No deferred tools named {string.Join(", ", missing)}. Available: {string.Join(", ", deferred.Select(t => t.Name))}", "NOT_FOUND"));
            }
            else
            {
                var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                found = deferred
                    .Select(t => (t, score: terms.Sum(term => (t.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
                                                       + (t.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0))))
                    .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.t.Name).Take(max).Select(x => x.t).ToList();
                if (found.Count == 0)
                    return Task.FromResult(ToolOutcome.Text($"No deferred tools match. Available: {string.Join(", ", deferred.Select(t => t.Name))}"));
            }

            registry.Activate(found.Select(t => t.Name));
            var sb = new StringBuilder("Loaded; these tools can now be called directly:\n");
            foreach (var t in found)
            {
                var def = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["inputSchema"] = JsonNode.Parse(t.InputSchema.GetRawText()),
                };
                sb.Append(def.ToJsonString(new JsonSerializerOptions { WriteIndented = false })).Append('\n');
            }
            return Task.FromResult(ToolOutcome.Text(sb.ToString().TrimEnd()));
        },
    };
}
