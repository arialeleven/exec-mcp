using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ExecMcp.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExecMcp.Tests;

public class ProxyProtocolTests
{
    [Theory]
    [InlineData("PROXY TCP4 160.79.106.25 203.0.113.10 51000 443", "160.79.106.25:51000")]
    [InlineData("PROXY TCP6 2001:db8::1 2001:db8::2 1234 443", "[2001:db8::1]:1234")]
    public void ParsesValidHeaders(string line, string expected) =>
        Assert.Equal(expected, HttpHost.ParseProxyHeader(line)!.ToString());

    [Theory]
    [InlineData("PROXY UNKNOWN")]
    [InlineData("PROXY TCP4 2001:db8::1 1.2.3.4 1 2")]
    [InlineData("PROXY TCP4 1.2.3.4 5.6.7.8 99999 443")]
    [InlineData("GET / HTTP/1.1")]
    public void RejectsInvalidHeaders(string line) => Assert.Null(HttpHost.ParseProxyHeader(line));

    [Theory]
    [InlineData("160.79.104.1", true)]
    [InlineData("160.79.111.254", true)]
    [InlineData("160.79.112.1", false)]
    [InlineData("::ffff:160.79.106.25", true)]
    [InlineData("203.0.113.10", false)]
    [InlineData("127.0.0.1", false)]
    public void AnthropicEgressRange(string ip, bool expected) =>
        Assert.Equal(expected, HttpHost.IsAnthropicEgress(IPAddress.Parse(ip)));
}

/// <summary>The OAuth flow Claude runs against a custom connector, end to end, in memory.</summary>
public class OAuthTests : IAsyncLifetime
{
    const string Base = "https://mcp.example.test";
    const string Callback = "https://claude.ai/api/mcp/auth_callback";
    readonly string _state = Path.Combine(Path.GetTempPath(), "exec-mcp-tests", Guid.NewGuid().ToString("n"), "oauth.json");
    WebApplication _app = null!;
    HttpClient _http = null!;
    OAuthServer _oauth = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _oauth = new OAuthServer(Base, "pw", _state, NullLogger.Instance);
        _app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp") && !_oauth.ValidateBearer(ctx)) { await _oauth.Challenge(ctx); return; }
            await next();
        });
        _oauth.Map(_app);
        _app.MapPost("/mcp", () => "ok");
        await _app.StartAsync();
        _http = _app.GetTestClient();
        _http.BaseAddress = new Uri(Base);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    async Task<HttpResponseMessage> Authorize(string clientId, string challenge, string password) =>
        await _http.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = Callback, ["state"] = "s1",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["resource"] = Base + "/mcp",
            ["password"] = password, ["action"] = "allow",
        }));

    [Fact]
    public async Task FullFlow()
    {
        var mcp = await _http.PostAsync("/mcp", null);
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.Contains("resource_metadata=", mcp.Headers.WwwAuthenticate.ToString());

        var prm = await _http.GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(Base + "/mcp", prm!["resource"]!.GetValue<string>());
        var asm = await _http.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server");
        Assert.Contains("S256", asm!["code_challenge_methods_supported"]!.ToJsonString());

        var reg = await _http.PostAsJsonAsync("/register", new { client_name = "Claude", redirect_uris = new[] { Callback } });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var clientId = (await reg.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();

        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorize(clientId, challenge, "wrong")).StatusCode);
        var auth = await Authorize(clientId, challenge, "pw");
        Assert.Equal(HttpStatusCode.Redirect, auth.StatusCode);
        var location = auth.Headers.Location!.ToString();
        Assert.StartsWith(Callback, location);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["code"]!;

        var token = await _http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Callback,
            ["client_id"] = clientId, ["code_verifier"] = verifier,
        }));
        var t = (await token.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("Bearer", t["token_type"]!.GetValue<string>());

        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        req.Headers.Authorization = new("Bearer", t["access_token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(req)).StatusCode);

        // Refresh tokens rotate: the old one stops working.
        var refresh = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = t["refresh_token"]!.GetValue<string>() };
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync("/token", new FormUrlEncodedContent(refresh))).StatusCode);
        var again = await _http.PostAsync("/token", new FormUrlEncodedContent(refresh));
        Assert.Contains("invalid_grant", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RejectsForeignRedirectsAndBadPkce()
    {
        var evil = await _http.PostAsJsonAsync("/register", new { client_name = "x", redirect_uris = new[] { "https://evil.example/cb" } });
        Assert.Equal(HttpStatusCode.BadRequest, evil.StatusCode);

        var reg = await _http.PostAsJsonAsync("/register", new { client_name = "Claude", redirect_uris = new[] { Callback } });
        var clientId = (await reg.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();
        var auth = await Authorize(clientId, B64(SHA256.HashData("right"u8.ToArray())), "pw");
        var code = System.Web.HttpUtility.ParseQueryString(auth.Headers.Location!.Query)["code"]!;
        var token = await _http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Callback, ["client_id"] = clientId, ["code_verifier"] = "wrong",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
        Assert.Contains("PKCE", await token.Content.ReadAsStringAsync());
    }
}
