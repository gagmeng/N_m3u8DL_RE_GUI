#nullable enable
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace N_m3u8DL_RE_GUI.Core;

/// <summary>
/// Turns raw redirected console output from N_m3u8DL-RE into text fit for the GUI log
/// and a progress percentage. Pure functions — no streams, no state.
/// </summary>
public static class ConsoleOutputParser
{
    // CSI sequences: ESC [ <params> <final byte>. Covers colour, erase-line and cursor moves.
    private static readonly Regex AnsiPattern = new(
        @"\u001b\[[0-9;?]*[A-Za-z]",
        RegexOptions.Compiled);

    // Last percentage on the line is the freshest one on a redrawn progress row.
    private static readonly Regex PercentPattern = new(
        @"(?<!\d)(\d{1,3})(?:\.\d+)?%",
        RegexOptions.Compiled | RegexOptions.RightToLeft);

    public static string StripAnsi(string line) =>
        string.IsNullOrEmpty(line) ? string.Empty : AnsiPattern.Replace(line, string.Empty);

    /// <summary>Returns 0-100, or null when the line carries no usable percentage.</summary>
    public static int? TryExtractPercent(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        // A progress frame holds one bar per stream (Vid/Aud/Sub). The rightmost percent
        // is usually an idle subtitle bar (0%), which made the GUI progress bar oscillate.
        // Prefer the video bar's percent; fall back to the highest percent in the frame.
        var stripped = StripAnsi(line);
        var vidMatch = VidRowPattern.Match(stripped);
        if (vidMatch.Success)
        {
            return int.TryParse(vidMatch.Groups[1].Value, out var vidPercent) && vidPercent >= 0 && vidPercent <= 100
                ? vidPercent
                : null;
        }

        var match = PercentPattern.Match(stripped);
        if (!match.Success)
            return null;

        return int.TryParse(match.Groups[1].Value, out var percent) && percent >= 0 && percent <= 100
            ? percent
            : null;
    }

    /// <summary>Strips escapes and surrounding whitespace; empty when nothing remains.</summary>
    public static string Clean(string? rawLine) => StripAnsi(rawLine ?? string.Empty).Trim();

    /// <summary>
    /// N_m3u8DL-RE sometimes omits the newline between consecutive log records when its
    /// output is redirected, gluing them into one line ("...Streaming22:46:39.213 INFO : ...").
    /// Splits on the record stamp pattern so each record becomes its own line.
    /// </summary>
    public static List<string> SplitGluedLogRecords(string line)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(line))
            return result;

        var matches = RecordStampPattern.Matches(line);
        if (matches.Count <= 1)
        {
            result.Add(line);
            return result;
        }

        // Text before the first stamp belongs to the previous (prefix) fragment.
        var head = line[..matches[0].Index].TrimEnd();
        if (head.Length > 0)
            result.Add(head);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
            var record = line[start..end].TrimEnd();
            if (record.Length > 0)
                result.Add(record);
        }

        return result;
    }

    /// <summary>
    /// A segment of raw engine output, classified by <see cref="ConsoleOutputParser.SplitOutput"/>.
    /// Records become log lines; progress segments feed the throttled progress display.
    /// </summary>
    public readonly record struct OutputSegment(string Text, bool IsProgress, bool IsComplete);

    /// <summary>
    /// Splits raw redirected engine output into ordered segments. N_m3u8DL-RE redirects
    /// as a mix of newline-terminated log records and newline-less progress frames whose
    /// redraws are glued directly onto the previous row, and any of it can straddle a
    /// chunk boundary. Segments are cut on line breaks, record stamps ("HH:MM:SS.mmm
    /// INFO :") and progress row labels ("Vid "/"Aud "/"Sub "). The final segment may be
    /// an unterminated fragment (IsComplete == false) that the caller must hold until
    /// the next chunk completes it.
    /// </summary>
    public static List<OutputSegment> SplitOutput(string text)
    {
        var segments = new List<OutputSegment>();
        if (string.IsNullOrEmpty(text))
            return segments;

        var segStart = 0;
        var scan = 0;
        var inProgress = false;

        while (scan < text.Length)
        {
            var nl = text.IndexOfAny(BreakChars, scan);
            if (nl < 0)
                nl = int.MaxValue;

            var stamp = RecordStampPattern.Match(text, scan);
            var stampIdx = stamp.Success ? stamp.Index : int.MaxValue;

            var row = ProgressRowStartPattern.Match(text, scan);
            var rowIdx = row.Success ? row.Index : int.MaxValue;

            var first = Math.Min(nl, Math.Min(stampIdx, rowIdx));
            if (first == int.MaxValue)
                break;

            if (first > segStart)
                segments.Add(new OutputSegment(text[segStart..first], inProgress, IsComplete: true));

            if (first == stampIdx)
            {
                inProgress = false;
                segStart = stampIdx;
                scan = stampIdx + stamp.Length;
            }
            else if (first == rowIdx)
            {
                inProgress = true;
                segStart = rowIdx;
                scan = rowIdx + 3; // past the Vid/Aud/Sub label
            }
            else
            {
                // CR redraws continue a progress frame; LF starts a fresh record.
                inProgress = text[first] == '\r';
                segStart = first + 1;
                scan = segStart;
            }
        }

        if (segStart < text.Length)
            segments.Add(new OutputSegment(text[segStart..], inProgress, IsComplete: false));

        return segments;
    }

    /// <summary>
    /// Splits a progress frame chunk into one entry per stream row, e.g.
    /// "Vid 1920x1080 | ... 12/101 11.88% ... --:--:--". Rows not ending in a timestamp
    /// (the newest, still-being-written row) are folded into the preceding row.
    /// </summary>
    public static List<string> SplitProgressFrame(string line)
    {
        var rows = new List<string>();
        if (string.IsNullOrEmpty(line))
            return rows;

        var matches = ProgressRowStartPattern.Matches(line);
        if (matches.Count == 0)
        {
            rows.Add(line);
            return rows;
        }

        var head = line[..matches[0].Index].TrimEnd();
        if (head.Length > 0)
            rows.Add(head);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
            rows.Add(line[start..end].TrimEnd());
        }

        return rows;
    }

    private static readonly Regex RecordStampPattern = new(
        @"\d{2}:\d{2}:\d{2}\.\d{3} (?:INFO|WARN|ERROR|EXTRA|DEBUG)\s:",
        RegexOptions.Compiled);

    private static readonly char[] BreakChars = ['\r', '\n'];

    // A progress row label, captured so the scan can step past it. The redirected
    // stream has no separators between redrawn frames ("…00:02:54Vid Kbps …"), so
    // no word boundary may be required before the label.
    private static readonly Regex ProgressRowStartPattern = new(
        @"(Vid|Aud|Sub)\s", RegexOptions.Compiled);

    // The video bar's percent, e.g. "Vid 1920x1080 | ... 46/181 25.41% ...".
    private static readonly Regex VidRowPattern = new(
        @"Vid\s[^\r\n]*?---+\s*\d+/\d+\s+(\d{1,3})(?:\.\d+)?%",
        RegexOptions.Compiled);
}
