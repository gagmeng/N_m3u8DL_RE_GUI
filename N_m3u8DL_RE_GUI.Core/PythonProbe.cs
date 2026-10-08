#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace N_m3u8DL_RE_GUI.Core;

/// <summary>
/// Locates a Python interpreter that can import <c>curl_cffi</c>.
///
/// Extracted from the GUI so every caller — the interactive Cloudflare path and the
/// automatic fallback inside the download orchestrator — resolves interpreters the
/// same way. The previous service-side probe only looked at PATH, which fails on
/// installs that never register themselves there (the common case on Windows).
/// </summary>
public static class PythonProbe
{
    private static readonly object _cacheLock = new();
    private static string? _cachedResult;
    private static bool _cacheValid;

    /// <summary>Per-interpreter probe budget. Long enough for a cold import, short enough to keep the UI responsive.</summary>
    internal const int ProbeTimeoutMs = 10000;

    /// <summary>
    /// Candidate order: explicit CPython installs (avoids the Windows Store stub),
    /// managed environments, then the PATH-resolved launchers as a last resort.
    /// </summary>
    public static IReadOnlyList<string> EnumerateCandidates()
    {
        var candidates = new List<string>();

        try
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var baseDir in new[] { progFiles, progFilesX86 })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                var pyRoot = Path.Combine(baseDir, "Python");
                if (Directory.Exists(pyRoot))
                    foreach (var d in Directory.GetDirectories(pyRoot))
                        candidates.Add(Path.Combine(d, "python.exe"));
            }
            if (!string.IsNullOrEmpty(localApp))
            {
                var pp = Path.Combine(localApp, "Programs", "Python");
                if (Directory.Exists(pp))
                    foreach (var d in Directory.GetDirectories(pp))
                        candidates.Add(Path.Combine(d, "python.exe"));
            }
        }
        catch { }

        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                string wbPy = Path.Combine(userProfile, ".workbuddy", "binaries", "python", "versions");
                if (Directory.Exists(wbPy))
                    foreach (var v in Directory.GetDirectories(wbPy))
                        candidates.Add(Path.Combine(v, "python.exe"));

                foreach (var condaName in new[] { "anaconda3", "miniconda3", "Anaconda3", "Miniconda3" })
                {
                    var condaPath = Path.Combine(userProfile, condaName, "python.exe");
                    if (File.Exists(condaPath))
                        candidates.Add(condaPath);
                }
            }
        }
        catch { }

        candidates.Add("py");
        candidates.Add("python");
        candidates.Add("python3");
        return candidates;
    }

    /// <summary>
    /// First interpreter whose `import curl_cffi` exits 0, or null when none answers.
    /// Respects <paramref name="cancellationToken"/>: a user cancel is rethrown, a
    /// per-interpreter timeout only skips that candidate. A successful answer is cached
    /// for the process lifetime because the probe costs one subprocess per candidate.
    /// </summary>
    public static async Task<string?> DetectWithCurlCffiAsync(CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cacheValid)
                return _cachedResult;
        }

        foreach (var c in EnumerateCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Skip full paths that don't exist on disk.
                if (c.IndexOf(Path.DirectorySeparatorChar) >= 0 && !File.Exists(c))
                    continue;

                var psi = new ProcessStartInfo(c, "-c \"import curl_cffi\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null) continue;
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(ProbeTimeoutMs));

                    var outTask = p.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                    var errTask = p.StandardError.ReadToEndAsync(timeoutCts.Token);

                    try
                    {
                        await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        if (cancellationToken.IsCancellationRequested)
                            throw;
                        continue;
                    }

                    if (p.ExitCode == 0)
                    {
                        Remember(c);
                        return c;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }
        }

        Remember(null);
        return null;
    }

    private static void Remember(string? value)
    {
        lock (_cacheLock)
        {
            _cachedResult = value;
            _cacheValid = true;
        }
    }

    /// <summary>Drops the cached interpreter, e.g. after the user installs curl_cffi without restarting.</summary>
    public static void Reset()
    {
        lock (_cacheLock)
        {
            _cachedResult = null;
            _cacheValid = false;
        }
    }
}
