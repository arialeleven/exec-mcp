using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExecMcp.Host;

namespace ExecMcp.Plugins;

/// <summary>
/// Implements the plugin's ctx (host/* methods). Every side effect is checked against the manifest's
/// permissions here, in the host, not in the plugin.
/// </summary>
public sealed class PluginContextHost
{
    const int MaxFetchBytes = 5 * 1024 * 1024, MaxExecOutput = 1024 * 1024;

    readonly PluginManifest _m;
    readonly HostEnv _env;
    readonly ToolRegistry _registry;
    readonly Llm _llm;
    readonly Func<string, ToolCall?> _callContext;
    readonly List<Regex> _fsRead, _fsWrite;
    readonly object _kvLock = new();

    public string CacheDir { get; }
    public string ArtifactsDir { get; }
    string StateFile => Path.Combine(_env.StateRoot, _m.Name + ".json");
    string CacheFile => Path.Combine(CacheDir, "cache.json");

    public PluginContextHost(PluginManifest m, HostEnv env, ToolRegistry registry, Llm llm, Func<string, ToolCall?> callContext)
    {
        _m = m;
        _env = env;
        _registry = registry;
        _llm = llm;
        _callContext = callContext;
        CacheDir = Path.Combine(env.CacheRoot, m.Name);
        ArtifactsDir = Path.Combine(env.ArtifactsDir, m.Name);
        _fsRead = m.Permissions.Fs.Read.Concat(m.Permissions.Fs.Write).Select(GlobToRegex).ToList();
        _fsWrite = m.Permissions.Fs.Write.Select(GlobToRegex).ToList();
    }

    public string Expand(string s) => s
        .Replace("${workspace}", _env.Workspace)
        .Replace("${cache}", CacheDir)
        .Replace("${artifacts}", ArtifactsDir)
        .Replace("${plugin}", _m.Dir)
        .Replace("${home}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    Regex GlobToRegex(string glob)
    {
        var g = Path.GetFullPath(Expand(glob).Replace("**", "\u0001").Replace("*", "\u0002")).Replace('\\', '/');
        var rx = Regex.Escape(g).Replace("\u0001", ".*").Replace("\u0002", "[^/]*").Replace(@"\?", "[^/]");
        // "dir/**" also matches "dir" itself
        rx = rx.Replace("/.*", "(/.*)?");
        return new Regex("^" + rx + "$", OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase);
    }

    public static ToolException Denied(string what) => new("PERMISSION_DENIED", $"{what} is not allowed by the plugin's manifest permissions");

    public async Task<JsonNode?> HandleAsync(string method, JsonNode? p, CancellationToken ct)
    {
        var callId = p?["callId"]?.GetValue<string>();
        var call = callId is null ? null : _callContext(callId);
        var token = call?.Ct ?? ct;
        return method switch
        {
            "host/fetch" => await FetchAsync(p!, token),
            "host/fs.read" => FsRead(p!),
            "host/fs.write" => FsWrite(p!),
            "host/exec" => await ExecAsync(p!, token),
            "host/secrets.get" => SecretGet(p!),
            "host/llm.complete" => await LlmAsync(p!, call, token),
            "host/artifacts.write" => ArtifactWrite(p!),
            "host/cache.get" => CacheGet(p!),
            "host/cache.set" => CacheSet(p!),
            "host/state.get" => KvGet(StateFile, p!["key"]!.GetValue<string>())?.DeepClone(),
            "host/state.set" => KvSet(StateFile, p!["key"]!.GetValue<string>(), p["value"]?.DeepClone()),
            "host/state.delete" => KvSet(StateFile, p!["key"]!.GetValue<string>(), null, delete: true),
            "host/call" => await CallAsync(p!, call, token),
            _ => throw new ToolException("NOT_FOUND", $"unknown host method {method}"),
        };
    }

    // ---- net ----

    public bool NetAllowed(Uri uri)
    {
        var port = uri.Port;
        foreach (var rule in _m.Permissions.Net.Allow)
        {
            if (rule == "*") return true;
            var r = rule.Contains("://") ? new Uri(rule).Authority : rule;
            var parts = r.Split(':');
            var hostRule = parts[0];
            bool hostOk = hostRule.StartsWith("*.")
                ? uri.Host.EndsWith(hostRule[1..], StringComparison.OrdinalIgnoreCase)
                : uri.Host.Equals(hostRule, StringComparison.OrdinalIgnoreCase);
            bool portOk = parts.Length < 2 || parts[1] == "*" || parts[1] == port.ToString();
            if (hostOk && portOk) return true;
        }
        return false;
    }

    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    async Task<JsonNode?> FetchAsync(JsonNode p, CancellationToken ct)
    {
        var url = new Uri(p["url"]!.GetValue<string>());
        if (url.Scheme is not ("http" or "https")) throw new ToolException("INVALID_INPUT", "only http(s) URLs are supported");
        var method = new HttpMethod(p["method"]?.GetValue<string>() ?? "GET");
        string? body = p["body"]?.GetValue<string>();
        var headers = p["headers"] as JsonObject;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Min(_m.Limits.TimeoutMs, p["timeoutMs"]?.GetValue<int>() ?? int.MaxValue));

        for (int hop = 0; hop < 5; hop++)
        {
            // Checked on every hop, so a redirect cannot leave the allowlist.
            if (!NetAllowed(url)) throw Denied($"network access to {url.Host}:{url.Port}");
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8);
            foreach (var (k, v) in headers ?? [])
            {
                var value = v?.GetValue<string>() ?? "";
                if (!req.Headers.TryAddWithoutValidation(k, value)) req.Content?.Headers.TryAddWithoutValidation(k, value);
            }
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ToolException("TIMEOUT", $"fetch {url} timed out", retryable: true); }
            catch (HttpRequestException ex) { throw new ToolException("UPSTREAM", $"fetch {url} failed: {ex.Message}", retryable: true); }
            using (resp)
            {
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                {
                    url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                    if (resp.StatusCode is HttpStatusCode.SeeOther) { method = HttpMethod.Get; body = null; }
                    continue;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
                var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buf, timeout.Token)) > 0)
                {
                    if (ms.Length + n > MaxFetchBytes) throw new ToolException("UPSTREAM", $"response from {url} exceeds {MaxFetchBytes / 1024 / 1024} MB");
                    ms.Write(buf, 0, n);
                }
                var bytes = ms.ToArray();
                var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
                bool text = ctype.StartsWith("text/") || ctype.Contains("json") || ctype.Contains("xml") || ctype.Contains("javascript") || ctype.Length == 0;
                var hdrs = new JsonObject();
                foreach (var h in resp.Headers.Concat(resp.Content.Headers)) hdrs[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
                return new JsonObject
                {
                    ["status"] = (int)resp.StatusCode,
                    ["url"] = url.ToString(),
                    ["headers"] = hdrs,
                    ["body"] = text ? Encoding.UTF8.GetString(bytes) : null,
                    ["bodyBase64"] = text ? null : Convert.ToBase64String(bytes),
                };
            }
        }
        throw new ToolException("UPSTREAM", "too many redirects");
    }

    // ---- fs ----

    string CheckPath(string raw, List<Regex> allowed, string what)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(_env.Workspace, raw)).Replace('\\', '/');
        // Resolve symlinks so a link inside an allowed dir cannot point outside it.
        try
        {
            var fi = new FileInfo(full);
            if (fi.Exists && fi.ResolveLinkTarget(true) is { } target) full = target.FullName.Replace('\\', '/');
        }
        catch { }
        if (!allowed.Any(r => r.IsMatch(full))) throw Denied($"{what} {full}");
        return full;
    }

    JsonNode? FsRead(JsonNode p)
    {
        var path = CheckPath(p["path"]!.GetValue<string>(), _fsRead, "fs.read of");
        if (Directory.Exists(path))
            return new JsonObject { ["entries"] = new JsonArray(Directory.EnumerateFileSystemEntries(path).Select(e => (JsonNode)Path.GetFileName(e)!).ToArray()) };
        if (!File.Exists(path)) throw new ToolException("NOT_FOUND", $"file not found: {path}");
        var bytes = File.ReadAllBytes(path);
        return p["encoding"]?.GetValue<string>() == "base64"
            ? new JsonObject { ["base64"] = Convert.ToBase64String(bytes) }
            : new JsonObject { ["text"] = Encoding.UTF8.GetString(bytes) };
    }

    JsonNode? FsWrite(JsonNode p)
    {
        var path = CheckPath(p["path"]!.GetValue<string>(), _fsWrite, "fs.write to");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (p["base64"] is { } b64) File.WriteAllBytes(path, Convert.FromBase64String(b64.GetValue<string>()));
        else File.WriteAllText(path, p["text"]?.GetValue<string>() ?? "");
        return new JsonObject { ["path"] = path };
    }

    // ---- exec ----

    async Task<JsonNode?> ExecAsync(JsonNode p, CancellationToken ct)
    {
        var cmd = p["cmd"]!.GetValue<string>();
        var name = Path.GetFileNameWithoutExtension(cmd);
        if (!_m.Permissions.Exec.Contains(name) && !_m.Permissions.Exec.Contains(cmd)) throw Denied($"exec of '{cmd}'");
        var psi = new ProcessStartInfo(cmd)
        {
            WorkingDirectory = p["cwd"] is { } cwd ? CheckPath(cwd.GetValue<string>(), _fsRead, "exec cwd") : _env.Workspace,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in (p["args"] as JsonArray) ?? []) psi.ArgumentList.Add(a!.GetValue<string>());
        using var proc = Process.Start(psi) ?? throw new ToolException("UPSTREAM", $"could not start {cmd}");
        if (p["stdin"] is { } stdin) await proc.StandardInput.WriteAsync(stdin.GetValue<string>());
        proc.StandardInput.Close();
        var o = proc.StandardOutput.ReadToEndAsync(ct);
        var e = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Min(_m.Limits.TimeoutMs, p["timeoutMs"]?.GetValue<int>() ?? int.MaxValue));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new ToolException("TIMEOUT", $"{cmd} timed out");
        }
        static string Cap(string s) => s.Length > MaxExecOutput ? s[..MaxExecOutput] + "\n… truncated" : s;
        return new JsonObject { ["exitCode"] = proc.ExitCode, ["stdout"] = Cap(await o), ["stderr"] = Cap(await e) };
    }

    // ---- secrets / llm / artifacts ----

    public string? SecretValue(string name)
    {
        if (!_m.Permissions.Secrets.Contains(name)) throw Denied($"secret '{name}'");
        string? v = null;
        if (_env.Config.Plugins.TryGetValue(_m.Name, out var pc)) pc.Secrets.TryGetValue(name, out v);
        v ??= Environment.GetEnvironmentVariable(name);
        Secrets.Register(v);
        return v;
    }

    JsonNode? SecretGet(JsonNode p) => SecretValue(p["name"]!.GetValue<string>()) is { } v ? JsonValue.Create(v) : null;

    async Task<JsonNode?> LlmAsync(JsonNode p, ToolCall? call, CancellationToken ct)
    {
        var model = p["model"]?.GetValue<string>() ?? "small";
        if (!_m.Permissions.Llm.Contains(model)) throw Denied($"llm model '{model}'");
        var maxTokens = Math.Clamp(p["maxTokens"]?.GetValue<int>() ?? 1024, 1, 8192);
        var text = await _llm.CompleteAsync(p["prompt"]!.GetValue<string>(), model, maxTokens, call?.Server ?? _registry.Server, ct);
        return JsonValue.Create(text);
    }

    JsonNode? ArtifactWrite(JsonNode p)
    {
        var name = Regex.Replace(p["name"]!.GetValue<string>(), @"[^A-Za-z0-9._-]", "_");
        Directory.CreateDirectory(ArtifactsDir);
        var path = Path.Combine(ArtifactsDir, name);
        if (p["base64"] is { } b64) File.WriteAllBytes(path, Convert.FromBase64String(b64.GetValue<string>()));
        else File.WriteAllText(path, p["text"]?.GetValue<string>() ?? "");
        return new JsonObject { ["path"] = _env.Display(path), ["uri"] = new Uri(path).AbsoluteUri };
    }

    // ---- cache / state ----

    JsonNode? CacheGet(JsonNode p)
    {
        var entry = KvGet(CacheFile, p["key"]!.GetValue<string>());
        if (entry is not JsonObject o) return null;
        if (o["expires"]?.GetValue<long>() is { } exp && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        return o["value"]?.DeepClone();
    }

    JsonNode? CacheSet(JsonNode p)
    {
        var ttl = p["ttlSec"]?.GetValue<long>() ?? 3600;
        return KvSet(CacheFile, p["key"]!.GetValue<string>(), new JsonObject
        {
            ["value"] = p["value"]?.DeepClone(),
            ["expires"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl,
        });
    }

    JsonNode? KvGet(string file, string key)
    {
        lock (_kvLock)
            return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject o ? o[key]?.DeepClone() : null;
    }

    JsonNode? KvSet(string file, string key, JsonNode? value, bool delete = false)
    {
        lock (_kvLock)
        {
            var o = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject existing ? existing : new JsonObject();
            if (delete) o.Remove(key); else o[key] = value;
            if (file == CacheFile)
            {
                // Drop expired entries so the cache file does not grow forever.
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var k in o.Where(kv => kv.Value?["expires"]?.GetValue<long>() < now).Select(kv => kv.Key).ToList()) o.Remove(k);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, o.ToJsonString());
        }
        return null;
    }

    // ---- call ----

    async Task<JsonNode?> CallAsync(JsonNode p, ToolCall? call, CancellationToken ct)
    {
        var target = p["tool"]!.GetValue<string>();          // "ns.tool" or "ns_tool"
        var tool = _registry.All().FirstOrDefault(t => t.Name == target || $"{t.Namespace}.{t.SandboxName}" == target);
        if (tool?.Plugin is null || !_m.DependsOn.Contains(tool.Plugin)) throw Denied($"call to '{target}' (add the plugin to dependsOn)");
        var input = p["input"] is { } i ? JsonSerializer.SerializeToElement(i) : JsonDocument.Parse("{}").RootElement;
        var outcome = await _registry.InvokeAsync(tool.Name, input, CallOrigin.Sandbox, call?.Server, null, ct);
        if (outcome.IsError) throw new ToolException(outcome.ErrorCode ?? "UPSTREAM", outcome.AllText(), outcome.Retryable);
        return outcome.Value?.DeepClone() ?? outcome.Structured?.DeepClone() ?? JsonValue.Create(outcome.AllText());
    }
}
