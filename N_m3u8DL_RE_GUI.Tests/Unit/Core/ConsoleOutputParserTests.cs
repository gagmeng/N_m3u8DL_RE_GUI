#nullable enable
using N_m3u8DL_RE_GUI.Core;
using Xunit;

namespace N_m3u8DL_RE_GUI.Tests.Unit.Core;

public class ConsoleOutputParserTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("\u001b[32mgreen\u001b[0m", "green")]
    [InlineData("\u001b[1;33mbold yellow\u001b[0m tail", "bold yellow tail")]
    [InlineData("\u001b[2K\u001b[1Gredrawn", "redrawn")]
    [InlineData("no escapes at all", "no escapes at all")]
    [InlineData("", "")]
    public void StripAnsi_ShouldRemoveEscapeSequencesOnly(string input, string expected)
    {
        Assert.Equal(expected, ConsoleOutputParser.StripAnsi(input));
    }

    [Theory]
    [InlineData("Downloading... 45%", 45)]
    [InlineData("Vid 1080p | 45.7% | 3.2MBps", 45)]
    [InlineData("100%", 100)]
    [InlineData("0%", 0)]
    [InlineData("first 10% then 80%", 80)]      // last match wins — it is the freshest
    [InlineData("no percent here", null)]
    [InlineData("", null)]
    [InlineData("999%", null)]                   // out of range, ignore
    [InlineData("file_100%_name.ts", 100)]
    public void TryExtractPercent_ShouldReturnTheLastValidPercentage(string line, int? expected)
    {
        Assert.Equal(expected, ConsoleOutputParser.TryExtractPercent(line));
    }

    [Fact]
    public void TryExtractPercent_ShouldIgnoreAnsiNoise()
    {
        Assert.Equal(72, ConsoleOutputParser.TryExtractPercent("\u001b[32m72%\u001b[0m done"));
    }

    [Theory]
    [InlineData("  \u001b[32mhello\u001b[0m  ", "hello")]
    [InlineData("\u001b[2K", "")]
    [InlineData("   ", "")]
    [InlineData("\r\n", "")]
    public void Clean_ShouldStripEscapesAndTrim(string input, string expected)
    {
        Assert.Equal(expected, ConsoleOutputParser.Clean(input));
    }

    [Fact]
    public void Clean_ShouldPreserveInternalSpacingAndUnicode()
    {
        Assert.Equal("ตอนที่ 1 中文 — dash", ConsoleOutputParser.Clean("  ตอนที่ 1 中文 — dash  "));
    }

    [Fact]
    public void SplitOutput_ShouldSeparateRecordsFromNewlineLessProgressFrames()
    {
        // Shape seen in real runs: records glued directly onto progress frame redraws.
        var text = "08:35:59.885 INFO : 保存文件名: index" +
                   "Vid Kbps ------------------------------ 1/1213 0.08% -152.20KBps00:02:54" +
                   "Vid Kbps ------------------------------ 7/1213 0.58% 4.42MBps00:08:37" +
                   "08:36:01.987 INFO : [0x100]: Video, h264 (Main), 854x480";

        var segments = ConsoleOutputParser.SplitOutput(text);

        Assert.Collection(segments,
            s => { Assert.Equal("08:35:59.885 INFO : 保存文件名: index", s.Text); Assert.False(s.IsProgress); },
            s => { Assert.Contains("1/1213", s.Text); Assert.True(s.IsProgress); },
            s => { Assert.Contains("7/1213", s.Text); Assert.True(s.IsProgress); },
            s => { Assert.Contains("[0x100]", s.Text); Assert.False(s.IsProgress); });
    }

    [Fact]
    public void SplitOutput_ShouldSplitRecordsGluedOntoEachOther()
    {
        var segments = ConsoleOutputParser.SplitOutput(
            "08:35:59.550 INFO : 加载URL: https://example.com08:35:59.867 WARN : 写出meta json");

        Assert.Collection(segments,
            s => Assert.Equal("08:35:59.550 INFO : 加载URL: https://example.com", s.Text),
            s => Assert.Equal("08:35:59.867 WARN : 写出meta json", s.Text));
    }

    [Fact]
    public void SplitOutput_ShouldHoldTrailingIncompleteFragment()
    {
        var segments = ConsoleOutputParser.SplitOutput(
            "08:35:59.550 INFO : 加载URL: https://example.com08:36:0");

        Assert.Single(segments);
        Assert.False(segments[0].IsComplete);
        Assert.Equal("08:35:59.550 INFO : 加载URL: https://example.com08:36:0", segments[0].Text);
    }

    [Fact]
    public void SplitOutput_ShouldKeepNewlineTerminatedRecordComplete()
    {
        var segments = ConsoleOutputParser.SplitOutput("08:35:59.550 INFO : Done\r\n");

        Assert.Single(segments);
        Assert.True(segments[0].IsComplete);
        Assert.Equal("08:35:59.550 INFO : Done", segments[0].Text);
    }
}
