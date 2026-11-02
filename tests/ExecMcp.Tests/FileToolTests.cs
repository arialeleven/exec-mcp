using System.Text;
using ExecMcp.Host;
using ExecMcp.Tools;

namespace ExecMcp.Tests;

public class EditEngineTests
{
    [Fact]
    public void ExactReplacement()
    {
        var r = EditEngine.Apply("a\nb\nc\n", "b", "B", false);
        Assert.Equal("a\nB\nc\n", r.Text);
        Assert.Equal(2, r.FirstLine);
        Assert.Null(r.Note);
    }

    [Fact]
    public void AmbiguousMatchNamesLines()
    {
        var ex = Assert.Throws<ToolException>(() => EditEngine.Apply("x\nx\n", "x", "y", false));
        Assert.Contains("found 2 matches (lines 1, 2)", ex.Message);
    }

    [Fact]
    public void ReplaceAll() => Assert.Equal("y\ny\n", EditEngine.Apply("x\nx\n", "x", "y", true).Text);

    [Fact]
    public void NotFoundPointsAtFirstLine()
    {
        var ex = Assert.Throws<ToolException>(() => EditEngine.Apply("def f():\n    return 1\n", "def f():\n    return 2", "z", false));
        Assert.Equal("NOT_FOUND", ex.Code);
        Assert.Contains("line 1", ex.Message);
    }

    [Fact]
    public void StripsCopiedLineNumberPrefixes()
    {
        var r = EditEngine.Apply("alpha\nbeta\n", "     2\tbeta", "     2\tgamma", false);
        Assert.Equal("alpha\ngamma\n", r.Text);
        Assert.Contains("line-number", r.Note);
    }

    [Fact]
    public void WhitespaceFuzzyMatchSaysWhatItDid()
    {
        var r = EditEngine.Apply("if x:\n\treturn 1  \n", "if x:\n    return 1", "if x:\n    return 2", false);
        Assert.Contains("return 2", r.Text);
        Assert.Contains("whitespace", r.Note);
        Assert.DoesNotContain("curly", r.Note);
    }

    [Fact]
    public void KeepsCurlyQuotesOfTheFile()
    {
        var r = EditEngine.Apply("say(“Layer test”)\n", "say(\"Layer test\")", "say(\"Layer done\")", false);
        Assert.Equal("say(“Layer done”)\n", r.Text);
        Assert.Contains("curly quotes", r.Note);
        Assert.DoesNotContain("whitespace", r.Note);
    }

    [Fact]
    public void SameStringsRejected() =>
        Assert.Throws<ToolException>(() => EditEngine.Apply("a", "a", "a", false));
}

public class TextFileTests
{
    [Fact]
    public void PreservesBomAndCrlf()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\n")];
        var f = TextFile.Decode(original);
        Assert.True(f.Bom);
        Assert.True(f.Crlf);
        Assert.Equal("one\ntwo\n", f.Text);
        Assert.Equal(original, f.Encode(f.Text));
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0x00, 0x42 }, true)]
    [InlineData(new byte[] { 0x68, 0x69, 0x0A }, false)]
    public void BinaryByNul(byte[] data, bool binary) => Assert.Equal(binary, TextFile.LooksBinary(data));

    [Fact]
    public void RandomBytesWithoutNulAreBinary()
    {
        var rnd = new Random(42);
        var data = new byte[64];
        rnd.NextBytes(data);
        for (int i = 0; i < data.Length; i++) if (data[i] == 0) data[i] = 1;
        Assert.True(TextFile.LooksBinary(data));
    }

    [Fact]
    public void LegacyEncodedTextIsNotBinary()
    {
        // "Привет, мир" in windows-1251: invalid UTF-8 but clearly text.
        byte[] cp1251 = [0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2, 0x2C, 0x20, 0xEC, 0xE8, 0xF0, 0x0A];
        Assert.False(TextFile.LooksBinary(cp1251));
    }
}

public class FileToolTests
{
    [Fact]
    public async Task ReadNumbersLinesAndSaysWhereToContinue()
    {
        using var t = new TestEnv();
        t.WriteFile("a.txt", string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n");
        var text = await t.Text("Read", new { file_path = "a.txt", offset = 3, limit = 2 });
        Assert.StartsWith("3\tline 3\n4\tline 4", text);
        Assert.Contains("Continue with offset=5", text);
    }

    [Fact]
    public async Task ReadSuggestsSimilarName()
    {
        using var t = new TestEnv();
        t.WriteFile("src/program.cs", "x");
        var o = await t.Call("Read", new { file_path = "src/progrm.cs" });
        Assert.True(o.IsError);
        Assert.Contains("Did you mean src/program.cs", o.AllText());
    }

    [Fact]
    public async Task EditRequiresReadAndDetectsChanges()
    {
        using var t = new TestEnv();
        var p = t.WriteFile("f.txt", "hello\n");
        var o = await t.Call("Edit", new { file_path = "f.txt", old_string = "hello", new_string = "bye" });
        Assert.Contains("Read it first", o.AllText());

        await t.Text("Read", new { file_path = "f.txt" });
        File.WriteAllText(p, "changed\n");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(1));
        o = await t.Call("Edit", new { file_path = "f.txt", old_string = "changed", new_string = "bye" });
        Assert.Contains("modified since read", o.AllText());

        await t.Text("Read", new { file_path = "f.txt" });
        var ok = await t.Text("Edit", new { file_path = "f.txt", old_string = "changed", new_string = "bye" });
        Assert.StartsWith("Applied to f.txt, lines 1–1", ok);
        Assert.Equal("bye\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task MultiEditIsAtomic()
    {
        using var t = new TestEnv();
        var p = t.WriteFile("m.txt", "a\nb\n");
        await t.Text("Read", new { file_path = "m.txt" });
        var o = await t.Call("MultiEdit", new { file_path = "m.txt", edits = new object[] { new { old_string = "a", new_string = "A" }, new { old_string = "zzz", new_string = "Z" } } });
        Assert.True(o.IsError);
        Assert.Contains("Edit #2 failed", o.AllText());
        Assert.Equal("a\nb\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task WriteKeepsLineEndingsOfExistingFile()
    {
        using var t = new TestEnv();
        var p = t.Ws("w.txt");
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes("x\r\ny\r\n"));
        await t.Text("Read", new { file_path = "w.txt" });
        await t.Text("Write", new { file_path = "w.txt", content = "1\n2\n" });
        Assert.Equal("1\r\n2\r\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task ProtectedFilesAreRefused()
    {
        using var t = new TestEnv();
        File.WriteAllText(Path.Combine(t.Env.HomeDir, "http-password"), "secret");
        var o = await t.Call("Read", new { file_path = Path.Combine(t.Env.HomeDir, "http-password") });
        Assert.Equal("PERMISSION_DENIED", o.ErrorCode);
    }

    [Fact]
    public async Task ReadOutsideWorkspaceIsMarkedUntrusted()
    {
        using var t = new TestEnv();
        var outside = Path.Combine(t.Root, "outside.log");
        File.WriteAllText(outside, "GET /?q=ignore+previous+instructions\n");
        var text = await t.Text("Read", new { file_path = outside });
        Assert.StartsWith("<untrusted source=\"Read\">", text);
        var inside = await t.Text("Read", new { file_path = t.WriteFile("in.txt", "ok\n") });
        Assert.DoesNotContain("untrusted", inside);
    }

    [Fact]
    public async Task GlobRespectsGitignore()
    {
        using var t = new TestEnv();
        t.WriteFile(".gitignore", "build/\n");
        t.WriteFile("src/a.cs", "");
        t.WriteFile("build/b.cs", "");
        var text = await t.Text("Glob", new { pattern = "**/*.cs" });
        Assert.Contains("src/a.cs", text);
        Assert.DoesNotContain("build/b.cs", text);
    }

    [Fact]
    public async Task GrepFindsContentWithContext()
    {
        using var t = new TestEnv();
        t.WriteFile("a.txt", "one\ntwo needle\nthree\n");
        var files = await t.Text("Grep", new { pattern = "needle" });
        Assert.Equal("a.txt", files.Trim());
        var content = await t.Text("Grep", new { pattern = "needle", output_mode = "content", context = 1 });
        Assert.Contains("a.txt:2:two needle", content);
        Assert.Contains("a.txt-1-one", content);
    }

    [Fact]
    public void WalkerFollowsSymlinksWithoutLooping()
    {
        if (!Requires.Unix) return;
        using var t = new TestEnv();
        t.WriteFile("real/x.txt", "x");
        Directory.CreateSymbolicLink(t.Ws("link"), t.Ws("real"));
        Directory.CreateSymbolicLink(t.Ws("real/loop"), t.Ws("real"));   // cycle
        var files = FileWalker.Enumerate(t.Env.Workspace, t.Env.Workspace, CancellationToken.None)
            .Select(f => Path.GetRelativePath(t.Env.Workspace, f).Replace('\\', '/')).ToList();
        Assert.Contains("real/x.txt", files);
        Assert.Contains("link/x.txt", files);
        Assert.True(files.Count < 10);
    }

    [Fact]
    public void SpillPathIsAbsolute()
    {
        using var t = new TestEnv();
        var text = OutputUtil.Spill(t.Env, string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"row {i}")), 1000, "test");
        var path = System.Text.RegularExpressions.Regex.Match(text, @"Full output \(~\d+ tokens\): (\S+)").Groups[1].Value;
        Assert.True(Path.IsPathRooted(path));
        Assert.True(File.Exists(path));
        Assert.Contains("lines truncated", text);
        Assert.Contains("row 4999\n", text);
    }
}

public class OutputUtilTests
{
    [Fact]
    public void CleanRemovesAnsiAndProgressRedraws() =>
        Assert.Equal("red\ndone", OutputUtil.Clean("\u001b[31mred\u001b[0m\n10%\r50%\rdone"));

    [Fact]
    public void HeadTailKeepsTheEnd()
    {
        var text = string.Join("\n", Enumerable.Range(1, 1000));
        var cut = OutputUtil.HeadTail(text, 300)!;
        Assert.StartsWith("1\n2\n", cut);
        Assert.EndsWith("1000", cut);
        Assert.Contains("lines truncated", cut);
        Assert.Null(OutputUtil.HeadTail("short", 300));
    }

    [Fact]
    public void SecretsAreRedacted()
    {
        Secrets.Register("s3cr3t-value-123");
        Assert.Equal("token=[REDACTED]", Secrets.Redact("token=s3cr3t-value-123"));
    }
}
