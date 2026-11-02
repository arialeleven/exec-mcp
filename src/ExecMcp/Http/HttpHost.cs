using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ExecMcp.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExecMcp.Http;

public sealed record HttpOptions(
    string Listen,              // ":9443", "0.0.0.0:9443", "[::]:9443"
    string ExternalUrl,         // https://host:port as clients see it
    string? TlsCert,
    string? TlsKey,
    bool AnthropicOnly,         // /mcp, /token, /register only from Anthropic egress + loopback
    bool NoAuth,
    bool ProxyProtocol = false);  // accept a PROXY v1 header from a local TCP router (sni-router)

/// <summary>Remote mode: MCP Streamable HTTP at /mcp behind OAuth, for claude.ai custom connectors.</summary>
public static class HttpHost
{
    // https://platform.claude.com/docs/en/api/ip-addresses — outbound (MCP tool calls) is IPv4 only.
    static readonly (IPAddress Net, int Bits)[] AnthropicEgress = [(IPAddress.Parse("160.79.104.0"), 21)];

    public static async Task RunAsync(HttpOptions o, HostEnv env, ILogger log, Action<IMcpServerBuilder> configureMcp)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Connection-level diagnostics: who connects, and why a TLS handshake failed.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel.Connections", LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel.Https", LogLevel.Debug);

        var (host, port) = ParseListen(o.Listen);
        builder.WebHost.ConfigureKestrel(k =>
        {
            void Configure(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions lo)
            {
                if (o.ProxyProtocol) lo.Use(next => ctx => ProxyProtocolV1(ctx, next, log));
                if (o.TlsCert is not null)
                {
                    var certs = new ReloadingCert(o.TlsCert, o.TlsKey!, log);
                    // Send fullchain.pem exactly as issued (including cross-signs) instead of a chain
                    // rebuilt from the local trust store, so clients with older root stores can verify it.
                    lo.UseHttps(new TlsHandshakeCallbackOptions
                    {
                        OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = certs.Get(),
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                        }),
                    });
                }
            }
            if (host is null) k.ListenAnyIP(port, Configure);   // dual-stack: IPv4 and IPv6
            else k.Listen(IPAddress.Parse(host), port, Configure);
        });

        var mcp = builder.Services.AddMcpServer(s => s.ServerInfo = new() { Name = "exec-mcp", Version = "0.1.0" });
        configureMcp(mcp);
        // Sessions for initialize-handshake clients (list_changed, elicitation); 2026-07-28+ clients run statelessly.
#pragma warning disable MCP9006 // stateful sessions are kept on purpose for clients on the 2025-11-25 revision (claude.ai today)
        mcp.WithHttpTransport(t =>
        {
            t.SessionMode = ModelContextProtocol.AspNetCore.HttpServerSessionMode.StatefulForInitializeClients;
            t.IdleTimeout = TimeSpan.FromHours(2);
        });
#pragma warning restore MCP9006

        var app = builder.Build();

        var password = LoadOrCreatePassword(env, log);
        var oauth = new OAuthServer(o.ExternalUrl, password, Path.Combine(env.HomeDir, "oauth-state.json"), log);

        app.Use(async (ctx, next) =>
        {
            await next();
            log.LogInformation("{Ip} {Method} {Path} {Status} ua={Agent}", ctx.Connection.RemoteIpAddress, ctx.Request.Method,
                ctx.Request.Path.Value, ctx.Response.StatusCode, ctx.Request.Headers.UserAgent.ToString());
        });

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            bool machine = path.StartsWith("/mcp") || path is "/token" or "/register";
            if (o.AnthropicOnly && machine && !FromAllowedNetwork(ctx.Connection.RemoteIpAddress))
            {
                log.LogWarning("rejected {Path} from {Ip} (not Anthropic egress)", path, ctx.Connection.RemoteIpAddress);
                ctx.Response.StatusCode = 403;
                return;
            }
            if (path.StartsWith("/mcp") && !o.NoAuth && !oauth.ValidateBearer(ctx))
            {
                await oauth.Challenge(ctx);
                return;
            }
            await next();
        });

        if (!o.NoAuth) oauth.Map(app);
        app.MapGet("/", () => Results.Text("exec-mcp: MCP endpoint is /mcp\n"));
        app.MapMcp("/mcp");

        log.LogInformation("exec-mcp listening on {Listen}; connector URL: {Url}/mcp", o.Listen, o.ExternalUrl.TrimEnd('/'));
        await app.RunAsync();
    }

    /// <summary>
    /// Reads "PROXY TCP4|TCP6 src dst sport dport\r\n" sent by a local router and restores the real client address.
    /// The header is honoured only on loopback connections, so remote clients cannot spoof their address.
    /// </summary>
    static async Task ProxyProtocolV1(Microsoft.AspNetCore.Connections.ConnectionContext ctx,
        Microsoft.AspNetCore.Connections.ConnectionDelegate next, ILogger log)
    {
        var remote = (ctx.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            await next(ctx);
            return;
        }
        var input = ctx.Transport.Input;
        while (true)
        {
            var result = await input.ReadAsync(ctx.ConnectionClosed);
            var buf = result.Buffer;
            var span = buf.Length > 108 ? buf.Slice(0, 108) : buf;
            var text = System.Text.Encoding.ASCII.GetString(span.ToArray());
            var prefix = text[..Math.Min(6, text.Length)];
            if (prefix.Length > 0 && !"PROXY ".StartsWith(prefix, StringComparison.Ordinal))
            {
                input.AdvanceTo(buf.Start);   // no header: a direct local connection
                break;
            }
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                if (result.IsCompleted || buf.Length >= 108) { ctx.Abort(); return; }
                input.AdvanceTo(buf.Start, buf.End);
                continue;
            }
            var line = text[..end];
            if (ParseProxyHeader(line) is { } ep) ctx.RemoteEndPoint = ep;
            else if (!line.StartsWith("PROXY UNKNOWN", StringComparison.Ordinal)) log.LogWarning("bad PROXY header: {Header}", line);
            input.AdvanceTo(buf.GetPosition(end + 2));
            break;
        }
        await next(ctx);
    }

    /// <summary>"PROXY TCP4 1.2.3.4 5.6.7.8 1234 443" → 1.2.3.4:1234; null for UNKNOWN or malformed lines.</summary>
    public static IPEndPoint? ParseProxyHeader(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 6 || parts[0] != "PROXY" || parts[1] is not ("TCP4" or "TCP6")) return null;
        if (!IPAddress.TryParse(parts[2], out var src) || !int.TryParse(parts[4], out var port) || port is < 0 or > 65535) return null;
        if ((parts[1] == "TCP4") != (src.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) return null;
        return new IPEndPoint(src, port);
    }

    public static bool IsAnthropicEgress(IPAddress? ip) => FromAllowedNetwork(ip) && !IPAddress.IsLoopback(ip!.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    static (string? Host, int Port) ParseListen(string listen)
    {
        var i = listen.LastIndexOf(':');
        var hostPart = i > 0 ? listen[..i].Trim('[', ']') : null;
        return (string.IsNullOrEmpty(hostPart) ? null : hostPart, int.Parse(listen[(i + 1)..]));
    }

    static bool FromAllowedNetwork(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        return AnthropicEgress.Any(n => InNetwork(ip, n.Net, n.Bits));
    }

    static bool InNetwork(IPAddress ip, IPAddress net, int bits)
    {
        var a = ip.GetAddressBytes();
        var b = net.GetAddressBytes();
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    /// <summary>EXEC_MCP_PASSWORD, or a generated one stored in ~/.exec-mcp/http-password (mode 600).</summary>
    static string LoadOrCreatePassword(HostEnv env, ILogger log)
    {
        var fromEnv = Environment.GetEnvironmentVariable("EXEC_MCP_PASSWORD");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        var file = Path.Combine(env.HomeDir, "http-password");
        if (File.Exists(file)) return File.ReadAllText(file).Trim();
        var pw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'x').Replace('/', 'y');
        Directory.CreateDirectory(env.HomeDir);
        File.WriteAllText(file, pw + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        log.LogWarning("generated owner password in {File}", file);
        return pw;
    }

    /// <summary>Re-reads the PEM files when certbot renews them.</summary>
    sealed class ReloadingCert(string certPath, string keyPath, ILogger log)
    {
        SslStreamCertificateContext? _ctx;
        DateTime _stamp;

        public SslStreamCertificateContext Get()
        {
            var stamp = File.GetLastWriteTimeUtc(certPath);
            if (_ctx is not null && stamp == _stamp) return _ctx;
            try
            {
                using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                var leaf = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
                var all = new X509Certificate2Collection();
                all.ImportFromPemFile(certPath);
                var chain = new X509Certificate2Collection(all.Cast<X509Certificate2>().Where(c => c.Thumbprint != leaf.Thumbprint).ToArray());
                _ctx = SslStreamCertificateContext.Create(leaf, chain, offline: true);
                _stamp = stamp;
                log.LogInformation("TLS certificate loaded: {Subject}, valid until {NotAfter:u}, {N} chain certs", leaf.Subject, leaf.NotAfter, chain.Count);
            }
            catch (Exception ex) when (_ctx is not null)
            {
                log.LogWarning(ex, "TLS reload failed, keeping the old certificate");
            }
            return _ctx!;
        }
    }
}
