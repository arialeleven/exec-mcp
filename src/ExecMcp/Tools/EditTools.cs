using System.Text;
using System.Text.RegularExpressions;
using ExecMcp.Host;

namespace ExecMcp.Tools;

/// <summary>str_replace with uniqueness checks and a fuzzy fallback that reports what it normalized.</summary>
public static partial class EditEngine
{
    public sealed record Result(string Text, int FirstLine, int LastLine, int Count, string? Note);

    [GeneratedRegex(@"^\s*\d+\t", RegexOptions.Multiline)]
    private static partial Regex LinePrefix();

    public static Result Apply(string text, string oldS, string newS, bool replaceAll)
    {
        oldS = oldS.Replace("\r\n", "\n");
        newS = newS.Replace("\r\n", "\n");
        if (oldS == newS) throw new ToolException("INVALID_INPUT", "new_string must differ from old_string.");
        if (oldS.Length == 0)
        {
            if (text.Length == 0) return new Result(newS, 1, Math.Max(1, newS.Count(c => c == '\n')), 1, null);
            throw new ToolException("INVALID_INPUT", "old_string is empty. Provide the exact text to replace (or use Write for a new file).");
        }

        // 1. Exact.
        var ranges = FindExact(text, oldS);
        string? note = null;

        // 2. Line-number prefixes copied from Read output.
        if (ranges.Count == 0 && HasPrefixOnEveryLine(oldS))
        {
            var stripped = LinePrefix().Replace(oldS, "");
            ranges = FindExact(text, stripped);
            if (ranges.Count > 0)
            {
                note = "stripped line-number prefixes from old_string";
                if (HasPrefixOnEveryLine(newS)) newS = LinePrefix().Replace(newS, "");
                oldS = stripped;
            }
        }

        // 3. Whitespace / quote normalization (also on the prefix-stripped variant).
        if (ranges.Count == 0)
        {
            var notes = new List<string>();
            var needle = oldS;
            ranges = FindNormalized(text, needle);
            if (ranges.Count == 0 && HasPrefixOnEveryLine(oldS))
            {
                needle = LinePrefix().Replace(oldS, "");
                ranges = FindNormalized(text, needle);
                if (ranges.Count > 0)
                {
                    notes.Add("stripped line-number prefixes");
                    if (HasPrefixOnEveryLine(newS)) newS = LinePrefix().Replace(newS, "");
                }
            }
            if (ranges.Count > 0)
            {
                // Report what actually differed, and keep the file's typographic quotes.
                var region = text.Substring(ranges[0].Start, ranges[0].Length);
                if (FoldQuotes(region) != region && FoldQuotes(needle) == needle)
                {
                    notes.Add("matched curly quotes in the file; kept them in new_string");
                    newS = CurlQuotes(newS, region);
                }
                if (FoldQuotes(region) != FoldQuotes(needle))
                    notes.Add("matched despite whitespace differences (tabs/spaces or trailing spaces); check the indentation of the result");
                note = string.Join("; ", notes);
            }
        }

        if (ranges.Count == 0)
            throw new ToolException("NOT_FOUND", $"old_string not found in file.{NearestHint(text, oldS)} Read the file again and copy the text exactly (without the line-number prefix).");
        if (ranges.Count > 1 && !replaceAll)
            throw new ToolException("INVALID_INPUT", $"found {ranges.Count} matches (lines {string.Join(", ", ranges.Take(5).Select(r => LineOf(text, r.Start)))}), add context or use replace_all");

        var sb = new StringBuilder(text);
        for (int i = ranges.Count - 1; i >= 0; i--)
            sb.Remove(ranges[i].Start, ranges[i].Length).Insert(ranges[i].Start, newS);
        var result = sb.ToString();

        // Line span of the first replacement in the new text.
        int first = LineOf(text, ranges[0].Start);
        int last = first + Math.Max(0, newS.Count(c => c == '\n') - (newS.EndsWith('\n') ? 1 : 0));
        return new Result(result, first, last, ranges.Count, note);
    }

    static bool HasPrefixOnEveryLine(string s)
    {
        var lines = s.Split('\n').Where(l => l.Length > 0).ToList();
        return lines.Count > 0 && lines.All(l => LinePrefix().IsMatch(l));
    }

    static List<(int Start, int Length)> FindExact(string text, string needle)
    {
        var list = new List<(int, int)>();
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            list.Add((i, needle.Length));
        return list;
    }

    /// <summary>Normalizes text while remembering, for each output char, the original span it came from.</summary>
    static (string Norm, int[] Starts, int[] Ends) Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var starts = new List<int>(s.Length);
        var ends = new List<int>(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c is ' ' or '\t' or ' ')
            {
                int j = i;
                while (j < s.Length && s[j] is ' ' or '\t' or ' ') j++;
                if (j < s.Length && s[j] != '\n') { sb.Append(' '); starts.Add(i); ends.Add(j); }
                i = j;  // trailing whitespace before \n / end is dropped
                continue;
            }
            sb.Append(c switch
            {
                '‘' or '’' or '‚' or '‛' => '\'',
                '“' or '”' or '„' or '‟' => '"',
                _ => c,
            });
            starts.Add(i);
            ends.Add(i + 1);
            i++;
        }
        return (sb.ToString(), starts.ToArray(), ends.ToArray());
    }

    static List<(int Start, int Length)> FindNormalized(string text, string needle)
    {
        var (nt, starts, ends) = Normalize(text);
        var nn = Normalize(needle).Norm;
        if (nn.Trim().Length == 0) return [];
        var list = new List<(int, int)>();
        for (int i = nt.IndexOf(nn, StringComparison.Ordinal); i >= 0; i = nt.IndexOf(nn, i + nn.Length, StringComparison.Ordinal))
            list.Add((starts[i], ends[i + nn.Length - 1] - starts[i]));
        return list;
    }

    static string FoldQuotes(string s) => s
        .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201A', '\'').Replace('\u201B', '\'')
        .Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u201E', '"').Replace('\u201F', '"');

    /// <summary>Turns straight quotes into the curly style the matched region uses (opening after space/bracket, closing otherwise).</summary>
    static string CurlQuotes(string s, string region)
    {
        bool doubles = region.IndexOfAny(['\u201C', '\u201D']) >= 0;
        bool singles = region.IndexOfAny(['\u2018', '\u2019']) >= 0;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool opening = i == 0 || char.IsWhiteSpace(s[i - 1]) || "([{<«—–".Contains(s[i - 1]);
            if (c == '"' && doubles) sb.Append(opening ? '\u201C' : '\u201D');
            else if (c == '\'' && singles) sb.Append(opening ? '\u2018' : '\u2019');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string NearestHint(string text, string oldS)
    {
        var firstLine = oldS.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 3);
        if (firstLine is null) return "";
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(firstLine, StringComparison.Ordinal))
                return $" The first line of old_string appears at line {i + 1}, but the following lines differ.";
        return "";
    }

    public static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>Short confirmation with a couple of lines of context, not the whole file.</summary>
    public static string Snippet(string text, int first, int last, int context = 2)
    {
        var lines = text.Split('\n');
        int from = Math.Max(1, first - context), to = Math.Min(lines.Length, last + context);
        var sb = new StringBuilder();
        void Add(int n) => sb.Append(n).Append('\t').Append(lines[n - 1].Length > 300 ? lines[n - 1][..300] + "…" : lines[n - 1]).Append('\n');
        if (to - from > 24)
        {
            for (int n = from; n < from + 10; n++) Add(n);
            sb.Append($"… {to - from - 15} lines …\n");
            for (int n = to - 4; n <= to; n++) Add(n);
        }
        else
            for (int n = from; n <= to; n++) Add(n);
        return sb.ToString().TrimEnd('\n');
    }
}

public static class EditTools
{
    public static HostTool Edit(HostEnv env, FileState state) => new()
    {
        Name = "Edit",
        Description = "Exact string replacement in a file. old_string must match exactly (without the line-number prefix) and be unique unless replace_all is true. Include enough surrounding context to make it unique. The file must be Read first.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "file_path":   { "type": "string" },
            "old_string":  { "type": "string" },
            "new_string":  { "type": "string", "description": "Must differ from old_string" },
            "replace_all": { "type": "boolean", "default": false }
          },
          "required": ["file_path", "old_string", "new_string"]
        }
        """),
        Annotations = new() { DestructiveHint = true, IdempotentHint = false },
        Handler = call => Task.FromResult(Apply(env, state, call.Req("file_path"),
            [(call.Req("old_string"), call.Req("new_string"), call.Bool("replace_all"))])),
    };

    public static HostTool MultiEdit(HostEnv env, FileState state) => new()
    {
        Name = "MultiEdit",
        Description = "Apply several Edit operations to one file atomically, in order. All succeed or none are applied.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string" },
            "edits": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "old_string":  { "type": "string" },
                  "new_string":  { "type": "string" },
                  "replace_all": { "type": "boolean", "default": false }
                },
                "required": ["old_string", "new_string"]
              }
            }
          },
          "required": ["file_path", "edits"]
        }
        """),
        Annotations = new() { DestructiveHint = true },
        Handler = call =>
        {
            var edits = call.Get("edits")!.Value.EnumerateArray().Select(e => (
                e.GetProperty("old_string").GetString()!,
                e.GetProperty("new_string").GetString()!,
                e.TryGetProperty("replace_all", out var ra) && ra.ValueKind == System.Text.Json.JsonValueKind.True)).ToList();
            return Task.FromResult(Apply(env, state, call.Req("file_path"), edits));
        },
    };

    static ToolOutcome Apply(HostEnv env, FileState state, string rawPath, List<(string Old, string New, bool All)> edits)
    {
        var path = env.Resolve(rawPath);
        var display = env.Display(path);
        if (env.IsProtected(path)) return ToolOutcome.Error($"{path} is protected (exec-mcp secrets or tools.protected).", "PERMISSION_DENIED");

        if (!File.Exists(path))
        {
            // Edit with an empty old_string on a missing file creates it (same as Claude Code).
            if (edits.Count == 1 && edits[0].Old.Length == 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = Encoding.UTF8.GetBytes(edits[0].New);
                File.WriteAllBytes(path, bytes);
                state.Record(path, bytes);
                return ToolOutcome.Text($"Created {display}");
            }
            return ToolOutcome.Error(PathHints.NotFound(path, env), "NOT_FOUND");
        }

        var stale = state.CheckFresh(path, env);
        if (stale is not null) return ToolOutcome.Error(stale, "INVALID_INPUT");

        var raw = File.ReadAllBytes(path);
        if (TextFile.LooksBinary(raw)) return ToolOutcome.Error($"{display} is a binary file; Edit works on text only.");
        var file = TextFile.Decode(raw);

        var text = file.Text;
        var notes = new List<string>();
        EditEngine.Result? lastResult = null;
        int total = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            try
            {
                lastResult = EditEngine.Apply(text, edits[i].Old, edits[i].New, edits[i].All);
            }
            catch (ToolException ex) when (edits.Count > 1)
            {
                throw new ToolException(ex.Code, $"Edit #{i + 1} failed: {ex.Message} No edits were applied.");
            }
            text = lastResult.Text;
            total += lastResult.Count;
            if (lastResult.Note is not null) notes.Add(edits.Count > 1 ? $"#{i + 1}: {lastResult.Note}" : lastResult.Note);
        }

        var outBytes = file.Encode(text);
        File.WriteAllBytes(path, outBytes);
        state.Record(path, outBytes);

        var r = lastResult!;
        var head = edits.Count > 1
            ? $"Applied {edits.Count} edits ({total} replacements) to {display}; last at lines {r.FirstLine}–{r.LastLine}:"
            : r.Count > 1
                ? $"Applied {r.Count} replacements to {display}; first at lines {r.FirstLine}–{r.LastLine}:"
                : $"Applied to {display}, lines {r.FirstLine}–{r.LastLine}:";
        var sb = new StringBuilder(head).Append('\n').Append(EditEngine.Snippet(text, r.FirstLine, r.LastLine));
        if (notes.Count > 0) sb.Append("\nNote: ").Append(string.Join("; ", notes));
        return ToolOutcome.Text(sb.ToString());
    }

    public static HostTool Write(HostEnv env, FileState state) => new()
    {
        Name = "Write",
        Description = "Create or fully overwrite a file. Prefer Edit for changes to existing files. Overwriting requires a prior Read.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string" },
            "content":   { "type": "string" }
          },
          "required": ["file_path", "content"]
        }
        """),
        Annotations = new() { DestructiveHint = true },
        Handler = call =>
        {
            var path = env.Resolve(call.Req("file_path"));
            var content = call.Req("content");
            if (env.IsProtected(path)) return Task.FromResult(ToolOutcome.Error($"{path} is protected (exec-mcp secrets or tools.protected).", "PERMISSION_DENIED"));
            if (Directory.Exists(path)) return Task.FromResult(ToolOutcome.Error($"{env.Display(path)} is a directory."));
            bool exists = File.Exists(path);
            byte[] bytes;
            if (exists)
            {
                var stale = state.CheckFresh(path, env);
                if (stale is not null) return Task.FromResult(ToolOutcome.Error(stale));
                var old = File.ReadAllBytes(path);
                // Keep the file's BOM and line-ending style.
                bytes = TextFile.LooksBinary(old) ? Encoding.UTF8.GetBytes(content) : TextFile.Decode(old).Encode(content.Replace("\r\n", "\n"));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                bytes = Encoding.UTF8.GetBytes(content);
            }
            File.WriteAllBytes(path, bytes);
            state.Record(path, bytes);
            int lines = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);
            return Task.FromResult(ToolOutcome.Text($"{(exists ? "Overwrote" : "Created")} {env.Display(path)} ({lines} lines)"));
        },
    };
}
