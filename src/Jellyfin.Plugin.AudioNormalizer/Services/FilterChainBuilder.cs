using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.AudioNormalizer.Configuration;
using Jellyfin.Plugin.AudioNormalizer.Models;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>
/// Builds the ffmpeg filter graphs.
///
/// The ordering here is deliberate and was arrived at by measurement, not taste:
///
/// 1. Downmix first, with a matrix scaled so the coefficients can never sum above 1.0.
///    An unscaled dialogue-forward matrix clips: measured +6.2 dBTP on a test mix.
/// 2. Window-based dynamics (dynaudnorm / speechnorm) go BEFORE loudnorm. They are
///    level-independent, so the attenuated downmix does not bother them.
/// 3. A classic compressor goes AFTER loudnorm instead, because its threshold is an
///    absolute dBFS value and is meaningless against an attenuated signal. Measured:
///    the same compressor placed before loudnorm did nothing at all.
/// 4. loudnorm runs in linear mode with measured values, so it applies one predictable
///    gain rather than pumping.
/// 5. alimiter last, as the hard ceiling.
///
/// The sample rate is always forced by the caller. Left alone, loudnorm resamples its
/// output to 192 kHz and the file comes out four times larger.
/// </summary>
public static class FilterChainBuilder
{
    /// <summary>Filters the plugin may need, for capability checks.</summary>
    public static readonly string[] RequiredFilters = { "loudnorm", "alimiter", "ebur128", "pan", "aresample" };

    /// <summary>
    /// Headroom in dB between the sample-peak limiter and the requested true-peak ceiling,
    /// covering inter-sample overshoot.
    /// </summary>
    private const double TruePeakSafetyDb = 0.3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Builds a graph that measures several audio tracks in one decode, so the report can
    /// show numbers for every track of a film without running ffmpeg once per track.
    ///
    /// The caller must remember which filter index belongs to which stream: ffmpeg prints
    /// the summaries out of order, so they are matched by the number in "Parsed_ebur128_N",
    /// which is the filter's position here.
    /// </summary>
    /// <param name="streamIndexes">Absolute ffprobe indexes to measure, in order.</param>
    /// <param name="outputLabels">Receives the graph output labels to map.</param>
    /// <returns>The filter_complex string.</returns>
    public static string BuildMultiTrackAnalysisGraph(
        IReadOnlyList<int> streamIndexes,
        out List<string> outputLabels)
    {
        outputLabels = new List<string>(streamIndexes.Count);
        var sb = new StringBuilder();

        for (var i = 0; i < streamIndexes.Count; i++)
        {
            var label = "m" + i.ToString(Inv);
            outputLabels.Add(label);

            if (i > 0)
            {
                sb.Append(';');
            }

            sb.Append(string.Create(
                Inv,
                $"[0:{streamIndexes[i]}]ebur128=peak=true:framelog=quiet[{label}]"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the part of the chain that runs before loudnorm.
    /// </summary>
    /// <param name="profile">Settings.</param>
    /// <param name="sourceChannels">Channel count of the source track.</param>
    /// <param name="sourceLayout">Channel layout string from the probe, may be null.</param>
    /// <param name="measuredRangeLu">Measured source loudness range, used by auto strength.</param>
    /// <returns>Filter fragments, possibly empty.</returns>
    public static List<string> BuildPreChain(
        NormalizationProfile profile,
        int sourceChannels,
        string? sourceLayout,
        double measuredRangeLu)
    {
        var chain = new List<string>();

        var mix = BuildChannelStage(profile, sourceChannels, sourceLayout);
        if (mix is not null)
        {
            chain.Add(mix);
        }

        switch (profile.Engine)
        {
            case NormalizationEngine.Dynaudnorm:
                chain.Add(BuildDynaudnorm(profile, measuredRangeLu));
                break;
            case NormalizationEngine.SpeechNorm:
                chain.Add(BuildSpeechNorm(profile, measuredRangeLu));
                break;
            case NormalizationEngine.Compressor:
            case NormalizationEngine.LoudnessOnly:
            default:
                break;
        }

        return chain;
    }

    /// <summary>Builds the part of the chain that runs after loudnorm.</summary>
    /// <param name="profile">Settings.</param>
    /// <param name="measuredRangeLu">Measured source loudness range.</param>
    /// <returns>Filter fragments.</returns>
    public static List<string> BuildPostChain(NormalizationProfile profile, double measuredRangeLu)
    {
        var chain = new List<string>();

        if (profile.Engine == NormalizationEngine.Compressor)
        {
            chain.Add(BuildCompressor(profile, measuredRangeLu));
        }

        // Hard ceiling. alimiter's limit is a linear SAMPLE-peak value, but the ceiling the user
        // asked for is a TRUE peak, and inter-sample peaks sit above sample peaks. Measured on a
        // test mix: limiting at exactly -1.5 dBFS produced -1.3 dBTP. A small margin keeps the
        // finished track at or under what was asked for.
        var limit = Math.Pow(10.0, (profile.MaxTruePeakDb - TruePeakSafetyDb) / 20.0);
        chain.Add(string.Create(
            Inv,
            $"alimiter=limit={limit.ToString("F5", Inv)}:level=false:attack=5:release=50"));

        if (profile.ManualDelayMs > 0)
        {
            chain.Add(string.Create(Inv, $"adelay=delays={profile.ManualDelayMs}:all=1"));
        }
        else if (profile.ManualDelayMs < 0)
        {
            var seconds = (-profile.ManualDelayMs) / 1000.0;
            chain.Add(string.Create(Inv, $"atrim=start={seconds.ToString("F4", Inv)},asetpts=PTS-STARTPTS"));
        }

        // Guard against the loudnorm 192 kHz surprise even if the caller forgets -ar.
        chain.Add(string.Create(Inv, $"aresample={profile.SampleRate}:resampler=soxr"));

        return chain;
    }

    /// <summary>
    /// Builds the single-decode analysis graph: it measures the untouched source for the report
    /// and, in the same pass, measures what loudnorm needs for the encode. Verified to produce
    /// numbers identical to running the two separately, at half the I/O.
    /// </summary>
    /// <param name="profile">Settings.</param>
    /// <param name="sourceChannels">Source channel count.</param>
    /// <param name="sourceLayout">Source channel layout.</param>
    /// <param name="streamSpecifier">Input stream specifier, for example "0:1".</param>
    /// <param name="roughRangeLu">A first guess at the loudness range, for auto strength.</param>
    /// <returns>The filter_complex string.</returns>
    public static string BuildAnalysisGraph(
        NormalizationProfile profile,
        int sourceChannels,
        string? sourceLayout,
        string streamSpecifier,
        double roughRangeLu)
    {
        var pre = BuildPreChain(profile, sourceChannels, sourceLayout, roughRangeLu);
        var sb = new StringBuilder();
        sb.Append('[').Append(streamSpecifier).Append(']').Append("asplit=2[src][proc];");
        sb.Append("[src]ebur128=peak=true:framelog=quiet[measured];");
        sb.Append("[proc]");
        foreach (var f in pre)
        {
            sb.Append(f).Append(',');
        }

        sb.Append(string.Create(
            Inv,
            $"loudnorm=I={profile.TargetLoudnessLufs.ToString("F2", Inv)}:TP={profile.MaxTruePeakDb.ToString("F2", Inv)}:LRA={LoudnormLra(profile).ToString("F2", Inv)}:print_format=json[analyzed]"));

        return sb.ToString();
    }

    /// <summary>Builds the loudnorm stage for the encode pass, using the measured values.</summary>
    /// <param name="profile">Settings.</param>
    /// <param name="measured">Values from the analysis pass.</param>
    /// <returns>The loudnorm filter fragment.</returns>
    public static string BuildLoudnorm(NormalizationProfile profile, LoudnormMeasurement? measured)
    {
        var lra = LoudnormLra(profile);
        var sb = new StringBuilder();
        sb.Append(string.Create(
            Inv,
            $"loudnorm=I={profile.TargetLoudnessLufs.ToString("F2", Inv)}:TP={profile.MaxTruePeakDb.ToString("F2", Inv)}:LRA={lra.ToString("F2", Inv)}"));

        if (measured is { IsValid: true })
        {
            // Linear mode applies one gain across the whole file. The dynamics work has already
            // been done by the stage before this, so a second dynamic pass would only pump.
            sb.Append(string.Create(
                Inv,
                $":linear=true:measured_I={measured.InputI.ToString("F2", Inv)}:measured_TP={measured.InputTp.ToString("F2", Inv)}:measured_LRA={measured.InputLra.ToString("F2", Inv)}:measured_thresh={measured.InputThresh.ToString("F2", Inv)}:offset={measured.TargetOffset.ToString("F2", Inv)}"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Translates the strength knob into dynaudnorm's max gain factor, which is the parameter
    /// that actually decides how much quiet dialogue is lifted.
    /// </summary>
    /// <param name="profile">Settings.</param>
    /// <param name="measuredRangeLu">Measured source loudness range.</param>
    /// <returns>The max gain factor.</returns>
    public static double ResolveMaxGain(NormalizationProfile profile, double measuredRangeLu)
    {
        if (!profile.AutoStrength)
        {
            // 0 -> 2 (barely anything), 100 -> 20 (as flat as dynaudnorm goes).
            var t = Math.Clamp(profile.Strength, 0, 100) / 100.0;
            return Math.Round(2.0 + (Math.Pow(t, 1.4) * 18.0), 1);
        }

        // Auto: pick from how much range has to disappear. The mapping is a measured
        // approximation, not a guarantee; the per-item report exists so it can be overridden.
        var needed = measuredRangeLu - profile.TargetDynamicRangeLu;
        if (needed <= 2)
        {
            return 2;
        }

        double[] reduction = { 2, 4, 8, 12, 15, 17 };
        double[] gain = { 2, 4, 6, 8, 12, 16 };
        for (var i = 1; i < reduction.Length; i++)
        {
            if (needed <= reduction[i])
            {
                var span = reduction[i] - reduction[i - 1];
                var f = span <= 0 ? 0 : (needed - reduction[i - 1]) / span;
                return Math.Round(gain[i - 1] + (f * (gain[i] - gain[i - 1])), 1);
            }
        }

        return 16;
    }

    private static double LoudnormLra(NormalizationProfile profile)
    {
        // loudnorm refuses values outside 1..50 and silently drops to dynamic mode when the
        // input range exceeds the target. Since the stage before it already handled dynamics,
        // ask loudnorm for a wide range so it stays linear.
        var lra = profile.Engine == NormalizationEngine.LoudnessOnly
            ? profile.TargetDynamicRangeLu
            : 20.0;
        return Math.Clamp(lra, 1.0, 50.0);
    }

    private static string BuildDynaudnorm(NormalizationProfile profile, double measuredRangeLu)
    {
        var m = ResolveMaxGain(profile, measuredRangeLu);

        // g must be odd; ffmpeg rejects even values outright.
        var g = 21;
        var p = 0.85;
        return string.Create(
            Inv,
            $"dynaudnorm=f=220:g={g}:p={p.ToString("F2", Inv)}:m={m.ToString("F1", Inv)}:s=11:b=1");
    }

    private static string BuildSpeechNorm(NormalizationProfile profile, double measuredRangeLu)
    {
        var m = ResolveMaxGain(profile, measuredRangeLu);
        var e = Math.Clamp(m, 1.0, 50.0);
        return string.Create(
            Inv,
            $"speechnorm=e={e.ToString("F1", Inv)}:r=0.0005:l=1");
    }

    private static string BuildCompressor(NormalizationProfile profile, double measuredRangeLu)
    {
        // Runs after loudnorm, so the signal already sits at the target loudness and an
        // absolute threshold means something.
        var m = ResolveMaxGain(profile, measuredRangeLu);
        var ratio = Math.Clamp(1.5 + (m / 4.0), 1.5, 12.0);
        var threshold = Math.Clamp(profile.TargetLoudnessLufs - 4.0, -40.0, -6.0);
        var makeup = Math.Clamp(m / 4.0, 1.0, 8.0);
        return string.Create(
            Inv,
            $"acompressor=threshold={threshold.ToString("F1", Inv)}dB:ratio={ratio.ToString("F1", Inv)}:attack=25:release=350:knee=8:makeup={makeup.ToString("F1", Inv)}:detection=rms");
    }

    private static string? BuildChannelStage(NormalizationProfile profile, int channels, string? layout)
    {
        if (profile.Downmix == DownmixMode.PlainStereo)
        {
            return channels > 2 ? "pan=stereo|FL=FL|FR=FR" : null;
        }

        if (profile.Downmix == DownmixMode.KeepLayout)
        {
            return BuildCenterBoost(profile, channels, layout);
        }

        if (channels <= 2)
        {
            // Already stereo or mono: nothing to downmix, and a pan here would only risk clipping.
            return null;
        }

        return BuildDialogueDownmix(profile, channels, layout);
    }

    private static string BuildDialogueDownmix(NormalizationProfile profile, int channels, string? layout)
    {
        var front = 1.0;
        var center = Db(profile.CenterBoostDb);
        var surround = Db(profile.SurroundGainDb);
        var lfe = Db(profile.LfeGainDb);

        var hasLfe = channels >= 6 || (layout?.Contains("LFE", StringComparison.OrdinalIgnoreCase) ?? false);
        var hasSurround = channels >= 5;
        if (!hasLfe)
        {
            lfe = 0;
        }

        if (!hasSurround)
        {
            surround = 0;
        }

        // Scale the whole matrix so the coefficients on one output cannot sum past unity.
        // Without this the downmix clips before anything else in the chain sees it.
        var sum = front + center + surround + lfe;
        if (sum <= 0)
        {
            return "pan=stereo|FL=FL|FR=FR";
        }

        var f = front / sum;
        var c = center / sum;
        var s = surround / sum;
        var l = lfe / sum;

        var left = new StringBuilder();
        var right = new StringBuilder();
        left.Append(string.Create(Inv, $"{f.ToString("F4", Inv)}*FL"));
        right.Append(string.Create(Inv, $"{f.ToString("F4", Inv)}*FR"));

        if (c > 0)
        {
            left.Append(string.Create(Inv, $"+{c.ToString("F4", Inv)}*FC"));
            right.Append(string.Create(Inv, $"+{c.ToString("F4", Inv)}*FC"));
        }

        if (hasSurround && s > 0)
        {
            var backLeft = channels >= 7 ? "SL" : "BL";
            var backRight = channels >= 7 ? "SR" : "BR";
            left.Append(string.Create(Inv, $"+{s.ToString("F4", Inv)}*{backLeft}"));
            right.Append(string.Create(Inv, $"+{s.ToString("F4", Inv)}*{backRight}"));
        }

        if (hasLfe && l > 0.0005)
        {
            left.Append(string.Create(Inv, $"+{l.ToString("F4", Inv)}*LFE"));
            right.Append(string.Create(Inv, $"+{l.ToString("F4", Inv)}*LFE"));
        }

        return "pan=stereo|FL=" + left + "|FR=" + right;
    }

    private static string? BuildCenterBoost(NormalizationProfile profile, int channels, string? layout)
    {
        if (channels < 5 || Math.Abs(profile.CenterBoostDb) < 0.01)
        {
            return null;
        }

        var c = Db(profile.CenterBoostDb);
        var lfe = Db(profile.LfeGainDb);

        // Scale everything down by the loudest coefficient so boosting the centre cannot clip.
        var peak = Math.Max(1.0, Math.Max(c, lfe));
        var n = 1.0 / peak;
        var cn = c / peak;
        var ln = lfe / peak;

        if (channels == 6)
        {
            return string.Create(
                Inv,
                $"pan=5.1|FL={n.ToString("F4", Inv)}*FL|FR={n.ToString("F4", Inv)}*FR|FC={cn.ToString("F4", Inv)}*FC|LFE={ln.ToString("F4", Inv)}*LFE|BL={n.ToString("F4", Inv)}*BL|BR={n.ToString("F4", Inv)}*BR");
        }

        if (channels == 8)
        {
            return string.Create(
                Inv,
                $"pan=7.1|FL={n.ToString("F4", Inv)}*FL|FR={n.ToString("F4", Inv)}*FR|FC={cn.ToString("F4", Inv)}*FC|LFE={ln.ToString("F4", Inv)}*LFE|BL={n.ToString("F4", Inv)}*BL|BR={n.ToString("F4", Inv)}*BR|SL={n.ToString("F4", Inv)}*SL|SR={n.ToString("F4", Inv)}*SR");
        }

        // Unusual layout: leave it alone rather than guessing a matrix and mangling channels.
        return null;
    }

    private static double Db(double db) => db <= -59.9 ? 0.0 : Math.Pow(10.0, db / 20.0);
}
