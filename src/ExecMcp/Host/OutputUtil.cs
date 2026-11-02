using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ExecMcp.Host;

public static partial class OutputUtil
{
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Ansi();

    /// <summary>Removes ANSI escapes and carriage-return progress redraws.</summary>
    public static string Clean(string s)
    {
        if (s.Length == 0) return s;
        s = Ansi().Replace(s, "");
        if (!s.Contains('\r')) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
        {
            var idx = line.TrimEnd('\r').LastIndexOf('\r');
            sb.Append(idx >= 0 ? line[(idx + 1)..] : line).Append('\n');
        }
        sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// Keeps the head and the (more important) tail, dropping whole lines from the middle.
    /// Returns null when the text already fits.
    /// </summary>
    public static string? HeadTail(string text, int maxChars, string? spillNote = null)
    {
        if (text.Length <= maxChars) return null;
        var lines = text.Split('\n');
        int headBudget = maxChars / 3, tailBudget = maxChars - headBudget;
        int h = 0, used = 0;
        while (h < lines.Length && used + lines[h].Length + 1 <= headBudget) used += lines[h++].Length + 1;
        int t = lines.Length, tUsed = 0;
        while (t > h && tUsed + lines[t - 1].Length + 1 <= tailBudget) tUsed += lines[--t].Length + 1;

        var sb = new StringBuilder();
        if (h == 0 && t == lines.Length)
        {
            // A single giant line: cut by characters.
            sb.Append(text, 0, headBudget).Append($"\n… {text.Length - maxChars} chars truncated …\n").Append(text, text.Length - tailBudget, tailBudget);
        }
        else
        {
            for (int i = 0; i < h; i++) sb.Append(lines[i]).Append('\n');
            sb.Append($"… {t - h} lines truncated …\n");
            for (int i = t; i < lines.Length; i++) sb.Append(lines[i]).Append(i < lines.Length - 1 ? "\n" : "");
        }
        if (spillNote is not null) sb.Append('\n').Append(spillNote);
        return sb.ToString();
    }

    /// <summary>Writes the full text to an artifact and returns the truncated view pointing at it.</summary>
    public static string Spill(HostEnv env, string text, int maxChars, string prefix)
    {
        if (text.Length <= maxChars) return text;
        var path = env.NewArtifactPath(prefix, ".txt");
        File.WriteAllText(path, text);
        var note = $"… truncated. Full output (~{text.Length / 4} tokens): {path} — use Read with offset or Grep";
        return HeadTail(text, maxChars, note)!;
    }

    public static int Levenshtein(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    public static IEnumerable<string> Closest(string target, IEnumerable<string> candidates, int max = 3) =>
        candidates.Select(c => (c, dist: Levenshtein(target, c)))
            .Where(x => x.dist <= Math.Max(2, target.Length / 3))
            .OrderBy(x => x.dist).Take(max).Select(x => x.c);
}

/// <summary>Secret values registered here are scrubbed from every tool output and log line.</summary>
public static class Secrets
{
    static readonly ConcurrentDictionary<string, byte> Values = new();

    public static void Register(string? value)
    {
        if (!string.IsNullOrEmpty(value) && value.Length >= 4) Values.TryAdd(value, 0);
    }

    public static string Redact(string s)
    {
        if (Values.IsEmpty || string.IsNullOrEmpty(s)) return s;
        foreach (var v in Values.Keys) s = s.Replace(v, "[REDACTED]");
        return s;
    }
}
