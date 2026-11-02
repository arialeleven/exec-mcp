using ExecMcp.Host;
using ExecMcp.Tools;

namespace ExecMcp.Tests;

public class BashPolicyTests
{
    static readonly BashPolicy Defaults = new();

    [Theory]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("rm -fr /tmp/x")]
    [InlineData("rm -r -f /tmp/x")]
    [InlineData("rm --recursive --force /tmp/x")]
    [InlineData("/bin/rm -Rf /tmp/x")]
    [InlineData("sudo rm -rf /tmp/x")]
    [InlineData("echo ok && rm -rf build")]
    [InlineData("timeout 5 rm -rf build")]
    [InlineData("find /tmp -name '*.log' -delete")]
    [InlineData("find . -exec rm {} \\;")]
    [InlineData("git push origin +main")]
    [InlineData("git push --force-with-lease")]
    [InlineData("FOO=1 curl https://example.com")]
    public void DangerousFormsNeedConfirmation(string cmd) =>
        Assert.Equal("ask", BashPolicyCheck.Evaluate(cmd, Defaults).Action);

    [Theory]
    [InlineData("ls -la")]
    [InlineData("rm file.txt")]
    [InlineData("echo 'rm -rf /' > notes.txt")]
    [InlineData("grep -r 'curl' src")]
    [InlineData("git push origin main")]
    public void HarmlessCommandsRun(string cmd) =>
        Assert.Equal("allow", BashPolicyCheck.Evaluate(cmd, Defaults).Action);

    [Fact]
    public void DenyWinsOverAllow()
    {
        var p = new BashPolicy { Allow = ["git"], Deny = ["git push"] };
        Assert.Equal("deny", BashPolicyCheck.Evaluate("git status && git push", p).Action);
    }

    [Fact]
    public void AllowlistModeAsksForEverythingElse()
    {
        var p = new BashPolicy { DefaultAction = "ask", Ask = [], Allow = ["ls", "cat"] };
        Assert.Equal("allow", BashPolicyCheck.Evaluate("ls | cat", p).Action);
        Assert.Equal("ask", BashPolicyCheck.Evaluate("ls; whoami", p).Action);
    }
}

public class BashToolTests
{
    [Fact]
    public async Task SessionStatePersists()
    {
        if (!Requires.Unix) return;
        using var t = new TestEnv();
        Directory.CreateDirectory(t.Ws("sub"));
        await t.Text("Bash", new { command = "cd sub && export FOO=bar && greet() { echo hi-$1; }" });
        var text = await t.Text("Bash", new { command = "pwd; echo $FOO; greet x" });
        Assert.Contains("/sub", text);
        Assert.Contains("bar", text);
        Assert.Contains("hi-x", text);
    }

    [Fact]
    public async Task TimeoutKillsCommandButKeepsSession()
    {
        if (!Requires.Unix) return;
        using var t = new TestEnv();
        await t.Text("Bash", new { command = "export KEEP=1" });
        var o = await t.Call("Bash", new { command = "sleep 30", timeout_ms = 1000 });
        Assert.Equal("TIMEOUT", o.ErrorCode);
        Assert.Contains("session state kept", o.AllText());
        Assert.Contains("1", await t.Text("Bash", new { command = "echo $KEEP" }));
    }

    [Fact]
    public async Task ExitCodeAndStderrAreReported()
    {
        if (!Requires.Unix) return;
        using var t = new TestEnv();
        var text = await t.Text("Bash", new { command = "echo out; echo err >&2; exit 3" });
        Assert.Contains("out", text);
        Assert.Contains("err", text);
        Assert.Contains("Exit code 3", text);
    }

    [Fact]
    public async Task BackgroundShell()
    {
        if (!Requires.Unix) return;
        using var t = new TestEnv();
        var start = await t.Text("Bash", new { command = "echo started; sleep 0.3; echo finished", run_in_background = true });
        var id = System.Text.RegularExpressions.Regex.Match(start, @"shell_id=(\w+)").Groups[1].Value;
        await Task.Delay(1500);
        var output = await t.Text("BashOutput", new { shell_id = id });
        Assert.Contains("exited with code 0", output);
        Assert.Contains("finished", output);
    }

    [Fact]
    public async Task AskWithoutElicitationDeniesByDefault()
    {
        if (!Requires.Unix) return;
        using var t = new TestEnv();
        var o = await t.Call("Bash", new { command = "rm -rf ./nothing-here" });
        Assert.Equal("PERMISSION_DENIED", o.ErrorCode);
        Assert.Contains("askWithoutElicitation", o.AllText());

        using var t2 = new TestEnv(c => c.Bash.AskWithoutElicitation = "allow");
        Assert.False((await t2.Call("Bash", new { command = "rm -rf ./nothing-here" })).IsError);
    }

    [Fact]
    public async Task OutputIsMarkedUntrusted()
    {
        if (!Requires.Unix) return;
        using var t = new TestEnv();
        Assert.StartsWith("<untrusted source=\"Bash\">", await t.Text("Bash", new { command = "echo hi" }));
    }
}
