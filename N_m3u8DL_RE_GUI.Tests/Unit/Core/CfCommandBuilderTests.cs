#nullable enable
using System.Linq;
using N_m3u8DL_RE_GUI.Core;
using Xunit;

namespace N_m3u8DL_RE_GUI.Tests.Unit.Core;

public class CfCommandBuilderTests
{
    private static CfCommandOptions Sample(string url = "https://example.com/a.m3u8") => new(
        PythonExe: "python",
        ScriptPath: @"C:\App\m3u8_cf_bypass.py",
        Url: url,
        OutputName: "video.mp4",
        WorkDir: @"C:\Save",
        SegDir: @"C:\App\cf_segments",
        Referer: "https://example.com/",
        Cookie: "",
        Impersonate: "chrome",
        ThreadCount: 12,
        KeepSegments: false);

    [Fact]
    public void BuildCommand_ShouldQuoteEveryPathArgument()
    {
        var cmd = CfCommandBuilder.BuildCommand(Sample());

        Assert.Contains("\"python\"", cmd);
        Assert.Contains("\"C:\\App\\m3u8_cf_bypass.py\"", cmd);
        Assert.Contains("-o \"video.mp4\"", cmd);
        Assert.Contains("--work-dir \"C:\\Save\"", cmd);
        Assert.Contains("--impersonate \"chrome\"", cmd);
        Assert.Contains("--thread-count \"12\"", cmd);
    }

    [Fact]
    public void BuildCommand_ShouldOmitCookieWhenEmpty()
    {
        Assert.DoesNotContain("--cookie", CfCommandBuilder.BuildCommand(Sample()));
    }

    [Fact]
    public void BuildCommand_ShouldIncludeCookieWhenPresent()
    {
        var options = Sample() with { Cookie = "cf_clearance=abc" };

        Assert.Contains("--cookie \"cf_clearance=abc\"", CfCommandBuilder.BuildCommand(options));
    }

    [Fact]
    public void BuildCommand_ShouldAppendKeepSegsOnlyWhenRequested()
    {
        Assert.DoesNotContain("--keep-segs", CfCommandBuilder.BuildCommand(Sample()));
        Assert.Contains("--keep-segs", CfCommandBuilder.BuildCommand(Sample() with { KeepSegments = true }));
    }

    [Fact]
    public void BuildCommand_ShouldEscapeEmbeddedDoubleQuotes()
    {
        var options = Sample() with { OutputName = "my \"best\" clip.mp4" };

        Assert.Contains("-o \"my \\\"best\\\" clip.mp4\"", CfCommandBuilder.BuildCommand(options));
    }

    [Fact]
    public void BuildBatchScript_ShouldDoublePercentSigns()
    {
        // THE BUG: a percent-encoded URL is eaten by cmd.exe argument expansion.
        var cmd = CfCommandBuilder.BuildCommand(Sample("https://example.com/a%20b.m3u8"));

        var bat = CfCommandBuilder.BuildBatchScript(cmd);

        Assert.Contains("a%%20b.m3u8", bat);
        Assert.DoesNotContain("a%20b.m3u8", bat.Replace("%%", "\u0000"));
    }

    [Fact]
    public void BuildBatchScript_ShouldEmitUtf8Header()
    {
        var bat = CfCommandBuilder.BuildBatchScript("echo hi");

        Assert.StartsWith("@echo off", bat);
        Assert.Contains("chcp 65001 >nul", bat);
        Assert.Contains("set PYTHONUTF8=1", bat);
        Assert.Contains("set \"CF_EXIT_CODE=%ERRORLEVEL%\"", bat);
        Assert.Contains("exit /b %CF_EXIT_CODE%", bat);
    }

    [Theory]
    [InlineData("https://custom.example/", "https://example.com/a.m3u8", "https://custom.example/")]
    [InlineData("", "https://surrit.com/id/video.m3u8", "https://missav123.com/")]
    [InlineData("", "https://example.com/path/a.m3u8", "https://example.com/")]
    [InlineData(null, "https://example.com:8443/a.m3u8", "https://example.com:8443/")]
    [InlineData("", "not a url", "")]
    [InlineData("", null, "")]
    public void DeriveReferer_ShouldPreferExplicitThenFallBackToTheUrlAuthority(
        string? explicitReferer, string? inputUrl, string expected)
    {
        Assert.Equal(expected, CfCommandBuilder.DeriveReferer(explicitReferer, inputUrl));
    }

    [Fact]
    public void BuildArgumentList_ShouldCarryTheSameFlagSetAsBuildCommand()
    {
        var sample = Sample();
        var args = CfCommandBuilder.BuildArgumentList(sample);

        // The automatic fallback feeds ProcessStartInfo.ArgumentList (no shell quoting);
        // the interactive path feeds a .bat. Both must send the same flags, or a bypass
        // triggered without user interaction quietly does less work than the visible one.
        foreach (var expected in new[] { "--referer", "-o", "--work-dir", "--seg-dir",
                                         "--impersonate", "--thread-count" })
            Assert.Contains(expected, args);
        Assert.Equal(sample.ScriptPath, args[0]);
        Assert.Equal(sample.Url, args[1]);
        Assert.DoesNotContain("--cookie", args);
        Assert.DoesNotContain("--keep-segs", args);
    }

    [Fact]
    public void BuildArgumentList_ShouldSkipEmptyValuesAndClampThreadsLikeTheBatchForm()
    {
        var withCookie = CfCommandBuilder.BuildArgumentList(Sample() with { Cookie = "cf_clearance=abc" });
        Assert.Contains("--cookie", withCookie);
        var ci = withCookie.ToList().IndexOf("--cookie");
        Assert.True(ci >= 0);
        Assert.Equal("cf_clearance=abc", withCookie[ci + 1]);

        var clamped = CfCommandBuilder.BuildArgumentList(Sample() with { ThreadCount = 9999, KeepSegments = true });
        var ti = clamped.ToList().IndexOf("--thread-count");
        Assert.True(ti >= 0);
        Assert.Equal("64", clamped[ti + 1]);
        Assert.Contains("--keep-segs", clamped);
    }
}

/// <summary>Candidate ordering is the contract the interactive and automatic paths share.</summary>
public class PythonProbeTests
{
    [Fact]
    public void EnumerateCandidates_ShouldEndWithThePathLaunchers()
    {
        var candidates = N_m3u8DL_RE_GUI.Core.PythonProbe.EnumerateCandidates();

        Assert.Contains("py", candidates);
        Assert.True(candidates.Count >= 3, "at least the three PATH launchers must always be offered");

        var n = candidates.Count;
        Assert.Equal("py", candidates[n - 3]);
        Assert.Equal("python", candidates[n - 2]);
        Assert.Equal("python3", candidates[n - 1]);

        // Anything before the launchers is a probed install path, and must be a real
        // python.exe rather than a directory or a bare alias.
        for (var i = 0; i < n - 3; i++)
            Assert.EndsWith("python.exe", candidates[i]);
    }
}
