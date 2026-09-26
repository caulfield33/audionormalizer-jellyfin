using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioNormalizer.Models;

/// <summary>EBU R128 numbers for one audio stream.</summary>
public class LoudnessMeasurement
{
    /// <summary>Gets or sets the integrated loudness, LUFS.</summary>
    public double IntegratedLufs { get; set; }

    /// <summary>Gets or sets the loudness range, LU. This is the quiet-to-loud spread.</summary>
    public double LoudnessRangeLu { get; set; }

    /// <summary>Gets or sets the bottom of the loudness range, LUFS. Roughly where dialogue sits.</summary>
    public double RangeLowLufs { get; set; }

    /// <summary>Gets or sets the top of the loudness range, LUFS. Roughly where the loud scenes sit.</summary>
    public double RangeHighLufs { get; set; }

    /// <summary>Gets or sets the true peak, dBTP.</summary>
    public double TruePeakDb { get; set; }

    /// <summary>Gets the peak-to-loudness ratio, dB. High values mean big sudden bangs.</summary>
    [JsonIgnore]
    public double PeakToLoudnessDb => TruePeakDb - IntegratedLufs;
}

/// <summary>The measured values loudnorm needs for its second pass.</summary>
public class LoudnormMeasurement
{
    /// <summary>Gets or sets the measured input loudness.</summary>
    public double InputI { get; set; }

    /// <summary>Gets or sets the measured input true peak.</summary>
    public double InputTp { get; set; }

    /// <summary>Gets or sets the measured input loudness range.</summary>
    public double InputLra { get; set; }

    /// <summary>Gets or sets the measured input threshold.</summary>
    public double InputThresh { get; set; }

    /// <summary>Gets or sets the target offset loudnorm reported.</summary>
    public double TargetOffset { get; set; }

    /// <summary>Gets or sets a value indicating whether the numbers are usable.</summary>
    public bool IsValid { get; set; }
}

/// <summary>
/// One audio stream of a film, everything the plugin knows about it, and the state of the
/// normalized copy built from it. A film can have several of these, and the user picks
/// which ones get normalized.
/// </summary>
public class AudioTrackInfo
{
    /// <summary>Gets or sets the absolute ffprobe stream index. This is what -map uses.</summary>
    public int StreamIndex { get; set; }

    /// <summary>Gets or sets a readable one-line description for the report.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec name.</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets the channel count.</summary>
    public int Channels { get; set; }

    /// <summary>Gets or sets the channel layout reported by the probe.</summary>
    public string? ChannelLayout { get; set; }

    /// <summary>Gets or sets the language, ISO 639-2 where the file provides one.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the track title from the container, if any.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether the source marks this track default.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether this looks like a commentary track.</summary>
    public bool IsCommentary { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this track is chosen for normalization.
    /// Reflects the user's per-item selection, or the automatic rule when they have not chosen.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>Gets or sets a value indicating whether the selection came from the user rather than the automatic rule.</summary>
    public bool SelectionIsExplicit { get; set; }

    /// <summary>Gets or sets the measurement of this source track.</summary>
    public LoudnessMeasurement? Source { get; set; }

    /// <summary>Gets or sets the measurement of the normalized copy, once verified.</summary>
    public LoudnessMeasurement? Result { get; set; }

    /// <summary>Gets or sets the path of the generated track.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets the size of the generated track in bytes.</summary>
    public long OutputSize { get; set; }

    /// <summary>Gets or sets the profile fingerprint the generated track was built with.</summary>
    public string? ProfileSignature { get; set; }

    /// <summary>Gets or sets the state of this track's normalized copy.</summary>
    public TrackState State { get; set; } = TrackState.Unknown;

    /// <summary>Gets or sets the last error for this track.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets why this track was skipped.</summary>
    public string? SkipReason { get; set; }

    /// <summary>Gets or sets when this track was last measured.</summary>
    public DateTime? AnalyzedUtc { get; set; }

    /// <summary>Gets or sets when its normalized copy was last written.</summary>
    public DateTime? GeneratedUtc { get; set; }
}

/// <summary>What the plugin knows about one library item.</summary>
public class TrackRecord
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name, for the report table.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind, for the report table.</summary>
    public string ItemKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the source media path.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the source file size when it was last looked at.</summary>
    public long SourceSize { get; set; }

    /// <summary>Gets or sets the source file timestamp when it was last looked at.</summary>
    public DateTime SourceModifiedUtc { get; set; }

    /// <summary>Gets or sets the item duration in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets every audio track of this item.</summary>
    public List<AudioTrackInfo> AudioTracks { get; set; } = new List<AudioTrackInfo>();

    /// <summary>Gets or sets an item-level problem, such as a missing file or an unwritable folder.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets an item-level reason for doing nothing.</summary>
    public string? SkipReason { get; set; }

    /// <summary>Gets or sets when the item's tracks were last measured.</summary>
    public DateTime? AnalyzedUtc { get; set; }

    /// <summary>Gets the tracks the user wants normalized.</summary>
    /// <returns>The selected tracks.</returns>
    public IEnumerable<AudioTrackInfo> SelectedTracks()
    {
        foreach (var t in AudioTracks)
        {
            if (t.Selected)
            {
                yield return t;
            }
        }
    }

    /// <summary>Finds a track by its stream index.</summary>
    /// <param name="streamIndex">The absolute ffprobe index.</param>
    /// <returns>The track, or null.</returns>
    public AudioTrackInfo? FindTrack(int streamIndex)
    {
        foreach (var t in AudioTracks)
        {
            if (t.StreamIndex == streamIndex)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>
    /// A single state for the whole item, for the collapsed report row: the least finished
    /// state among the selected tracks.
    /// </summary>
    /// <returns>The rolled-up state.</returns>
    public TrackState RollupState()
    {
        var any = false;
        var allDone = true;
        var anyRunning = false;
        var anyFailed = false;
        var anyQueued = false;

        foreach (var t in AudioTracks)
        {
            if (!t.Selected)
            {
                continue;
            }

            any = true;
            switch (t.State)
            {
                case TrackState.Running: anyRunning = true; allDone = false; break;
                case TrackState.Failed: anyFailed = true; allDone = false; break;
                case TrackState.Queued: anyQueued = true; allDone = false; break;
                case TrackState.Done: break;
                case TrackState.Skipped: break;
                default: allDone = false; break;
            }
        }

        if (!any)
        {
            return AudioTracks.Count > 0 ? TrackState.Skipped : TrackState.Unknown;
        }

        if (anyRunning)
        {
            return TrackState.Running;
        }

        if (anyFailed)
        {
            return TrackState.Failed;
        }

        if (anyQueued)
        {
            return TrackState.Queued;
        }

        return allDone ? TrackState.Done : TrackState.Analyzed;
    }
}

/// <summary>Lifecycle of a generated track.</summary>
public enum TrackState
{
    /// <summary>Never looked at.</summary>
    Unknown = 0,

    /// <summary>Source measured, nothing generated yet.</summary>
    Analyzed = 1,

    /// <summary>Waiting in the queue.</summary>
    Queued = 2,

    /// <summary>ffmpeg is running.</summary>
    Running = 3,

    /// <summary>Track written and verified.</summary>
    Done = 4,

    /// <summary>Deliberately left alone.</summary>
    Skipped = 5,

    /// <summary>Something went wrong.</summary>
    Failed = 6,

    /// <summary>Settings or source changed since the track was written.</summary>
    Stale = 7
}
