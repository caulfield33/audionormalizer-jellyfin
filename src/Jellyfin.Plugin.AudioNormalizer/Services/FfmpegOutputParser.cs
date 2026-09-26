using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AudioNormalizer.Models;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>Pulls numbers out of ffmpeg's output. Both ebur128 and loudnorm print to stderr.</summary>
public static class FfmpegOutputParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ebur128 prints a "Summary:" block with indented labels.
    private static readonly Regex Ebur128HeaderRx = new(
        @"\[Parsed_ebur128_(\d+) @ [^\]]*\]\s*Summary:",
        RegexOptions.Compiled);

    private static readonly Regex IntegratedRx = new(@"^\s*I:\s*(-?\d+(?:\.\d+)?)\s*LUFS", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex LraRx = new(@"^\s*LRA:\s*(-?\d+(?:\.\d+)?)\s*LU", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex LraLowRx = new(@"^\s*LRA low:\s*(-?\d+(?:\.\d+)?)\s*LUFS", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex LraHighRx = new(@"^\s*LRA high:\s*(-?\d+(?:\.\d+)?)\s*LUFS", RegexOptions.Multiline | RegexOptions.Compiled);

    // The true peak sits under a "True peak:" heading and is labelled "Peak:", in dBFS.
    private static readonly Regex TruePeakRx = new(
        @"True peak:\s*\r?\n\s*Peak:\s*(-?\d+(?:\.\d+)?|-inf)\s*dBFS",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Parses every ebur128 summary in the output, keyed by the filter's index in the graph.
    ///
    /// When one invocation measures several tracks at once, ffmpeg prints the summaries in
    /// whatever order the filters finish, NOT in graph order. Observed on a three-track file:
    /// 0, then 2, then 1. So the only safe key is the number in "Parsed_ebur128_N", which is
    /// the filter's position in the graph the caller built.
    /// </summary>
    /// <param name="stderr">ffmpeg's stderr.</param>
    /// <returns>Measurements by filter index.</returns>
    public static Dictionary<int, LoudnessMeasurement> ParseEbur128Multi(string stderr)
    {
        var result = new Dictionary<int, LoudnessMeasurement>();
        if (string.IsNullOrEmpty(stderr))
        {
            return result;
        }

        var headers = Ebur128HeaderRx.Matches(stderr);
        for (var i = 0; i < headers.Count; i++)
        {
            if (!int.TryParse(headers[i].Groups[1].Value, NumberStyles.Integer, Inv, out var filterIndex))
            {
                continue;
            }

            // The block runs from this header to the next one, or to the end.
            var start = headers[i].Index;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : stderr.Length;
            var block = stderr[start..end];

            var measurement = ParseEbur128(block);
            if (measurement is not null)
            {
                result[filterIndex] = measurement;
            }
        }

        return result;
    }

    /// <summary>Parses the ebur128 summary block.</summary>
    /// <param name="stderr">ffmpeg's stderr.</param>
    /// <returns>The measurement, or null when the block is missing.</returns>
    public static LoudnessMeasurement? ParseEbur128(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return null;
        }

        var i = MatchDouble(IntegratedRx, stderr);
        var lra = MatchDouble(LraRx, stderr);
        if (i is null || lra is null)
        {
            return null;
        }

        return new LoudnessMeasurement
        {
            IntegratedLufs = i.Value,
            LoudnessRangeLu = lra.Value,
            RangeLowLufs = MatchDouble(LraLowRx, stderr) ?? 0,
            RangeHighLufs = MatchDouble(LraHighRx, stderr) ?? 0,
            TruePeakDb = MatchTruePeak(stderr)
        };
    }

    /// <summary>Parses the JSON block loudnorm prints in analysis mode.</summary>
    /// <param name="stderr">ffmpeg's stderr.</param>
    /// <returns>The measurement; IsValid is false when it could not be read.</returns>
    public static LoudnormMeasurement ParseLoudnorm(string stderr)
    {
        var result = new LoudnormMeasurement();
        if (string.IsNullOrEmpty(stderr))
        {
            return result;
        }

        // Take the LAST JSON object: a chain may legitimately contain more than one loudnorm,
        // and ffmpeg prints them in order.
        var start = stderr.LastIndexOf('{');
        if (start < 0)
        {
            return result;
        }

        var end = stderr.IndexOf('}', start);
        if (end < 0)
        {
            return result;
        }

        var json = stderr.Substring(start, end - start + 1);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetDouble(root, "input_i", out var inI)
                || !TryGetDouble(root, "input_tp", out var inTp)
                || !TryGetDouble(root, "input_lra", out var inLra)
                || !TryGetDouble(root, "input_thresh", out var inThresh))
            {
                return result;
            }

            TryGetDouble(root, "target_offset", out var offset);

            // A silent or near-silent track measures as -inf and the second pass would then
            // apply nonsense gain. Treat it as unusable rather than producing a broken file.
            if (double.IsNegativeInfinity(inI) || double.IsNaN(inI) || inI < -70)
            {
                return result;
            }

            result.InputI = inI;
            result.InputTp = inTp;
            result.InputLra = inLra;
            result.InputThresh = inThresh;
            result.TargetOffset = double.IsNaN(offset) ? 0 : offset;
            result.IsValid = true;
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    private static bool TryGetDouble(JsonElement root, string name, out double value)
    {
        value = double.NaN;
        if (!root.TryGetProperty(name, out var el))
        {
            return false;
        }

        var raw = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw = raw.Trim();
        if (raw.Equals("-inf", StringComparison.OrdinalIgnoreCase))
        {
            value = double.NegativeInfinity;
            return true;
        }

        return double.TryParse(raw, NumberStyles.Float, Inv, out value);
    }

    private static double? MatchDouble(Regex rx, string text)
    {
        var m = rx.Match(text);
        if (!m.Success)
        {
            return null;
        }

        return double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out var v) ? v : null;
    }

    private static double MatchTruePeak(string text)
    {
        var m = TruePeakRx.Match(text);
        if (!m.Success)
        {
            return 0;
        }

        var raw = m.Groups[1].Value;
        if (raw.Equals("-inf", StringComparison.OrdinalIgnoreCase))
        {
            return -120;
        }

        return double.TryParse(raw, NumberStyles.Float, Inv, out var v) ? v : 0;
    }

    /// <summary>Picks the last few interesting lines of stderr for an error message.</summary>
    /// <param name="stderr">ffmpeg's stderr.</param>
    /// <param name="maxLines">How many lines to keep.</param>
    /// <returns>A short message.</returns>
    public static string Summarize(string stderr, int maxLines = 4)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "ffmpeg produced no output";
        }

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        for (var i = lines.Length - 1; i >= 0 && kept.Count < maxLines; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("frame=", StringComparison.Ordinal) || line.StartsWith("size=", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Insert(0, line);
        }

        return string.Join(" | ", kept);
    }
}
