using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ExecMcp.Plugins;

public sealed partial class PluginManifest
{
    public const string HostApiVersion = "0.1.0";

    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public string ApiVersion { get; set; } = "^0.1";
    public string Description { get; set; } = "";
    public RuntimeSpec Runtime { get; set; } = new();
    public PermissionsSpec Permissions { get; set; } = new();
    public JsonObject? Config { get; set; }
    public ExposureSpec Exposure { get; set; } = new();
    public LimitsSpec Limits { get; set; } = new();
    public List<string> DependsOn { get; set; } = [];

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public string Source { get; set; } = "";   // project | global | npm | pip
    [JsonIgnore] public string Namespace => string.IsNullOrEmpty(Exposure.Namespace) ? Name : Exposure.Namespace;

    [GeneratedRegex("^[a-z][a-z0-9-]*$")] private static partial Regex NameRx();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PluginManifest Load(string dir)
    {
        var file = Path.Combine(dir, "plugin.json");
        var m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException("plugin.json is empty");
        m.Dir = Path.GetFullPath(dir);
        return m;
    }

    /// <summary>All problems with the manifest; empty means valid.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!NameRx().IsMatch(Name)) errors.Add("name: must match [a-z][a-z0-9-]*");
        if (SemVer.TryParse(Version) is null) errors.Add($"version: '{Version}' is not semver");
        if (!SemVer.Satisfies(HostApiVersion, ApiVersion)) errors.Add($"apiVersion: '{ApiVersion}' is not compatible with host API {HostApiVersion}");
        switch (Runtime.Type)
        {
            case "node" or "python":
                if (string.IsNullOrEmpty(Runtime.Entry)) errors.Add("runtime.entry: required");
                else if (!File.Exists(Path.Combine(Dir, Runtime.Entry))) errors.Add($"runtime.entry: {Runtime.Entry} not found");
                break;
            case "stdio" or "mcp":
                if (string.IsNullOrEmpty(Runtime.Command) && string.IsNullOrEmpty(Runtime.Entry)) errors.Add("runtime.command: required");
                break;
            default:
                errors.Add($"runtime.type: '{Runtime.Type}' must be node, python, stdio or mcp");
                break;
        }
        if (Exposure.Mcp is not ("eager" or "deferred" or "hidden")) errors.Add("exposure.mcp: must be eager, deferred or hidden");
        if (Exposure.Namespace is { Length: > 0 } ns && !Regex.IsMatch(ns, "^[a-z][a-z0-9_]*$")) errors.Add("exposure.namespace: must match [a-z][a-z0-9_]*");
        if (Limits.TimeoutMs <= 0 || Limits.MaxOutputTokens <= 0 || Limits.Concurrency <= 0) errors.Add("limits: values must be positive");
        foreach (var e in Permissions.Exec.Where(e => e.Contains('/') || e.Contains('\\')))
            errors.Add($"permissions.exec: '{e}' must be a command name, not a path");
        return errors;
    }
}

public sealed class RuntimeSpec
{
    public string Type { get; set; } = "node";
    public string? Entry { get; set; }
    /// <summary>stdio/mcp: executable and arguments (relative paths resolve against the plugin dir).</summary>
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = new();
}

public sealed class PermissionsSpec
{
    [JsonConverter(typeof(NetSpecConverter))]
    public NetSpec Net { get; set; } = new();
    public FsSpec Fs { get; set; } = new();
    public List<string> Exec { get; set; } = [];
    public List<string> Secrets { get; set; } = [];
    public List<string> Llm { get; set; } = [];

    public string Canonical() => JsonSerializer.Serialize(this, PluginManifest.Json);
}

public sealed class NetSpec
{
    public List<string> Allow { get; set; } = [];
}

/// <summary>Accepts "net": "*" as shorthand for {"allow": ["*"]}.</summary>
public sealed class NetSpecConverter : JsonConverter<NetSpec>
{
    public override NetSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new NetSpec { Allow = [reader.GetString()!] };
        if (reader.TokenType == JsonTokenType.StartArray) return new NetSpec { Allow = JsonSerializer.Deserialize<List<string>>(ref reader) ?? [] };
        var allow = new List<string>();
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.TryGetProperty("allow", out var a))
            allow = a.ValueKind == JsonValueKind.String ? [a.GetString()!] : a.EnumerateArray().Select(x => x.GetString()!).ToList();
        return new NetSpec { Allow = allow };
    }

    public override void Write(Utf8JsonWriter writer, NetSpec value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("allow");
        JsonSerializer.Serialize(writer, value.Allow);
        writer.WriteEndObject();
    }
}

public sealed class FsSpec
{
    public List<string> Read { get; set; } = [];
    public List<string> Write { get; set; } = [];
}

public sealed class ExposureSpec
{
    public string? Namespace { get; set; }
    public string Mcp { get; set; } = "deferred";
    public bool Sandbox { get; set; } = true;
}

public sealed class LimitsSpec
{
    public int TimeoutMs { get; set; } = 30_000;
    public int MaxOutputTokens { get; set; } = 4_000;
    public int Concurrency { get; set; } = 4;
    public int RatePerMin { get; set; } = 120;
    public int MemoryMb { get; set; } = 512;
}

/// <summary>Accepts "net": {"allow": "*"} as well as a list.</summary>
public sealed class StringOrListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? [reader.GetString()!]
            : JsonSerializer.Deserialize<List<string>>(ref reader) ?? [];

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value);
}

public static class SemVer
{
    public static (int Major, int Minor, int Patch)? TryParse(string v)
    {
        var m = Regex.Match(v.Trim(), @"^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:[-+].*)?$");
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0, m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>Supports ^x.y, ~x.y, >=x.y, exact x.y.z and *.</summary>
    public static bool Satisfies(string version, string range)
    {
        range = range.Trim();
        if (range is "*" or "") return true;
        var v = TryParse(version);
        if (v is null) return false;
        char op = range[0];
        var r = TryParse(range.TrimStart('^', '~', '>', '='));
        if (r is null) return false;
        var (vM, vm, vp) = v.Value;
        var (rM, rm, rp) = r.Value;
        int cmp = (vM, vm, vp).CompareTo((rM, rm, rp));
        return op switch
        {
            '^' => cmp >= 0 && (rM > 0 ? vM == rM : rm > 0 ? vM == 0 && vm == rm : vM == 0 && vm == 0 && vp == rp),
            '~' => cmp >= 0 && vM == rM && vm == rm,
            '>' => cmp >= 0,
            _ => cmp == 0,
        };
    }
}
