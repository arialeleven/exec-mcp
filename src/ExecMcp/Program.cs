using System.Text.Json;
using ExecMcp.Host;
using ExecMcp.Plugins;
using ExecMcp.Sandbox;
using ExecMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var opts = CliArgs.Parse(args);
var env = HostEnv.Create(opts.Workspace, opts.Dev);

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
    // CLI subcommands only show warnings; the server logs its lifecycle too.
    .SetMinimumLevel(opts.Verbose ? LogLevel.Debug : opts.Rest.Length > 0 ? LogLevel.Warning : LogLevel.Information));
var log = loggerFactory.CreateLogger("exec-mcp");

if (opts.Rest.FirstOrDefault() == "plugin")
    return await PluginCli.RunAsync(opts.Rest, env, log);
if (opts.Rest.FirstOrDefault() == "tools")
    return await ScriptToolsCli.RunAsync(opts.Rest, env, log);
if (opts.Rest.FirstOrDefault() is "version" or "--version")
{
    Console.WriteLine($"exec-mcp {typeof(HostEnv).Assembly.GetName().Version?.ToString(3)}");
    return 0;
}
if (opts.Rest.FirstOrDefault() is "help" or "--help" or "-h")
{
    Console.WriteLine("usage: exec-mcp [--workspace DIR] [--dev] [--verbose]      run the MCP server on stdio");
    Console.WriteLine("       exec-mcp --http :9443 --external-url https://host:9443 [--tls-cert fullchain.pem --tls-key privkey.pem] [--anthropic-only]");
    Console.WriteLine("       exec-mcp tools list | new <name> [--lang sh|py|js|ps1] | run <name> ['{json}']");
    Console.WriteLine("       exec-mcp plugin list | approve <name> [--yes] | build <name|dir> | test <name|dir>");
    Console.WriteLine("       exec-mcp version");
    return 0;
}

var registry = new ToolRegistry(env, log);
var fileState = new FileState();
var bash = new BashTools(env, log);
var llm = new Llm(env, log);

// Core tools. The file/shell ones are also projected into the exec sandbox as core.<Name>.
var disabled = env.Config.Tools.Disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
void Core(HostTool tool, bool sandbox = false)
{
    if (disabled.Contains(tool.Name)) return;
    registry.Register(sandbox ? tool with { Namespace = "core", SandboxName = tool.Name } : tool);
}
Core(ReadTool.Create(env, fileState), sandbox: true);
Core(EditTools.Edit(env, fileState), sandbox: true);
Core(EditTools.MultiEdit(env, fileState), sandbox: true);
Core(EditTools.Write(env, fileState), sandbox: true);
Core(SearchTools.Glob(env), sandbox: true);
Core(SearchTools.Grep(env), sandbox: true);
Core(bash.Bash(), sandbox: true);
Core(bash.BashOutput(), sandbox: true);
Core(MiscTools.TodoWrite());
Core(TaskTool.Create(registry, llm));
// ToolSearch only makes sense once something is deferred (plugins, script tools).
Core(MiscTools.ToolSearch(registry) with { Visible = () => registry.Deferred().Count > 0 });
Core(ExecSandbox.Create(env, registry));

using var scriptTools = new ScriptTools(env, registry, log);   // keeps the folder watchers alive for the whole run
scriptTools.LoadAndWatch();

await using var plugins = new PluginManager(env, registry, llm, log);
await plugins.LoadAllAsync(allowStart: true, CancellationToken.None);

var instructions = Instructions.Build(env, registry, new ShellSession(env, log).ShellName, plugins.Notes());

void ConfigureMcp(IMcpServerBuilder mcp)
{
    mcp.Services.Configure<ModelContextProtocol.Server.McpServerOptions>(o =>
    {
        o.ServerInfo = new Implementation { Name = "exec-mcp", Version = "0.1.0" };
        o.ServerInstructions = instructions;
        o.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } };
    });
    mcp.WithListToolsHandler((req, ct) =>
        {
            registry.AttachServer(req.Server);
            return ValueTask.FromResult(new ListToolsResult { Tools = registry.Listed().Select(t => t.ToMcp()).ToList() });
        })
        .WithCallToolHandler(async (req, ct) =>
        {
            var p = req.Params!;
            var argsJson = p.Arguments is null ? JsonDocument.Parse("{}").RootElement : JsonSerializer.SerializeToElement(p.Arguments);
            var outcome = await registry.InvokeAsync(p.Name, argsJson, CallOrigin.Mcp, req.Server, p.ProgressToken, ct);
            return outcome.ToMcp();
        });
}

if (opts.Http is not null)
{
    if (opts.ExternalUrl is null) { Console.Error.WriteLine("--http needs --external-url https://host:port"); return 2; }
    await ExecMcp.Http.HttpHost.RunAsync(
        new ExecMcp.Http.HttpOptions(opts.Http, opts.ExternalUrl, opts.TlsCert, opts.TlsKey, opts.AnthropicOnly, opts.NoAuth, opts.ProxyProtocol),
        env, log, ConfigureMcp);
}
else
{
    var builder = Host.CreateApplicationBuilder([]);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    var mcp = builder.Services.AddMcpServer();
    ConfigureMcp(mcp);
    mcp.WithStdioServerTransport();
    await builder.Build().RunAsync();
}
bash.KillAll();
try { Directory.Delete(env.RunDir, recursive: true); } catch { }
return 0;

sealed record CliArgs(string? Workspace, bool Dev, bool Verbose, string[] Rest,
    string? Http, string? ExternalUrl, string? TlsCert, string? TlsKey, bool AnthropicOnly, bool NoAuth, bool ProxyProtocol)
{
    public static CliArgs Parse(string[] args)
    {
        string? ws = null, http = null, ext = null, cert = null, key = null;
        bool dev = false, verbose = false, anthropicOnly = false, noAuth = false, proxyProtocol = false;
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace" or "-w" when i + 1 < args.Length: ws = args[++i]; break;
                case "--dev": dev = true; break;
                case "--http" when i + 1 < args.Length: http = args[++i]; break;
                case "--external-url" when i + 1 < args.Length: ext = args[++i]; break;
                case "--tls-cert" when i + 1 < args.Length: cert = args[++i]; break;
                case "--tls-key" when i + 1 < args.Length: key = args[++i]; break;
                case "--anthropic-only": anthropicOnly = true; break;
                case "--no-auth": noAuth = true; break;
                case "--proxy-protocol": proxyProtocol = true; break;
                case "--verbose" or "-v": verbose = true; break;
                default: rest.Add(args[i]); break;
            }
        }
        return new CliArgs(ws, dev, verbose, rest.ToArray(), http, ext, cert, key, anthropicOnly, noAuth, proxyProtocol);
    }
}
