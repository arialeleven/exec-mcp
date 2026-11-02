using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExecMcp.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ExecMcp.Http;

/// <summary>
/// Minimal single-owner OAuth 2.1 authorization server for remote MCP clients (claude.ai, Claude Code):
/// protected resource metadata (RFC 9728), server metadata (RFC 8414), Dynamic Client Registration (RFC 7591),
/// authorization code + PKCE S256, rotating refresh tokens. The owner approves each sign-in with a password.
/// </summary>
public sealed class OAuthServer
{
    const int AccessTtlSec = 3600, RefreshTtlDays = 30, CodeTtlSec = 120;

    readonly string _external;     // https://host:port
    readonly string _resource;     // https://host:port/mcp
    readonly byte[] _passwordHash;
    readonly string _stateFile;
    readonly ILogger _log;
    readonly object _lock = new();
    readonly ConcurrentDictionary<string, Code> _codes = new();
    readonly Queue<DateTime> _failures = new();
    State _state;

    sealed record Code(string ClientId, string RedirectUri, string Challenge, string Scope, DateTime Expires);

    sealed class Client
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> RedirectUris { get; set; } = [];
        public DateTime Created { get; set; }
    }

    sealed class Token
    {
        public string ClientId { get; set; } = "";
        public string Scope { get; set; } = "";
        public DateTime Expires { get; set; }
    }

    sealed class State
    {
        public Dictionary<string, Client> Clients { get; set; } = new();
        public Dictionary<string, Token> Access { get; set; } = new();    // key: sha256(token)
        public Dictionary<string, Token> Refresh { get; set; } = new();
    }

    public OAuthServer(string externalUrl, string password, string stateFile, ILogger log)
    {
        _external = externalUrl.TrimEnd('/');
        _resource = _external + "/mcp";
        _passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        _stateFile = stateFile;
        _log = log;
        _state = File.Exists(stateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(stateFile)) ?? new() : new();
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    void Save()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _state.Access.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToList()) _state.Access.Remove(k);
        foreach (var k in _state.Refresh.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToList()) _state.Refresh.Remove(k);
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(_state));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_stateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Only Claude's hosted callback and loopback redirects (Claude Code) are accepted.</summary>
    static bool RedirectAllowed(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        if (u.Scheme == "https" && u.Host == "claude.ai" && u.AbsolutePath == "/api/mcp/auth_callback") return true;
        return u.Scheme == "http" && (u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "[::1]");
    }

    static bool RedirectMatches(Client c, string uri)
    {
        if (c.RedirectUris.Contains(uri)) return true;
        // RFC 8252: loopback redirects match regardless of port.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != "http") return false;
        return c.RedirectUris.Any(r => Uri.TryCreate(r, UriKind.Absolute, out var x) && x.Scheme == "http" && x.Host == u.Host && x.AbsolutePath == u.AbsolutePath);
    }

    public bool ValidateBearer(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        if (!h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        lock (_lock)
            return _state.Access.TryGetValue(Hash(h[7..].Trim()), out var t) && t.Expires > DateTime.UtcNow;
    }

    public Task Challenge(HttpContext ctx)
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{_external}/.well-known/oauth-protected-resource/mcp\", scope=\"mcp\"";
        return ctx.Response.WriteAsJsonAsync(new { error = "invalid_token", error_description = "Sign in required" });
    }

    public void Map(WebApplication app)
    {
        object ResourceMeta() => new
        {
            resource = _resource,
            authorization_servers = new[] { _external },
            scopes_supported = new[] { "mcp" },
            bearer_methods_supported = new[] { "header" },
            resource_name = "exec-mcp",
        };
        app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(ResourceMeta()));
        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(ResourceMeta()));
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
        {
            issuer = _external,
            authorization_endpoint = _external + "/authorize",
            token_endpoint = _external + "/token",
            registration_endpoint = _external + "/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            scopes_supported = new[] { "mcp", "offline_access" },
        }));

        app.MapPost("/register", (Delegate)Register);
        app.MapGet("/authorize", (Delegate)AuthorizeGet);
        app.MapPost("/authorize", (Delegate)AuthorizePost);
        app.MapPost("/token", (Delegate)TokenEndpoint);
    }

    async Task<IResult> Register(HttpContext ctx)
    {
        JsonNode? body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
        catch { return Results.Json(new { error = "invalid_client_metadata", error_description = "body must be JSON" }, statusCode: 400); }
        var uris = (body?["redirect_uris"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? [];
        if (uris.Count == 0 || !uris.All(RedirectAllowed))
            return Results.Json(new { error = "invalid_redirect_uri", error_description = "only https://claude.ai/api/mcp/auth_callback and loopback redirects are allowed" }, statusCode: 400);
        var client = new Client
        {
            Id = "c_" + NewToken()[..24],
            Name = (body?["client_name"]?.GetValue<string>() ?? "MCP client")[..Math.Min(80, (body?["client_name"]?.GetValue<string>() ?? "MCP client").Length)],
            RedirectUris = uris,
            Created = DateTime.UtcNow,
        };
        lock (_lock)
        {
            // Bound growth: DCR registers a new client on every fresh connection.
            if (_state.Clients.Count > 200)
                foreach (var old in _state.Clients.Values.OrderBy(c => c.Created).Take(50).ToList()) _state.Clients.Remove(old.Id);
            _state.Clients[client.Id] = client;
            Save();
        }
        _log.LogInformation("oauth: registered client {Name} ({Id})", client.Name, client.Id);
        return Results.Json(new
        {
            client_id = client.Id,
            client_id_issued_at = new DateTimeOffset(client.Created).ToUnixTimeSeconds(),
            client_name = client.Name,
            redirect_uris = client.RedirectUris,
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
        }, statusCode: 201);
    }

    (Client? Client, string? Error) CheckAuthorizeParams(Func<string, string?> get)
    {
        Client? client;
        lock (_lock) _state.Clients.TryGetValue(get("client_id") ?? "", out client);
        if (client is null) return (null, "Unknown client_id. Remove the connector in Claude and add it again.");
        var redirect = get("redirect_uri") ?? "";
        if (!RedirectMatches(client, redirect)) return (null, "redirect_uri does not match the registered client.");
        if (get("response_type") != "code") return (null, "response_type must be code.");
        if (get("code_challenge_method") != "S256" || string.IsNullOrEmpty(get("code_challenge"))) return (null, "PKCE S256 is required.");
        var resource = get("resource");
        if (resource is not null && resource.TrimEnd('/') != _resource && resource.TrimEnd('/') != _external)
            return (null, $"resource must be {_resource}.");
        return (client, null);
    }

    IResult AuthorizeGet(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var (client, error) = CheckAuthorizeParams(k => q.TryGetValue(k, out var v) ? v.ToString() : null);
        if (client is null) return Html(ErrorPage(error!), 400);
        return Html(LoginPage(client, q.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), null));
    }

    async Task<IResult> AuthorizePost(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        string? Get(string k) => form.TryGetValue(k, out var v) ? v.ToString() : null;
        var (client, error) = CheckAuthorizeParams(Get);
        if (client is null) return Html(ErrorPage(error!), 400);
        var fields = form.Where(kv => kv.Key != "password" && kv.Key != "action").ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var redirect = Get("redirect_uri")!;
        var state = Get("state");

        if (Get("action") == "deny")
            return Results.Redirect(AddQuery(redirect, ("error", "access_denied"), ("state", state)));

        lock (_failures)
        {
            while (_failures.Count > 0 && _failures.Peek() < DateTime.UtcNow.AddMinutes(-15)) _failures.Dequeue();
            if (_failures.Count >= 10) return Html(ErrorPage("Too many wrong passwords. Try again in 15 minutes."), 429);
        }
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(Get("password") ?? ""));
        if (!CryptographicOperations.FixedTimeEquals(given, _passwordHash))
        {
            lock (_failures) _failures.Enqueue(DateTime.UtcNow);
            _log.LogWarning("oauth: wrong password from {Ip}", ctx.Connection.RemoteIpAddress);
            await Task.Delay(1000);
            return Html(LoginPage(client, fields, "Wrong password."), 401);
        }

        var code = NewToken();
        _codes[code] = new Code(client.Id, redirect, Get("code_challenge")!, Get("scope") ?? "mcp", DateTime.UtcNow.AddSeconds(CodeTtlSec));
        _log.LogInformation("oauth: approved sign-in for {Client}", client.Name);
        return Results.Redirect(AddQuery(redirect, ("code", code), ("state", state), ("iss", _external)));
    }

    async Task<IResult> TokenEndpoint(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType) return TokenError("invalid_request", "use application/x-www-form-urlencoded");
        var form = await ctx.Request.ReadFormAsync();
        string? Get(string k) => form.TryGetValue(k, out var v) ? v.ToString() : null;

        switch (Get("grant_type"))
        {
            case "authorization_code":
            {
                if (!_codes.TryRemove(Get("code") ?? "", out var code) || code.Expires < DateTime.UtcNow)
                    return TokenError("invalid_grant", "code is invalid or expired");
                if (code.ClientId != Get("client_id") || code.RedirectUri != Get("redirect_uri"))
                    return TokenError("invalid_grant", "client_id or redirect_uri mismatch");
                var verifier = Get("code_verifier") ?? "";
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(code.Challenge)))
                    return TokenError("invalid_grant", "PKCE verification failed");
                return Issue(code.ClientId, code.Scope);
            }
            case "refresh_token":
            {
                Token? t;
                lock (_lock)
                {
                    var key = Hash(Get("refresh_token") ?? "");
                    if (!_state.Refresh.TryGetValue(key, out t) || t.Expires < DateTime.UtcNow || (Get("client_id") is { } cid && cid != t.ClientId))
                        return TokenError("invalid_grant", "refresh token is invalid or expired");
                    _state.Refresh.Remove(key);   // rotation: each refresh token works once
                }
                return Issue(t.ClientId, t.Scope);
            }
            default:
                return TokenError("unsupported_grant_type", "use authorization_code or refresh_token");
        }
    }

    IResult Issue(string clientId, string scope)
    {
        var access = NewToken();
        var refresh = NewToken();
        lock (_lock)
        {
            _state.Access[Hash(access)] = new Token { ClientId = clientId, Scope = scope, Expires = DateTime.UtcNow.AddSeconds(AccessTtlSec) };
            _state.Refresh[Hash(refresh)] = new Token { ClientId = clientId, Scope = scope, Expires = DateTime.UtcNow.AddDays(RefreshTtlDays) };
            Save();
        }
        return Results.Json(new { access_token = access, token_type = "Bearer", expires_in = AccessTtlSec, refresh_token = refresh, scope },
            statusCode: 200);
    }

    static IResult TokenError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: 400);

    static string AddQuery(string uri, params (string Key, string? Value)[] pairs)
    {
        var sb = new StringBuilder(uri);
        var sep = uri.Contains('?') ? '&' : '?';
        foreach (var (k, v) in pairs)
        {
            if (v is null) continue;
            sb.Append(sep).Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
            sep = '&';
        }
        return sb.ToString();
    }

    static IResult Html(string body, int status = 200) => Results.Content(body, "text/html; charset=utf-8", Encoding.UTF8, status);

    static string E(string s) => WebUtility.HtmlEncode(s);

    const string Style = "<style>body{font-family:system-ui,sans-serif;max-width:420px;margin:60px auto;padding:0 16px;color:#222}" +
                         "input[type=password]{width:100%;padding:10px;font-size:16px;box-sizing:border-box;margin:8px 0 16px}" +
                         "button{padding:10px 18px;font-size:15px;margin-right:8px}.err{color:#b00}.muted{color:#666;font-size:14px}" +
                         "@media(prefers-color-scheme:dark){body{background:#1b1b1b;color:#eee}.muted{color:#aaa}}</style>";

    string LoginPage(Client client, Dictionary<string, string> fields, string? error)
    {
        var hidden = string.Concat(fields.Where(kv => kv.Key is not ("password" or "action"))
            .Select(kv => $"<input type=\"hidden\" name=\"{E(kv.Key)}\" value=\"{E(kv.Value)}\">"));
        var redirectHost = Uri.TryCreate(fields.GetValueOrDefault("redirect_uri"), UriKind.Absolute, out var u) ? u.Host : "?";
        return $"""
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>exec-mcp: вход</title>{Style}</head><body>
            <h2>exec-mcp</h2>
            <p><b>{E(client.Name)}</b> просит доступ к инструментам exec-mcp на <b>{E(new Uri(_external).Host)}</b>:
            чтение и запись файлов и выполнение команд.</p>
            <p class="muted">После входа браузер вернётся на <b>{E(redirectHost)}</b>.</p>
            {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
            <form method="post" action="/authorize">{hidden}
            <label>Пароль владельца<input type="password" name="password" autofocus autocomplete="current-password"></label>
            <button type="submit" name="action" value="allow">Разрешить</button>
            <button type="submit" name="action" value="deny">Отклонить</button>
            </form></body></html>
            """;
    }

    static string ErrorPage(string message) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>exec-mcp</title>{Style}</head><body><h2>exec-mcp</h2><p class=\"err\">{E(message)}</p></body></html>";
}
