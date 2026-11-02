using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExecMcp.Host;

namespace ExecMcp.Tools;

/// <summary>Remembers which files were Read and what they looked like, so Edit/Write can refuse stale edits.</summary>
public sealed class FileState
{
    readonly record struct Snapshot(DateTime MtimeUtc, long Size, string Hash);

    readonly ConcurrentDictionary<string, Snapshot> _seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public void Record(string path, byte[] content)
    {
        var fi = new FileInfo(path);
        _seen[path] = new Snapshot(fi.LastWriteTimeUtc, fi.Length, Hash(content));
    }

    /// <summary>Null when the file may be edited, otherwise an error message telling the model what to do.</summary>
    public string? CheckFresh(string path, HostEnv env)
    {
        if (!_seen.TryGetValue(path, out var snap))
            return $"File has not been read yet: {env.Display(path)}. Read it first.";
        var fi = new FileInfo(path);
        if (!fi.Exists) return $"File no longer exists: {env.Display(path)}.";
        if (fi.LastWriteTimeUtc == snap.MtimeUtc && fi.Length == snap.Size) return null;
        // mtime changed but the content may be identical (touch, formatter no-op).
        return Hash(File.ReadAllBytes(path)) == snap.Hash ? null : $"File modified since read: {env.Display(path)}. Read it again.";
    }

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
}

/// <summary>Text file decoding/encoding that preserves BOM, line endings and the final newline.</summary>
public sealed class TextFile
{
    public required string Text { get; init; }      // always LF
    public bool Bom { get; init; }
    public bool Crlf { get; init; }

    public static TextFile Decode(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var s = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        int crlf = 0, lf = 0;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\n') { if (i > 0 && s[i - 1] == '\r') crlf++; else lf++; }
        return new TextFile { Text = s.Replace("\r\n", "\n"), Bom = bom, Crlf = crlf > lf };
    }

    public byte[] Encode(string lfText)
    {
        var s = Crlf ? lfText.Replace("\r\n", "\n").Replace("\n", "\r\n") : lfText;
        var body = Encoding.UTF8.GetBytes(s);
        return Bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    /// <summary>
    /// NUL bytes, or many control characters (random data, compressed files). Invalid UTF-8 alone is not enough:
    /// legacy-encoded text (cp1251, latin1) has few control characters and is still shown.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var head = bytes[..Math.Min(bytes.Length, 8000)];
        if (head.Length == 0) return false;
        if (head.Contains((byte)0)) return true;
        int control = 0;
        foreach (var b in head)
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C or 0x1B)) control++;
        double ratio = (double)control / head.Length;
        // Drop a possibly cut multi-byte sequence at the end before validating.
        var check = head.Length == bytes.Length ? head : head[..Math.Max(0, head.Length - 3)];
        bool utf8 = System.Text.Unicode.Utf8.IsValid(check);
        return ratio > 0.3 || (!utf8 && ratio > 0.05);
    }
}

internal static class Schema
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

internal static class PathHints
{
    /// <summary>"File not found" with a suggestion: similar names next to it or the same name elsewhere in the workspace.</summary>
    public static string NotFound(string path, HostEnv env)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        var suggestions = new List<string>();
        if (dir is not null && Directory.Exists(dir))
        {
            suggestions.AddRange(OutputUtil.Closest(name, Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName)!)
                .Select(n => env.Display(Path.Combine(dir, n))));
        }
        if (suggestions.Count == 0)
        {
            try
            {
                suggestions.AddRange(FileWalker.Enumerate(env.Workspace, env.Workspace, CancellationToken.None)
                    .Where(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                    .Take(3).Select(env.Display));
            }
            catch { /* best effort */ }
        }
        var msg = $"File not found: {env.Display(path)}.";
        return suggestions.Count > 0 ? $"{msg} Did you mean {string.Join(", ", suggestions)}?" : msg + " Use Glob to find files.";
    }
}
