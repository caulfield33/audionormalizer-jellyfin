// Harness: drives the plugin's own FilterChainBuilder and OutputNaming so the shipped code,
// not a hand-typed command line, is what gets run against real audio.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Jellyfin.Plugin.AudioNormalizer.Configuration;
using Jellyfin.Plugin.AudioNormalizer.Models;
using Jellyfin.Plugin.AudioNormalizer.Services;

internal static class Program
{
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "chain";
        var channels = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 6;
        var range = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 25.4;

        var profile = new NormalizationProfile();

        // Optional overrides: engine, targetLra, strengthOrAuto, downmix
        if (args.Length > 3) { profile.Engine = Enum.Parse<NormalizationEngine>(args[3], true); }
        if (args.Length > 4) { profile.TargetDynamicRangeLu = double.Parse(args[4], CultureInfo.InvariantCulture); }
        if (args.Length > 5)
        {
            if (args[5].Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                profile.AutoStrength = true;
            }
            else
            {
                profile.AutoStrength = false;
                profile.Strength = int.Parse(args[5], CultureInfo.InvariantCulture);
            }
        }

        if (args.Length > 6) { profile.Downmix = Enum.Parse<DownmixMode>(args[6], true); }

        switch (mode)
        {
            case "analysis":
                Console.WriteLine(FilterChainBuilder.BuildAnalysisGraph(profile, channels, null, "0:a:0", range));
                return 0;

            case "chain":
            {
                var chain = new List<string>();
                chain.AddRange(FilterChainBuilder.BuildPreChain(profile, channels, null, range));
                chain.Add(FilterChainBuilder.BuildLoudnorm(profile, null));
                chain.AddRange(FilterChainBuilder.BuildPostChain(profile, range));
                Console.WriteLine(string.Join(',', chain));
                return 0;
            }

            case "chain2":
            {
                // Second pass, with measured values supplied on stdin as five numbers.
                var line = Console.ReadLine() ?? string.Empty;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                LoudnormMeasurement? measured = null;
                if (parts.Length >= 5)
                {
                    measured = new LoudnormMeasurement
                    {
                        InputI = double.Parse(parts[0], CultureInfo.InvariantCulture),
                        InputTp = double.Parse(parts[1], CultureInfo.InvariantCulture),
                        InputLra = double.Parse(parts[2], CultureInfo.InvariantCulture),
                        InputThresh = double.Parse(parts[3], CultureInfo.InvariantCulture),
                        TargetOffset = double.Parse(parts[4], CultureInfo.InvariantCulture),
                        IsValid = true
                    };
                }

                var chain = new List<string>();
                chain.AddRange(FilterChainBuilder.BuildPreChain(profile, channels, null, range));
                chain.Add(FilterChainBuilder.BuildLoudnorm(profile, measured));
                chain.AddRange(FilterChainBuilder.BuildPostChain(profile, range));
                Console.WriteLine(string.Join(',', chain));
                return 0;
            }

            case "cmd":
            {
                // Mirrors what SelfTest hands the user, including the quoting, so the promise
                // that it can be pasted into a shell is actually tested.
                var input = args.Length > 7 ? args[7] : "/tmp/in.mkv";
                var graph = FilterChainBuilder.BuildAnalysisGraph(profile, channels, null, "0:a:0", range);
                var argv = new List<string>
                {
                    "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
                    "-i", input,
                    "-filter_complex", graph,
                    "-map", "[measured]",
                    "-map", "[analyzed]",
                    "-f", "null", "-"
                };
                Console.WriteLine(NormalizationService.Quote("ffmpeg", argv));
                return 0;
            }

            case "multi":
            {
                // Full round trip: build the graph the plugin would use for a film with
                // several audio tracks, run it, and parse the results back per track.
                var input = args.Length > 7 ? args[7] : "/tmp/in.mkv";
                var indexes = (args.Length > 8 ? args[8] : "1,2,3")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => int.Parse(x, CultureInfo.InvariantCulture))
                    .ToList();

                var graph = FilterChainBuilder.BuildMultiTrackAnalysisGraph(indexes, out var labels);
                var argv = new List<string>
                {
                    "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
                    "-i", input,
                    "-filter_complex", graph
                };
                foreach (var l in labels)
                {
                    argv.Add("-map");
                    argv.Add("[" + l + "]");
                }

                argv.Add("-f");
                argv.Add("null");
                argv.Add("-");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (var a in argv) { psi.ArgumentList.Add(a); }

                using var proc = System.Diagnostics.Process.Start(psi)!;
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                var parsed = FfmpegOutputParser.ParseEbur128Multi(stderr);
                Console.WriteLine($"graph filters: {indexes.Count}, summaries parsed: {parsed.Count}");
                for (var i = 0; i < indexes.Count; i++)
                {
                    if (parsed.TryGetValue(i, out var m))
                    {
                        Console.WriteLine(
                            $"  filter {i} -> stream {indexes[i]}: {m.IntegratedLufs,7:F1} LUFS  range {m.LoudnessRangeLu,5:F1} LU  quiet {m.RangeLowLufs,7:F1}  loud {m.RangeHighLufs,7:F1}  peak {m.TruePeakDb,6:F1} dBTP");
                    }
                    else
                    {
                        Console.WriteLine($"  filter {i} -> stream {indexes[i]}: NO RESULT");
                    }
                }

                return 0;
            }

            case "names":
            {
                // Several tracks of one film must never produce the same file name.
                var path = args.Length > 7 ? args[7] : "/movies/Film (2021)/Film (2021).mkv";
                var tracks = new List<AudioTrackInfo>
                {
                    new() { StreamIndex = 1, Channels = 6, Language = "ukr", Title = "Ukrainian 5.1" },
                    new() { StreamIndex = 2, Channels = 2, Language = "eng", Title = "English Stereo" },
                    new() { StreamIndex = 3, Channels = 2, Language = "eng", Title = "English Stereo" },
                    new() { StreamIndex = 4, Channels = 6, Language = "pol" },
                    // Flag-word traps: Jellyfin matches default/forced/foreign as substrings.
                    new() { StreamIndex = 5, Channels = 2, Language = "hi",  Title = "default" },
                    new() { StreamIndex = 6, Channels = 2, Language = "fra", Title = "Forced English" },
                    new() { StreamIndex = 7, Channels = 2, Language = "spa", Title = "Foreign parts" },
                    new() { StreamIndex = 8, Channels = 2, Language = "deu", Title = "sdh" },
                    new() { StreamIndex = 9, Channels = 2, Language = "ita", Title = "Commentary.with.dots" }
                };
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in tracks)
                {
                    var outPath = OutputNaming.BuildOutputPath(path, profile, t, taken);
                    Console.WriteLine($"  stream {t.StreamIndex}: title \"{OutputNaming.BuildTitle(profile, t)}\"");
                    Console.WriteLine($"             -> {System.IO.Path.GetFileName(outPath)}");
                }

                return 0;
            }

            case "gain":
                Console.WriteLine(FilterChainBuilder.ResolveMaxGain(profile, range).ToString(CultureInfo.InvariantCulture));
                return 0;

            case "name":
            {
                var path = args.Length > 7 ? args[7] : "/movies/Some Movie (2021)/Some Movie (2021) [1080p].mkv";
                var lang = args.Length > 8 ? args[8] : "ukr";
                var track = new AudioTrackInfo { StreamIndex = 1, Channels = 6, Language = lang };
                var outPath = OutputNaming.BuildOutputPath(path, profile, track);
                Console.WriteLine(outPath);
                Console.WriteLine(OutputNaming.TempPathFor(outPath));
                return 0;
            }

            case "nametest":
            {
                string[] titles = { "Normalized", "default", "eng", "Night.Mode", "Нормалізований", "", "forced" };
                foreach (var t in titles)
                {
                    Console.WriteLine($"{t,-20} -> {OutputNaming.SanitizeTitle(t)}");
                }

                return 0;
            }

            default:
                Console.Error.WriteLine("unknown mode");
                return 1;
        }
    }
}
