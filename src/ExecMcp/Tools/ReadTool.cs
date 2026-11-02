using System.Diagnostics;
using System.Text;
using ExecMcp.Host;
using ModelContextProtocol.Protocol;

namespace ExecMcp.Tools;

public static class ReadTool
{
    const int DefaultLimit = 2000, MaxLineChars = 2000, MaxTotalChars = 120_000;
    const long MaxImageBytes = 5 * 1024 * 1024, MaxPdfBytes = 20 * 1024 * 1024;

    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    public static HostTool Create(HostEnv env, FileState state) => new()
    {
        Name = "Read",
        Description = "Read a file. Output lines are prefixed with line numbers as `N\\t`. Use offset/limit for large files. Reads images and PDFs. Read a file before editing it.",
        InputSchema = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Absolute path" },
            "offset":    { "type": "integer", "minimum": 1, "description": "1-based start line" },
            "limit":     { "type": "integer", "minimum": 1, "description": "Max lines (default 2000)" }
          },
          "required": ["file_path"]
        }
        """),
        Annotations = new() { ReadOnlyHint = true },
        Handler = call => Task.FromResult(Run(env, state, call)),
    };

    static ToolOutcome Run(HostEnv env, FileState state, ToolCall call)
    {
        var path = env.Resolve(call.Req("file_path"));
        if (env.IsProtected(path)) return ToolOutcome.Error($"{path} is protected (exec-mcp secrets or tools.protected).", "PERMISSION_DENIED");
        if (Directory.Exists(path))
            return ToolOutcome.Error($"{env.Display(path)} is a directory. Use Glob to list files.", "INVALID_INPUT");
        if (!File.Exists(path)) return ToolOutcome.Error(PathHints.NotFound(path, env), "NOT_FOUND");

        var ext = Path.GetExtension(path);
        var size = new FileInfo(path).Length;

        if (ImageTypes.TryGetValue(ext, out var mime))
        {
            if (size > MaxImageBytes) return ToolOutcome.Error($"Image is {size / 1024} KB, over the {MaxImageBytes / 1024 / 1024} MB limit.");
            var bytes = File.ReadAllBytes(path);
            state.Record(path, bytes);
            return new ToolOutcome { Content = [ImageContentBlock.FromBytes(bytes, mime)] };
        }

        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return ReadPdf(env, state, path, size, call);

        var raw = File.ReadAllBytes(path);
        state.Record(path, raw);
        if (TextFile.LooksBinary(raw))
            return ToolOutcome.Text($"Binary file ({size:N0} bytes), not displayed. Use Bash (e.g. `file`, `xxd | head`) to inspect it.");

        var outcome = ToolOutcome.Text(Number(TextFile.Decode(raw).Text, call.Int("offset") ?? 1, call.Int("limit") ?? DefaultLimit, env.Display(path)));
        // Logs, downloads and system files outside the workspace may contain text written by others.
        outcome.Untrusted = !env.InWorkspace(path);
        return outcome;
    }

    /// <summary>Formats lines as `N\tline`, truncates long lines, and tells where to continue.</summary>
    public static string Number(string text, int offset, int limit, string displayPath)
    {
        if (text.Length == 0) return "(empty file)";
        var lines = text.Split('\n');
        int total = lines.Length;
        if (text.EndsWith('\n')) total--;  // a trailing newline does not start a new line
        if (offset > total) return $"File has only {total} lines; offset {offset} is past the end.";

        var sb = new StringBuilder();
        int last = Math.Min(total, offset - 1 + limit);
        int i = offset - 1;
        for (; i < last; i++)
        {
            var line = lines[i];
            if (line.Length > MaxLineChars) line = line[..MaxLineChars] + $"… [line truncated, {line.Length} chars]";
            if (sb.Length + line.Length > MaxTotalChars && i > offset - 1) break;
            sb.Append(i + 1).Append('\t').Append(line).Append('\n');
        }
        if (i < total)
            sb.Append($"… {total - i} more lines in {displayPath}. Continue with offset={i + 1}.\n");
        return sb.ToString().TrimEnd('\n');
    }

    static ToolOutcome ReadPdf(HostEnv env, FileState state, string path, long size, ToolCall call)
    {
        var bytes = File.ReadAllBytes(path);
        state.Record(path, bytes);
        var text = TryPdfToText(path);
        if (text is not null)
            return ToolOutcome.Text(Number(text.Replace("\f", "\n[page break]\n"), call.Int("offset") ?? 1, call.Int("limit") ?? DefaultLimit, env.Display(path)));
        if (size > MaxPdfBytes) return ToolOutcome.Error($"PDF is {size / 1024 / 1024} MB, too large to embed. Install poppler (pdftotext) to read it as text.");
        return new ToolOutcome
        {
            Content =
            [
                new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(bytes, new Uri(path).AbsoluteUri, "application/pdf") },
            ],
        };
    }

    static string? TryPdfToText(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("pdftotext") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-layout");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-");
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { p.Kill(true); return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
