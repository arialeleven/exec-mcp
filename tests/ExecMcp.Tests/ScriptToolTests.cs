using System.Text.Json;
using ExecMcp.Host;
using ExecMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExecMcp.Tests;

public class ScriptHeaderTests
{
    const string Sample = """
        #!/usr/bin/env bash
        # @tool disk_usage
        # @description Disk usage of a directory,
        # @description largest entries first.
        # @param path string Directory to measure
        # @param depth integer? =2 How deep
        # @param unit enum(K|M|G)? =M Size unit
        # @param tags string[]? Labels
        # @param dry boolean =false Only print
        # @readonly
        # @timeout 30s
        du -h "$path"
        """;

    [Fact]
    public void ParsesParametersTypesAndDefaults()
    {
        var (spec, error) = ScriptHeader.Parse("/x/disk_usage.sh", Sample);
        Assert.Null(error);
        Assert.NotNull(spec);
        Assert.Equal("disk_usage", spec.Name);
        Assert.Equal("Disk usage of a directory, largest entries first.", spec.Description);
        Assert.Equal(["bash"], spec.Command);
        Assert.Equal(TimeSpan.FromSeconds(30), spec.Timeout);
        Assert.True(spec.Annotations.ReadOnlyHint);
        Assert.False(spec.Annotations.DestructiveHint);

        var props = spec.InputSchema["properties"]!.AsObject();
        Assert.Equal("integer", props["depth"]!["type"]!.GetValue<string>());
        Assert.Equal(2, props["depth"]!["default"]!.GetValue<long>());
        Assert.Equal("M", props["unit"]!["default"]!.GetValue<string>());
        Assert.Equal(3, props["unit"]!["enum"]!.AsArray().Count);
        Assert.Equal("array", props["tags"]!["type"]!.GetValue<string>());
        Assert.False(props["dry"]!["default"]!.GetValue<bool>());
        Assert.Equal(["path"], spec.InputSchema["required"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Theory]
    [InlineData("# @tool t\n# @param x strng bad\n", "unknown type")]
    [InlineData("# @tool t\n", "@description is required")]
    [InlineData("# @tool bad-name!\n# @description d\n", "must match")]
    [InlineData("# @tool t\n# @description d\n# @param n integer =abc x\n", "does not match its type")]
    [InlineData("# @tool t\n# @description d\n# @readonly\n# @destructive\n", "exclude each other")]
    public void ReportsHeaderErrors(string text, string expected)
    {
        var (_, error) = ScriptHeader.Parse("/x/t.sh", text);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void FilesWithoutToolDirectiveAreIgnored()
    {
        var (spec, error) = ScriptHeader.Parse("/x/helper.sh", "#!/bin/sh\necho helper\n");
        Assert.Null(spec);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("x.py", "", "python")]
    [InlineData("x.mjs", "", "node")]
    [InlineData("x", "#!/usr/bin/env -S deno run", "deno")]
    [InlineData("x", "#!/bin/bash -e", "bash")]
    public void InterpreterFromShebangOrExtension(string file, string firstLine, string expected) =>
        Assert.Contains(expected, string.Join(' ', ScriptHeader.Interpreter(file, firstLine)!));

    [Fact]
    public void DefaultsToDestructiveWhenUnmarked()
    {
        var (spec, _) = ScriptHeader.Parse("/x/t.sh", "#!/bin/sh\n// @tool t\n// @description d\n");
        Assert.True(spec!.Annotations.DestructiveHint);
    }
}

public class ScriptToolRunTests
{
    static async Task<(TestEnv Env, ScriptTools Tools)> Setup(params (string Name, string Body)[] files)
    {
        var t = new TestEnv();
        var dir = Path.Combine(t.Env.HomeDir, "tools");
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files) await File.WriteAllTextAsync(Path.Combine(dir, name), body);
        var tools = new ScriptTools(t.Env, t.Registry, NullLogger.Instance);
        tools.Reload();
        return (t, tools);
    }

    [Fact]
    public async Task ParametersArriveAsEnvAndStdin()
    {
        if (!Requires.Unix) return;
        var (t, tools) = await Setup(("greet.sh", """
            #!/usr/bin/env bash
            # @tool greet
            # @description Greets.
            # @param name string Who
            # @param times integer? =2 How often
            # @readonly
            for i in $(seq 1 "$times"); do echo "hi $name"; done
            echo "stdin: $(cat)"
            """));
        using var _ = t;
        var text = await t.Text("greet", new { name = "Ann" });
        Assert.Contains("hi Ann\nhi Ann\n", text);
        Assert.Contains("\"times\":2", text);
    }

    [Fact]
    public async Task JsonOutputBecomesStructured()
    {
        if (!Requires.OnPath("python3")) return;
        var (t, tools) = await Setup(("count.py", """
            #!/usr/bin/env python3
            # @tool count
            # @description Counts words.
            # @param text string Text
            # @readonly
            # @output json
            import json, sys
            print(json.dumps({"words": len(json.load(sys.stdin)["text"].split())}))
            """));
        using var _ = t;
        var o = await t.Call("count", new { text = "a b c" });
        Assert.False(o.IsError, o.AllText());
        Assert.Equal(3, o.Structured!["words"]!.GetValue<int>());
    }

    [Fact]
    public async Task FailureTimeoutAndValidation()
    {
        if (!Requires.Unix) return;
        var (t, tools) = await Setup(
            ("fail.sh", "#!/bin/sh\n# @tool fail\n# @description Fails.\n# @readonly\necho boom >&2\nexit 7\n"),
            ("slow.sh", "#!/bin/sh\n# @tool slow\n# @description Slow.\n# @readonly\n# @timeout 300ms\nsleep 5\n"),
            ("needs.sh", "#!/bin/sh\n# @tool needs\n# @description Needs x.\n# @param x integer X\n# @readonly\necho $x\n"));
        using var _ = t;
        var fail = await t.Call("fail", new { });
        Assert.True(fail.IsError);
        Assert.Contains("exit code 7", fail.AllText());
        Assert.Contains("boom", fail.AllText());
        Assert.Equal("TIMEOUT", (await t.Call("slow", new { })).ErrorCode);
        Assert.Equal("INVALID_INPUT", (await t.Call("needs", new { x = "nope" })).ErrorCode);
    }

    [Fact]
    public async Task NameClashesWithCoreAreRejected()
    {
        var (t, tools) = await Setup(("read.sh", "#!/bin/sh\n# @tool Read\n# @description Shadow.\n"));
        using var _ = t;
        Assert.Contains(tools.Errors, e => e.Error.Contains("already taken"));
        Assert.Null(t.Registry.Find("Read")!.Plugin);   // the core tool is untouched
    }

    [Fact]
    public async Task HotReloadPicksUpNewFiles()
    {
        if (!Requires.Unix) return;
        var (t, tools) = await Setup();
        using var _ = t;
        using var __ = tools;
        tools.LoadAndWatch();
        Assert.Null(t.Registry.Find("later"));
        await File.WriteAllTextAsync(Path.Combine(t.Env.HomeDir, "tools", "later.sh"), "#!/bin/sh\n# @tool later\n# @description Added later.\n# @readonly\necho late\n");
        for (int i = 0; i < 50 && t.Registry.Find("later") is null; i++) await Task.Delay(100);
        Assert.Equal("late", (await t.Text("later", new { })).Trim());
    }

    [Fact]
    public async Task ScriptToolsAreInTheSandbox()
    {
        if (!Requires.Unix) return;
        var (t, tools) = await Setup(("up.sh", "#!/bin/sh\n# @tool up\n# @description Upper-cases.\n# @param s string S\n# @readonly\nprintf '%s' \"$s\" | tr a-z A-Z\n"));
        using var _ = t;
        var text = await t.Text("exec", new { code = "console.log(await tools.up({s: 'abc'}))" });
        Assert.Equal("ABC", text.Trim());
    }
}
