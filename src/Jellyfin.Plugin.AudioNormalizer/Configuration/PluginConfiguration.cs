using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AudioNormalizer.Configuration;

/// <summary>Which audio track of the source is used as the basis for the normalized one.</summary>
public enum SourceTrackSelection
{
    /// <summary>The track the server would play by default.</summary>
    DefaultTrack = 0,

    /// <summary>The track with the most channels, which is usually the full surround mix.</summary>
    MostChannels = 1,

    /// <summary>The first track matching <see cref="PluginConfiguration.PreferredSourceLanguages"/>.</summary>
    PreferredLanguage = 2
}

/// <summary>Plugin settings. Serialized to XML by the server, so keep every member simple.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the profile used for every item without an override.</summary>
    public NormalizationProfile GlobalProfile { get; set; } = new NormalizationProfile();

    /// <summary>Gets or sets the per-item overrides.</summary>
    public List<ItemProfileOverride> ItemOverrides { get; set; } = new List<ItemProfileOverride>();

    /// <summary>
    /// Gets or sets the libraries the scheduled tasks look at, by id. Empty means every
    /// movie and episode library.
    /// </summary>
    public List<string> EnabledLibraryIds { get; set; } = new List<string>();

    /// <summary>Gets or sets how many ffmpeg jobs may run at once.</summary>
    public int MaxParallelJobs { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether the queue pauses while somebody is watching
    /// something. Audio encoding is cheap per job but a library-wide run is not.
    /// </summary>
    public bool PauseWhilePlaybackActive { get; set; } = true;

    /// <summary>Gets or sets the process priority ffmpeg runs at, 0 normal to 19 lowest.</summary>
    public int ProcessNiceness { get; set; } = 10;

    /// <summary>Gets or sets which source track is normalized.</summary>
    public SourceTrackSelection SourceSelection { get; set; } = SourceTrackSelection.MostChannels;

    /// <summary>Gets or sets the preferred source languages, most preferred first, ISO 639-2.</summary>
    public List<string> PreferredSourceLanguages { get; set; } = new List<string> { "ukr", "eng" };

    /// <summary>
    /// Gets or sets a value indicating whether tracks whose title suggests commentary,
    /// description or narration are skipped as sources.
    /// </summary>
    public bool SkipCommentaryTracks { get; set; } = true;

    /// <summary>Gets or sets words that mark a track as commentary, matched case-insensitively.</summary>
    public List<string> CommentaryKeywords { get; set; } = new List<string>
    {
        "comment", "commentary", "director", "описов", "тифлокоментар", "audiodescription", "audio description", "described"
    };

    /// <summary>Gets or sets the shortest item worth processing, in minutes.</summary>
    public int MinimumDurationMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets the free space that must remain on the target volume after writing,
    /// in gigabytes. The job refuses to start if it would eat into this.
    /// </summary>
    public double MinimumFreeSpaceGb { get; set; } = 5.0;

    /// <summary>Gets or sets the cap on generated audio per run, in gigabytes. Zero means no cap.</summary>
    public double MaxTotalOutputGb { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether jobs only report what they would do.
    /// Nothing is written and nothing is refreshed.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin refreshes each item after writing,
    /// so the new track shows up without waiting for a library scan.
    /// </summary>
    public bool RefreshItemAfterGenerate { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an item is re-analyzed and rebuilt when the source
    /// file changes on disk (different size or timestamp).
    /// </summary>
    public bool RebuildWhenSourceChanges { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether orphaned generated tracks are deleted when the
    /// source file they belong to is gone. Only ever touches files this plugin wrote and recorded.
    /// </summary>
    public bool DeleteOrphanedTracks { get; set; } = true;

    /// <summary>Gets or sets the timeout for a single ffmpeg job, as a multiple of the item duration.</summary>
    public double JobTimeoutRealtimeFactor { get; set; } = 4.0;

    /// <summary>Gets or sets the floor for the job timeout, in minutes.</summary>
    public int JobTimeoutFloorMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether the full ffmpeg command line is written to the
    /// server log for every job. Off by default because the lines are long; turn it on when
    /// something misbehaves and you want to reproduce it by hand.
    /// </summary>
    public bool LogFfmpegCommands { get; set; }

    /// <summary>Gets the profile for an item, falling back to the global one.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>A copy of the effective profile, or null when the item is excluded.</returns>
    public NormalizationProfile? GetEffectiveProfile(Guid itemId)
    {
        var over = FindOverride(itemId);
        if (over is null)
        {
            return GlobalProfile.Clone();
        }

        return over.Excluded ? null : over.Profile.Clone();
    }

    /// <summary>Finds the override entry for an item, if any.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The override, or null.</returns>
    public ItemProfileOverride? FindOverride(Guid itemId)
    {
        var key = itemId.ToString("N");
        foreach (var o in ItemOverrides)
        {
            if (Guid.TryParse(o.ItemId, out var parsed) && parsed.ToString("N").Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return o;
            }
        }

        return null;
    }
}
