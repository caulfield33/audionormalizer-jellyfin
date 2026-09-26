using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.AudioNormalizer.Configuration;

/// <summary>How the dynamic range is reduced.</summary>
public enum NormalizationEngine
{
    /// <summary>Window-based upward+downward normalization (ffmpeg dynaudnorm). Best default for films.</summary>
    Dynaudnorm = 0,

    /// <summary>Speech-oriented peak normalization (ffmpeg speechnorm).</summary>
    SpeechNorm = 1,

    /// <summary>Classic downward compressor (ffmpeg acompressor). Gentlest, keeps most dynamics.</summary>
    Compressor = 2,

    /// <summary>Loudness alignment only, no dynamic range reduction.</summary>
    LoudnessOnly = 3
}

/// <summary>What channel layout the generated track has.</summary>
public enum DownmixMode
{
    /// <summary>Dialogue-forward stereo downmix. Recommended for TV speakers, soundbars and headphones.</summary>
    DialogueStereo = 0,

    /// <summary>Plain stereo downmix using the decoder's default matrix.</summary>
    PlainStereo = 1,

    /// <summary>Keep the source channel layout, optionally boosting the centre channel.</summary>
    KeepLayout = 2
}

/// <summary>Codec of the generated track.</summary>
public enum OutputCodec
{
    /// <summary>AAC-LC. Widest client support, 21 ms encoder delay (declared in the container).</summary>
    Aac = 0,

    /// <summary>Enhanced AC-3. Sample-exact length, 5 ms delay, great TV support, not decodable in browsers.</summary>
    Eac3 = 1,

    /// <summary>AC-3. Sample-exact, universal on TVs and receivers, not decodable in browsers.</summary>
    Ac3 = 2,

    /// <summary>FLAC. Lossless, zero encoder delay, roughly 3-5x the size.</summary>
    Flac = 3,

    /// <summary>Opus. Smallest files, 8 ms delay, weaker support on older TVs.</summary>
    Opus = 4
}

/// <summary>Container the generated track is written into.</summary>
public enum OutputContainer
{
    /// <summary>Matroska audio. Carries language and title tags. Recommended.</summary>
    Mka = 0,

    /// <summary>MP4 audio. Carries language but drops the title tag.</summary>
    M4a = 1,

    /// <summary>Raw elementary stream. Carries no tags at all; metadata comes from the file name only.</summary>
    Raw = 2
}

/// <summary>
/// One set of normalization settings. Used both as the global default and as a per-item override.
/// </summary>
public class NormalizationProfile
{
    /// <summary>Gets or sets the loudness the generated track is aligned to, in LUFS.</summary>
    public double TargetLoudnessLufs { get; set; } = -16.0;

    /// <summary>Gets or sets the hard ceiling for the generated track, in dBTP.</summary>
    public double MaxTruePeakDb { get; set; } = -1.5;

    /// <summary>
    /// Gets or sets the dynamic range the generated track should end up with, in LU.
    /// This is the "difference between whispers and explosions" the plugin reports per item.
    /// </summary>
    public double TargetDynamicRangeLu { get; set; } = 9.0;

    /// <summary>Gets or sets which filter does the dynamic range work.</summary>
    public NormalizationEngine Engine { get; set; } = NormalizationEngine.Dynaudnorm;

    /// <summary>
    /// Gets or sets how hard the engine squeezes, 0-100. 0 leaves dynamics alone, 100 flattens
    /// everything. Ignored when <see cref="AutoStrength"/> is on.
    /// </summary>
    public int Strength { get; set; } = 55;

    /// <summary>
    /// Gets or sets a value indicating whether the strength is derived from the measured
    /// dynamic range of each item instead of the fixed <see cref="Strength"/> value.
    /// </summary>
    public bool AutoStrength { get; set; } = true;

    /// <summary>Gets or sets the channel layout of the generated track.</summary>
    public DownmixMode Downmix { get; set; } = DownmixMode.DialogueStereo;

    /// <summary>Gets or sets the centre channel gain in dB, applied when downmixing or boosting.</summary>
    public double CenterBoostDb { get; set; } = 3.0;

    /// <summary>Gets or sets the surround channel gain in dB applied during downmix.</summary>
    public double SurroundGainDb { get; set; } = -3.0;

    /// <summary>
    /// Gets or sets the LFE gain in dB applied during downmix. Muted by default: most of the
    /// "too loud" energy in an action scene lives here.
    /// </summary>
    public double LfeGainDb { get; set; } = -60.0;

    /// <summary>Gets or sets the codec of the generated track.</summary>
    public OutputCodec Codec { get; set; } = OutputCodec.Aac;

    /// <summary>Gets or sets the container of the generated track.</summary>
    public OutputContainer Container { get; set; } = OutputContainer.Mka;

    /// <summary>Gets or sets the bitrate in kbps. Ignored for FLAC.</summary>
    public int BitrateKbps { get; set; } = 256;

    /// <summary>Gets or sets the sample rate of the generated track. Always set explicitly:
    /// loudnorm silently resamples to 192 kHz otherwise, quadrupling the file.</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>
    /// Gets or sets a manual delay in milliseconds, positive to push the track later.
    /// Only needed if a client mishandles the encoder delay declared by the container.
    /// </summary>
    public int ManualDelayMs { get; set; }

    /// <summary>Gets or sets a value indicating whether the generated track is marked default.</summary>
    public bool MakeDefaultTrack { get; set; }

    /// <summary>
    /// Gets or sets the prefix put in front of every generated track's name, so normalized
    /// tracks are obvious in the player's audio list. "AN" produces "AN - Ukrainian 5.1".
    /// </summary>
    public string TrackTitlePrefix { get; set; } = "AN";

    /// <summary>
    /// Gets or sets a value indicating whether the source track's own name is appended after
    /// the prefix. Turn it off to get a plain "AN" on every track, which is only sensible
    /// when a single track per film is normalized.
    /// </summary>
    public bool AppendSourceNameToTitle { get; set; } = true;

    /// <summary>
    /// Gets or sets the name used after the prefix when the source track has nothing usable
    /// to derive one from.
    /// </summary>
    public string TrackTitle { get; set; } = "Normalized";

    /// <summary>
    /// Gets or sets the dynamic range below which an item is left alone, in LU.
    /// Content that is already even gains nothing from a second track.
    /// </summary>
    public double SkipIfDynamicRangeBelowLu { get; set; } = 7.0;

    /// <summary>Returns a copy, so per-item edits never mutate the global profile.</summary>
    public NormalizationProfile Clone() => (NormalizationProfile)MemberwiseClone();

    /// <summary>
    /// A stable fingerprint of every setting that changes the produced audio. Stored next to each
    /// generated track so the plugin knows to rebuild when the settings change, and to skip when
    /// they have not.
    /// </summary>
    [XmlIgnore]
    public string SignatureKey =>
        string.Join(
            '|',
            TargetLoudnessLufs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            MaxTruePeakDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            TargetDynamicRangeLu.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            (int)Engine,
            AutoStrength ? "auto" : Strength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (int)Downmix,
            CenterBoostDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            SurroundGainDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            LfeGainDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            (int)Codec,
            (int)Container,
            BitrateKbps,
            SampleRate,
            ManualDelayMs,
            TrackTitlePrefix,
            AppendSourceNameToTitle ? "src" : "fixed",
            TrackTitle);
}

/// <summary>A per-item override of the global profile.</summary>
public class ItemProfileOverride
{
    /// <summary>Gets or sets the item id, as a plain string so the XML config stays readable.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name, kept only so the config file is human-readable.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this item is excluded entirely.</summary>
    public bool Excluded { get; set; }

    /// <summary>
    /// Gets or sets the absolute ffprobe indexes of the audio tracks to normalize for this
    /// item. Empty means the automatic rule decides. This is how a film with several
    /// language tracks gets exactly the ones the user wants.
    /// </summary>
    public List<int> SelectedStreamIndexes { get; set; } = new List<int>();

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="SelectedStreamIndexes"/> is a
    /// deliberate choice. Needed to tell "the user picked nothing" apart from "the user has
    /// not looked at this film yet".
    /// </summary>
    public bool HasExplicitTrackSelection { get; set; }

    /// <summary>Gets or sets the profile used for this item.</summary>
    public NormalizationProfile Profile { get; set; } = new NormalizationProfile();
}
