using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>Result of one ffmpeg run.</summary>
public sealed class FfmpegResult
{
    /// <summary>Gets or sets the exit code, or null when the process never finished.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Gets or sets the tail of stderr, which is where ffmpeg puts everything useful.</summary>
    public string StdErr { get; set; } = string.Empty;

    /// <summary>Gets or sets anything printed on stdout, used for loudnorm's JSON.</summary>
    public string StdOut { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether the run succeeded.</summary>
    public bool Success => ExitCode == 0;

    /// <summary>Gets or sets a value indicating whether the run was stopped by the timeout.</summary>
    public bool TimedOut { get; set; }
}

/// <summary>
/// Runs ffmpeg. Everything that touches a process lives here: argument quoting, progress,
/// timeouts, cancellation and cleanup, so the rest of the plugin never spawns anything itself.
/// </summary>
public sealed class FfmpegRunner
{
    private const int StdErrTailChars = 8000;

    private readonly ILogger<FfmpegRunner> _logger;
    private readonly IMediaEncoder _mediaEncoder;

    // Guards _filterProbe only; the probe itself runs outside the lock.
    private readonly object _filterLock = new();

    private Task<HashSet<string>?>? _filterProbe;

    /// <summary>Initializes a new instance of the <see cref="FfmpegRunner"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="mediaEncoder">The server's encoder, which knows where its own ffmpeg lives.</param>
    public FfmpegRunner(ILogger<FfmpegRunner> logger, IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>Gets the ffmpeg binary the server itself uses.</summary>
    public string EncoderPath => _mediaEncoder.EncoderPath;

    /// <summary>Asks whether a filter is compiled into the server's ffmpeg.</summary>
    /// <param name="filter">Filter name.</param>
    /// <returns>True when available, and true as well when the probe could not answer.</returns>
    /// <remarks>
    /// Deliberately NOT IMediaEncoder.SupportsFilter. The server answers that one out of
    /// EncoderValidator._requiredFilters, a whitelist of about forty hardware and scaling
    /// filters (scale_cuda, tonemap_vaapi, libplacebo, zscale, alphasrc...) that it collects
    /// to make transcoding decisions. Anything outside that list is reported missing on every
    /// ffmpeg build there is - and that includes every audio filter this plugin uses, so the
    /// diagnostics page claimed loudnorm, ebur128 and dynaudnorm were all absent from
    /// jellyfin-ffmpeg. Verified in MediaBrowser.MediaEncoding/Encoder/EncoderValidator.cs on
    /// both release-10.11.z and 12. So: read the list out of ffmpeg itself, once, and cache it.
    /// </remarks>
    public async Task<bool> SupportsFilterAsync(string filter)
    {
        var filters = await GetFiltersAsync().ConfigureAwait(false);

        // A probe that could not run must not turn into "nothing works". Assume present and
        // let ffmpeg fail with its own clear message if the filter really is missing.
        return filters is null || filters.Contains(filter);
    }

    private async Task<HashSet<string>?> GetFiltersAsync()
    {
        Task<HashSet<string>?> probe;
        lock (_filterLock)
        {
            probe = _filterProbe ??= ProbeFiltersAsync();
        }

        var result = await probe.ConfigureAwait(false);
        if (result is null)
        {
            // A failure is not cached: a corrected ffmpeg path should be picked up without
            // restarting the server.
            lock (_filterLock)
            {
                if (ReferenceEquals(_filterProbe, probe))
                {
                    _filterProbe = null;
                }
            }
        }

        return result;
    }

    private async Task<HashSet<string>?> ProbeFiltersAsync()
    {
        // Its own timeout, and no caller's cancellation token: the result is shared, so one
        // caller giving up must not poison it for every other caller.
        var run = await RunAsync(
            new[] { "-hide_banner", "-nostdin", "-filters" },
            0,
            null,
            0,
            TimeSpan.FromSeconds(30),
            CancellationToken.None).ConfigureAwait(false);

        if (!run.Success)
        {
            _logger.LogWarning(
                "Audio Normalizer: could not list ffmpeg filters (exit {Code}), assuming they are all present",
                run.ExitCode);
            return null;
        }

        var names = ParseFilterList(run.StdOut);
        _logger.LogDebug("Audio Normalizer: ffmpeg reports {Count} filters", names.Count);
        return names;
    }

    /// <summary>Reads the filter names out of the table printed by <c>ffmpeg -filters</c>.</summary>
    /// <param name="output">Raw stdout of the listing.</param>
    /// <returns>Every filter name found.</returns>
    private static HashSet<string> ParseFilterList(string output)
    {
        //  " T.. ebur128          A->V       EBU R128 scanner."
        //    ^flags ^name          ^conversion
        // The legend printed above the table ("  T.. = Timeline support") has no conversion
        // column, which is what keeps it out of the set.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(output);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3
                || parts[0].Length != 3
                || !parts[2].Contains("->", StringComparison.Ordinal)
                || !parts[0].All(c => c is 'T' or 'S' or 'C' or '.'))
            {
                continue;
            }

            names.Add(parts[1]);
        }

        return names;
    }

    /// <summary>Runs ffmpeg to completion.</summary>
    /// <param name="args">Arguments, one per element, already unquoted.</param>
    /// <param name="totalSeconds">Expected media duration, used for progress. Zero disables it.</param>
    /// <param name="progress">Optional 0-100 progress sink.</param>
    /// <param name="niceness">Unix niceness, ignored elsewhere.</param>
    /// <param name="timeout">Hard limit for the run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result.</returns>
    public async Task<FfmpegResult> RunAsync(
        IReadOnlyList<string> args,
        double totalSeconds,
        IProgress<double>? progress,
        int niceness,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = new FfmpegResult();
        string? progressFile = null;
        var effectiveArgs = new List<string>(args.Count + 2);

        if (progress is not null && totalSeconds > 0)
        {
            // Verified: ffmpeg writes progress blocks to this file for both the analysis pass
            // (-f null) and real encodes. Reading a file avoids deadlocking on a full pipe.
            progressFile = Path.Combine(Path.GetTempPath(), "jfan-" + Guid.NewGuid().ToString("N") + ".progress");
            effectiveArgs.Add("-progress");
            effectiveArgs.Add(progressFile);
        }

        effectiveArgs.AddRange(args);

        var psi = new ProcessStartInfo
        {
            FileName = EncoderPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ArgumentList, never a joined string: media paths contain spaces, quotes, Cyrillic and
        // brackets, and filter graphs contain commas and colons.
        foreach (var a in effectiveArgs)
        {
            psi.ArgumentList.Add(a);
        }

        ApplyNiceness(psi, niceness);

        // The exact command, reproducible by hand. This is the single most useful thing in the
        // log when a job misbehaves, so it is always written at Debug and promoted to
        // Information when the user turns the option on.
        if (Plugin.Instance?.Configuration.LogFfmpegCommands == true)
        {
            _logger.LogInformation("Audio Normalizer: running {Command}", DescribeCommand(psi));
        }
        else
        {
            _logger.LogDebug("Audio Normalizer: running {Command}", DescribeCommand(psi));
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdErr = new StringBuilder();
        var stdOut = new StringBuilder();
        var errLock = new object();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (errLock)
            {
                stdErr.AppendLine(e.Data);
                // Keep only the tail: a two hour encode can emit a lot, and we only ever show
                // the last few lines to the user.
                if (stdErr.Length > StdErrTailChars * 2)
                {
                    stdErr.Remove(0, stdErr.Length - StdErrTailChars);
                }
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errLock)
                {
                    stdOut.AppendLine(e.Data);
                }
            }
        };

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var started = DateTime.UtcNow;
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio Normalizer: could not start ffmpeg at {Path}", EncoderPath);
            result.StdErr = "Could not start ffmpeg at " + EncoderPath + ": " + ex.Message;
            CleanupProgressFile(progressFile);
            return result;
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        // ffmpeg reads stdin and will happily swallow the server's console otherwise.
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: closing ffmpeg stdin failed");
        }

        using var progressPump = StartProgressPump(progressFile, totalSeconds, progress, linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            result.ExitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = timeoutCts.IsCancellationRequested;
            KillQuietly(process);

            // Let it actually die before the caller deletes the part file underneath it.
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio Normalizer: ffmpeg did not exit after kill");
            }

            if (!result.TimedOut)
            {
                CleanupProgressFile(progressFile);
                lock (errLock)
                {
                    result.StdErr = stdErr.ToString();
                }

                throw;
            }
        }

        // Give the async readers a moment to drain the last lines.
        try
        {
            process.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: drain wait failed");
        }

        lock (errLock)
        {
            result.StdErr = stdErr.ToString();
            result.StdOut = stdOut.ToString();
        }


        // The realtime factor is the single most useful number when a library run feels slow:
        // it separates a genuinely slow decode from a queue that was barely running. Same
        // level as the command itself, so turning the option on gives both.
        var elapsed = DateTime.UtcNow - started;
        _logger.Log(
            Plugin.Instance?.Configuration.LogFfmpegCommands == true ? LogLevel.Information : LogLevel.Debug,
            "Audio Normalizer: ffmpeg exited {Code} after {Seconds:F0}s ({Factor:F1}x realtime)",
            result.ExitCode,
            elapsed.TotalSeconds,
            totalSeconds > 0 ? totalSeconds / Math.Max(0.001, elapsed.TotalSeconds) : 0);

        CleanupProgressFile(progressFile);
        return result;
    }

    private static string DescribeCommand(ProcessStartInfo psi)
    {
        static string One(string a) =>
            a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=')
                ? a
                : "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        return One(psi.FileName) + " " + string.Join(' ', psi.ArgumentList.Select(One));
    }

    private static void ApplyNiceness(ProcessStartInfo psi, int niceness)    {
        if (niceness <= 0 || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        // Wrap in nice(1) rather than setting priority afterwards: by the time the process is
        // running it has already competed for CPU with playback transcodes.
        var original = new List<string>(psi.ArgumentList);
        var target = psi.FileName;
        psi.FileName = "/usr/bin/nice";
        psi.ArgumentList.Clear();
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(Math.Clamp(niceness, 1, 19).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(target);
        foreach (var a in original)
        {
            psi.ArgumentList.Add(a);
        }

        if (!File.Exists("/usr/bin/nice"))
        {
            // Not every container ships coreutils' nice. Fall back to running ffmpeg directly.
            psi.FileName = target;
            psi.ArgumentList.Clear();
            foreach (var a in original)
            {
                psi.ArgumentList.Add(a);
            }
        }
    }

    private static void CleanupProgressFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a job over.
        }
    }

    private void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: killing ffmpeg failed");
        }
    }

    private CancellationTokenSource StartProgressPump(
        string? progressFile,
        double totalSeconds,
        IProgress<double>? progress,
        CancellationToken token)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (progressFile is null || progress is null || totalSeconds <= 0)
        {
            return cts;
        }

        _ = Task.Run(
            async () =>
            {
                var last = -1.0;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                        if (!File.Exists(progressFile))
                        {
                            continue;
                        }

                        // ffmpeg keeps the file open and appends; open shared or we race it.
                        string text;
                        using (var fs = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs))
                        {
                            text = await sr.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                        }

                        var us = LastLongValue(text, "out_time_us=");
                        if (us <= 0)
                        {
                            continue;
                        }

                        var pct = Math.Clamp(us / 1_000_000.0 / totalSeconds * 100.0, 0, 100);
                        if (pct - last >= 0.5)
                        {
                            last = pct;
                            progress.Report(pct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogTrace(ex, "Audio Normalizer: progress read failed");
                    }
                }
            },
            CancellationToken.None);

        return cts;
    }

    private static long LastLongValue(string text, string key)
    {
        var idx = text.LastIndexOf(key, StringComparison.Ordinal);
        if (idx < 0)
        {
            return -1;
        }

        var start = idx + key.Length;
        var end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '-'))
        {
            end++;
        }

        return long.TryParse(text.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : -1;
    }
}
