using System.Text.Json;
using System.Text.Json.Nodes;
using ExecMcp.Host;
using ExecMcp.Plugins;
using ExecMcp.Sandbox;

namespace ExecMcp.Tests;

public class SandboxTests
{
    [Fact]
    public async Task CoreNamespaceAndHelp()
    {
        using var t = new TestEnv();
        t.WriteFile("a.txt", "hello\n");
        var text = await t.Text("exec", new { code = "const r = await core.Read({file_path: 'a.txt'}); console.log(r.includes('hello'));\nconsole.log(help('core').includes('function Read('))" });
        Assert.Equal("true\ntrue", text);
    }

    [Fact]
    public async Task BridgeIsNotAGlobal()
    {
        using var t = new TestEnv();
        Assert.Equal("undefined undefined", await t.Text("exec", new { code = "console.log(typeof __call, typeof call)" }));
    }

    [Fact]
    public async Task HostErrorsAreCatchableAndOutputIsKept()
    {
        using var t = new TestEnv();
        var text = await t.Text("exec", new { code = """
            console.log("before");
            try { await core.Read({file_path: "missing.txt"}); } catch (e) { console.log("caught", e.message.includes("NOT_FOUND")); }
            try { await core.Read({nope: {deep: [1, 2]}}); } catch (e) { console.log("caught2", e.message.includes("INVALID_INPUT")); }
            """ });
        Assert.Equal("before\ncaught true\ncaught2 true", text);

        var o = await t.Call("exec", new { code = "console.log('kept'); throw new Error('boom')" });
        Assert.True(o.IsError);
        Assert.StartsWith("kept\n", o.AllText());
    }

    [Fact]
    public async Task LimitsAndIsolation()
    {
        using var t = new TestEnv(c => c.Sandbox.MemoryMb = 64);
        Assert.Equal("TIMEOUT", (await t.Call("exec", new { code = "while (true) {}", timeout_ms = 300 })).ErrorCode);
        Assert.Contains("memory limit", (await t.Call("exec", new { code = "const a = []; while (true) a.push('x'.repeat(1e6));" })).AllText());
        Assert.Equal("undefined undefined undefined", await t.Text("exec", new { code = "console.log(typeof require, typeof process, typeof fetch)" }));
    }

    [Fact]
    public async Task DestructiveCallsFromSandboxNeedConfirmation()
    {
        using var t = new TestEnv();
        var o = await t.Call("exec", new { code = "await core.Write({file_path: 'x.txt', content: 'x'})" });
        Assert.True(o.IsError);
        Assert.Contains("PERMISSION_DENIED", o.AllText());
        Assert.False(File.Exists(t.Ws("x.txt")));
    }

    [Fact]
    public void TypescriptForSchemas()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"array","items":{"type":"integer"}},"c":{"enum":["x","y"]}},"required":["a"]}""");
        Assert.Equal("{ a: string; b?: number[]; c?: \"x\" | \"y\" }", Dts.TypeOf(schema));
        Assert.Equal("{}", Dts.TypeOf(JsonNode.Parse("""{"type":"object","properties":{}}""")));
    }
}

public class RegistryTests
{
    [Fact]
    public async Task UnknownToolSuggestsNearestName()
    {
        using var t = new TestEnv();
        var o = await t.Call("Raed", new { });
        Assert.Equal("NOT_FOUND", o.ErrorCode);
        Assert.Contains("Did you mean: Read", o.AllText());
    }

    [Fact]
    public async Task SchemaErrorsAreShort()
    {
        using var t = new TestEnv();
        var o = await t.Call("Read", new { file_path = 5 });
        Assert.Equal("INVALID_INPUT", o.ErrorCode);
        Assert.Contains("file_path", o.AllText());
        Assert.DoesNotContain("did not match the required schema", o.AllText());
    }

    [Fact]
    public async Task RetryableErrorsOfReadOnlyToolsAreRetriedOnce()
    {
        using var t = new TestEnv();
        int calls = 0;
        t.Registry.Register(new HostTool
        {
            Name = "flaky",
            Description = "d",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
            Annotations = new() { ReadOnlyHint = true },
            Handler = _ => Task.FromResult(++calls == 1 ? ToolOutcome.Error("down", "UPSTREAM", retryable: true, retryAfterMs: 10) : ToolOutcome.Text("up")),
        });
        Assert.Equal("up", await t.Text("flaky", new { }));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ToolSearchHiddenUntilSomethingIsDeferred()
    {
        using var t = new TestEnv();
        t.Registry.Register(t.Registry.Find("ToolSearch")! with { Visible = () => t.Registry.Deferred().Count > 0 });
        Assert.DoesNotContain(t.Registry.Listed(), x => x.Name == "ToolSearch");
        t.Registry.ReplacePluginTools("p", [new HostTool
        {
            Name = "p_x", Description = "d", Exposure = Exposure.Deferred, Plugin = "p",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
            Handler = _ => Task.FromResult(ToolOutcome.Text("x")),
        }]);
        Assert.Contains(t.Registry.Listed(), x => x.Name == "ToolSearch");
        Assert.DoesNotContain(t.Registry.Listed(), x => x.Name == "p_x");
        t.Registry.Activate(["p_x"]);
        Assert.Contains(t.Registry.Listed(), x => x.Name == "p_x");
    }

    [Fact]
    public void CoreToolsComeFirstInStableOrder()
    {
        using var t = new TestEnv();
        var before = t.Registry.All().Select(x => x.Name).ToList();
        t.Registry.ReplacePluginTools("z", []);
        Assert.Equal(before, t.Registry.All().Select(x => x.Name).ToList());
    }
}

public class ManifestTests
{
    [Theory]
    [InlineData("0.1.0", "^0.1", true)]
    [InlineData("0.2.0", "^0.1", false)]
    [InlineData("1.4.0", "^1.2", true)]
    [InlineData("2.0.0", "^1.2", false)]
    [InlineData("0.1.5", "~0.1", true)]
    [InlineData("0.1.0", ">=0.1", true)]
    [InlineData("0.1.0", "*", true)]
    public void SemverRanges(string version, string range, bool ok) => Assert.Equal(ok, SemVer.Satisfies(version, range));

    [Fact]
    public void ValidatesManifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exec-mcp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "name": "Bad_Name", "version": "x", "apiVersion": "^9", "runtime": { "type": "cobol" },
              "permissions": { "net": "*", "exec": ["/bin/sh"] }, "exposure": { "mcp": "sometimes" } }
            """);
        var m = PluginManifest.Load(dir);
        var errors = string.Join("\n", m.Validate());
        foreach (var expected in new[] { "name:", "version:", "apiVersion:", "runtime.type", "exposure.mcp", "permissions.exec" })
            Assert.Contains(expected, errors);
        Assert.Equal(["*"], m.Permissions.Net.Allow);
        Directory.Delete(dir, true);
    }
}
