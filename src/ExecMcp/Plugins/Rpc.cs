using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ExecMcp.Host;
using Microsoft.Extensions.Logging;

namespace ExecMcp.Plugins;

public sealed class RpcException(string message, JsonNode? data = null) : Exception(message)
{
    public JsonNode? Data2 { get; } = data;
}

/// <summary>JSON-RPC 2.0 over a child process's stdin/stdout, one message per line. stderr goes to the host log.</summary>
public sealed class StdioRpc : IAsyncDisposable
{
    readonly Process _proc;
    readonly ILogger _log;
    readonly string _name;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    long _nextId;

    public Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? OnRequest { get; set; }
    public Action<string, JsonNode?>? OnNotification { get; set; }
    public event Action<int>? Exited;
    public bool HasExited => _proc.HasExited;

    public StdioRpc(ProcessStartInfo psi, string name, ILogger log)
    {
        _name = name;
        _log = log;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.Exited += (_, _) =>
        {
            foreach (var (_, tcs) in _pending) tcs.TrySetException(new RpcException($"plugin '{_name}' exited (code {SafeExitCode()})"));
            _pending.Clear();
            Exited?.Invoke(SafeExitCode());
        };
        _proc.Start();
        _ = ReadLoop();
        _ = ErrLoop();
    }

    int SafeExitCode() { try { return _proc.ExitCode; } catch { return -1; } }

    async Task ReadLoop()
    {
        try
        {
            while (await _proc.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch { _log.LogWarning("[{Plugin}] non-JSON on stdout: {Line}", _name, Secrets.Redact(Trim(line))); continue; }
                if (msg is not JsonObject obj) continue;

                var method = obj["method"]?.GetValue<string>();
                if (method is null)
                {
                    if (obj["id"] is { } idNode && _pending.TryRemove(idNode.GetValue<long>(), out var tcs))
                    {
                        if (obj["error"] is JsonObject err)
                            tcs.TrySetException(new RpcException(err["message"]?.GetValue<string>() ?? "error", err["data"]?.DeepClone()));
                        else tcs.TrySetResult(obj["result"]?.DeepClone());
                    }
                }
                else if (obj["id"] is { } reqId)
                    _ = HandleRequest(reqId.DeepClone(), method, obj["params"]?.DeepClone());
                else
                    OnNotification?.Invoke(method, obj["params"]?.DeepClone());
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "[{Plugin}] read loop ended", _name); }
    }

    async Task ErrLoop()
    {
        try
        {
            while (await _proc.StandardError.ReadLineAsync() is { } line)
                _log.LogInformation("[{Plugin}] {Line}", _name, Secrets.Redact(Trim(line)));
        }
        catch { }
    }

    static string Trim(string s) => s.Length > 2000 ? s[..2000] + "…" : s;

    async Task HandleRequest(JsonNode id, string method, JsonNode? p)
    {
        JsonObject response;
        try
        {
            if (OnRequest is null) throw new RpcException($"method not found: {method}");
            var result = await OnRequest(method, p, CancellationToken.None);
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (ToolException tex)
        {
            response = Error(id, tex.Message, new JsonObject { ["code"] = tex.Code, ["retryable"] = tex.Retryable });
        }
        catch (Exception ex)
        {
            response = Error(id, ex.Message, new JsonObject { ["code"] = "INTERNAL" });
        }
        await WriteAsync(response);
    }

    static JsonObject Error(JsonNode id, string message, JsonObject data) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message, ["data"] = data },
    };

    async Task WriteAsync(JsonNode msg)
    {
        var line = msg.ToJsonString();
        await _writeLock.WaitAsync();
        try
        {
            await _proc.StandardInput.WriteLineAsync(line);
            await _proc.StandardInput.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? p, CancellationToken ct)
    {
        if (_proc.HasExited) throw new RpcException($"plugin '{_name}' is not running");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p });
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task; }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, JsonNode? p) =>
        _proc.HasExited ? Task.CompletedTask : WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p });

    public async ValueTask DisposeAsync()
    {
        if (!_proc.HasExited)
        {
            try
            {
                using var cts = new CancellationTokenSource(2000);
                await RequestAsync("shutdown", null, cts.Token);
            }
            catch { }
            try { _proc.StandardInput.Close(); } catch { }
            if (!_proc.WaitForExit(2000)) try { _proc.Kill(entireProcessTree: true); } catch { }
        }
        _proc.Dispose();
    }
}
