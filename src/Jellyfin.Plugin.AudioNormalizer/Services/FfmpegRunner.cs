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

    /// <summary>Asks the server whether a filter is compiled into its ffmpeg.</summary>
    /// <param name="filter">Filter name.</param>
    /// <returns>True when available.</returns>
    public bool SupportsFilter(string filter)
    {
        try
        {
            return _mediaEncoder.SupportsFilter(filter);
        }
        catch (Exception ex)
        {
            // Older or unusual builds may not answer. Assume present and let ffmpeg complain
            // with a clear message rather than refusing to run at all.
            _logger.LogDebug(ex, "Audio Normalizer: filter probe failed for {Filter}", filter);
            return true;
        }
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
