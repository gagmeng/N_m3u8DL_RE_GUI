#nullable enable
using N_m3u8DL_RE_GUI.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace N_m3u8DL_RE_GUI.Services;

/// <summary>
/// Implementation of download service using N_m3u8DL-RE executable or arbitrary processes.
/// Owns process lifecycle and process-tree cancellation safety.
/// </summary>
public class DownloadService : IDownloadService
{
    private Process? _currentProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly object _lockObject = new();

    private static bool SafeIsRunning(Process? process)
    {
        if (process == null) return false;
        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public bool IsDownloading
    {
        get
        {
            lock (_lockObject)
            {
                return SafeIsRunning(_currentProcess);
            }
        }
    }

    public Task<bool> StartDownloadAsync(
        DownloadOptions options,
        IProgress<int>? progressCallback = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
        {
            logCallback?.Invoke("Download is already in progress. Please wait for it to complete.");
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(options.Input))
        {
            logCallback?.Invoke("Please enter a URL to download.");
            return Task.FromResult(false);
        }

        var exePath = string.IsNullOrWhiteSpace(options.ExePath) ? "N_m3u8DL-RE.exe" : options.ExePath;
        if (!System.IO.File.Exists(exePath))
        {
            logCallback?.Invoke($"File not found: {exePath}");
            logCallback?.Invoke("Please download N_m3u8DL-RE.exe from: https://github.com/nilaoda/N_m3u8DL-RE/releases");
            return Task.FromResult(false);
        }

        logCallback?.Invoke("Starting download...");
        var args = ArgsBuilder.Build(options);
        logCallback?.Invoke($"Command: {exePath} {args}");

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // N_m3u8DL-RE writes localized text in the system ANSI code page when its
            // output is redirected (no console); decoding as UTF-8 garbles Chinese text.
            StandardOutputEncoding = TextEncodingDetector.AnsiFallback,
            StandardErrorEncoding = TextEncodingDetector.AnsiFallback
        };

        return StartTrackedProcessAsync(startInfo, logCallback, progressCallback, redirect: true, cancellationToken);
    }

    public Task<bool> StartProcessAsync(
        string fileName,
        string arguments,
        Action<string>? logCallback = null,
        IProgress<int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
        {
            logCallback?.Invoke("A process is already in progress. Please wait for it to complete.");
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            logCallback?.Invoke("Process target file path is required.");
            return Task.FromResult(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = TextEncodingDetector.AnsiFallback,
            StandardErrorEncoding = TextEncodingDetector.AnsiFallback
        };

        return StartTrackedProcessAsync(startInfo, logCallback, progressCallback, redirect: true, cancellationToken);
    }

    private async Task<bool> StartTrackedProcessAsync(
        ProcessStartInfo startInfo,
        Action<string>? logCallback,
        IProgress<int>? progressCallback,
        bool redirect,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        CancellationTokenSource? cts = null;

        lock (_lockObject)
        {
            if (SafeIsRunning(_currentProcess))
            {
                logCallback?.Invoke("A process is already in progress. Please wait for it to complete.");
                return false;
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellationTokenSource = cts;

            process = new Process { StartInfo = startInfo };
            _currentProcess = process;
        }

        try
        {
            if (!process.Start())
            {
                logCallback?.Invoke($"Failed to start process: {startInfo.FileName}");
                return false;
            }

            if (redirect)
            {
                // N_m3u8DL-RE's redirected progress frames contain no newline at all, so
                // BeginOutputReadLine never fires until the process exits. Pump the raw
                // byte streams manually and classify each chunk via the parser.
                var forwarder = new OutputForwarder(logCallback, progressCallback);
                var pumpOut = PumpStreamAsync(process.StandardOutput.BaseStream, forwarder, cts.Token);
                var pumpErr = PumpStreamAsync(process.StandardError.BaseStream, forwarder, cts.Token);
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                    await Task.WhenAll(pumpOut, pumpErr);
                }
                catch (OperationCanceledException)
                {
                    logCallback?.Invoke("Process execution was cancelled.");
                    return false;
                }
                forwarder.FlushPending();
            }
            else
            {
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    logCallback?.Invoke("Process execution was cancelled.");
                    return false;
                }
            }

            var success = process.ExitCode == 0;
            logCallback?.Invoke(success
                ? "Process finished successfully!"
                : $"Process exited with code: {process.ExitCode}");

            if (success)
                progressCallback?.Report(100);

            return success;
        }
        catch (OperationCanceledException)
        {
            logCallback?.Invoke("Process execution was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"Process execution error: {ex.Message}");
            return false;
        }
        finally
        {
            lock (_lockObject)
            {
                if (_currentProcess == process) _currentProcess = null;
                if (_cancellationTokenSource == cts) _cancellationTokenSource = null;
            }

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                try { process.Dispose(); } catch { }
            }

            try { cts?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Reads a redirected stream in chunks, decodes it and hands the text to the
    /// forwarder. Uses the raw byte stream because BeginOutputReadLine only fires on
    /// LF and N_m3u8DL-RE progress frames contain none. Encoding: the engine writes
    /// localized text in the system ANSI code page when output is not a console.
    /// </summary>
    private static async Task PumpStreamAsync(Stream stream, OutputForwarder forwarder, CancellationToken token)
    {
        var buffer = new byte[8192];
        var decodeBuffer = new char[8192];
        var encoding = TextEncodingDetector.AnsiFallback;
        // Stateful decoder: multibyte ANSI characters (GBK) can straddle chunk boundaries.
        var decoder = encoding.GetDecoder();

        while (true)
        {
            try
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0)
                    break;

                // Decoder.Convert safely carries partial multibyte sequences across chunks;
                // plain GetChars throws when the output buffer is undersized.
                decoder.Convert(buffer, 0, read, decodeBuffer, 0, decodeBuffer.Length, flush: false,
                    out _, out var charCount, out _);
                forwarder.AcceptChunk(decodeBuffer, charCount);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                break; // stream closed (process killed)
            }
        }
    }

    /// <summary>
    /// Turns raw engine output into GUI log entries and progress reports. Each pump
    /// chunk is split by <see cref="ConsoleOutputParser.SplitOutput"/>; segment types
    /// drive the behaviour: complete records are emitted to the log immediately,
    /// progress frames are collapsed to at most one snapshot per interval (progress
    /// bar still updates on every change), and an unterminated tail fragment is held
    /// in the pending buffer until the next chunk completes it or the stream ends.
    /// </summary>
    private sealed class OutputForwarder
    {
        private const int ProgressThrottleMs = 500;

        private readonly Action<string>? _logCallback;
        private readonly IProgress<int>? _progressCallback;
        private readonly object _lock = new();
        private readonly StringBuilder _pending = new();
        private DateTime _lastProgressLog = DateTime.MinValue;
        private int _lastReportedPercent = -1;

        public OutputForwarder(Action<string>? logCallback, IProgress<int>? progressCallback)
        {
            _logCallback = logCallback;
            _progressCallback = progressCallback;
        }

        /// <summary>Accepts decoded characters from a pump chunk. Thread-safe.</summary>
        public void AcceptChunk(char[] chars, int count)
        {
            List<string> toEmit = new();
            lock (_lock)
            {
                _pending.Append(chars, 0, count);
                var text = _pending.ToString();
                _pending.Clear();

                var segments = ConsoleOutputParser.SplitOutput(text);
                for (var i = 0; i < segments.Count; i++)
                {
                    var segment = segments[i];
                    var isLast = i == segments.Count - 1;

                    if (isLast && !segment.IsComplete)
                    {
                        // Unterminated fragment — hold it until the next chunk completes it.
                        _pending.Append(segment.Text);
                        continue;
                    }

                    if (segment.IsProgress)
                        HandleProgressFrameLocked(segment.Text, toEmit);
                    else
                        EmitRecordLocked(segment.Text, toEmit);
                }
            }

            // Invoked outside the lock so log handlers can re-enter safely.
            Emit(toEmit);
        }

        /// <summary>Callers must hold _lock.</summary>
        private void EmitRecordLocked(string record, List<string> toEmit)
        {
            foreach (var cleaned in ConsoleOutputParser.SplitGluedLogRecords(record))
            {
                var entry = ConsoleOutputParser.Clean(cleaned);
                if (entry.Length > 0)
                {
                    var percent = ConsoleOutputParser.TryExtractPercent(entry);
                    if (percent.HasValue && percent.Value != _lastReportedPercent)
                    {
                        _lastReportedPercent = percent.Value;
                        _progressCallback?.Report(percent.Value);
                    }

                    toEmit.Add(entry);
                }
            }
        }

        /// <summary>Callers must hold _lock. Collects throttled progress entries into <paramref name="toEmit"/>.</summary>
        private void HandleProgressFrameLocked(string frame, List<string> toEmit)
        {
            var percent = ConsoleOutputParser.TryExtractPercent(frame);
            if (percent.HasValue && percent.Value != _lastReportedPercent)
            {
                _lastReportedPercent = percent.Value;
                _progressCallback?.Report(percent.Value);
            }

            if ((DateTime.Now - _lastProgressLog).TotalMilliseconds < ProgressThrottleMs)
                return;

            _lastProgressLog = DateTime.Now;
            foreach (var row in ConsoleOutputParser.SplitProgressFrame(frame))
            {
                var cleaned = ConsoleOutputParser.Clean(row);
                if (cleaned.Length > 0)
                    toEmit.Add(cleaned);
            }
        }

        /// <summary>
        /// Emits whatever is still pending after the stream ends: the newest throttled
        /// progress frame and any unterminated tail fragment.
        /// </summary>
        public void FlushPending()
        {
            List<string> lines;
            lock (_lock)
            {
                lines = new List<string>();
                if (_pending.Length > 0)
                {
                    foreach (var row in ConsoleOutputParser.SplitProgressFrame(_pending.ToString()))
                    {
                        var cleaned = ConsoleOutputParser.Clean(row);
                        if (cleaned.Length > 0)
                            lines.Add(cleaned);
                    }
                    _pending.Clear();
                }
            }

            Emit(lines);
        }

        private void Emit(List<string> entries)
        {
            foreach (var entry in entries)
                _logCallback?.Invoke(entry);
        }
    }

    public void StopDownload()
    {
        Process? procToKill = null;
        CancellationTokenSource? ctsToCancel = null;

        lock (_lockObject)
        {
            procToKill = _currentProcess;
            ctsToCancel = _cancellationTokenSource;
        }

        if (ctsToCancel != null)
        {
            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"Token cancel error: {ex.Message}");
            }
        }

        if (procToKill != null)
        {
            try
            {
                if (SafeIsRunning(procToKill))
                {
                    // Kill the entire process tree to also terminate child processes
                    // (ffmpeg, mp4decrypt, python, etc.)
                    procToKill.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to stop process tree: {ex.Message}");
            }
        }
    }
}
