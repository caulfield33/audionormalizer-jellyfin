using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioNormalizer.Configuration;
using Jellyfin.Plugin.AudioNormalizer.Models;
using Jellyfin.Plugin.AudioNormalizer.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioNormalizer.Api;

/// <summary>One audio track of a film, as the report shows it.</summary>
public class TrackRow
{
    /// <summary>Gets or sets the absolute ffprobe stream index.</summary>
    public int StreamIndex { get; set; }

    /// <summary>Gets or sets the readable description.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the channel count.</summary>
    public int Channels { get; set; }

    /// <summary>Gets or sets a value indicating whether the source marks this track default.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether this looks like a commentary track.</summary>
    public bool IsCommentary { get; set; }

    /// <summary>Gets or sets a value indicating whether this track is picked for normalization.</summary>
    public bool Selected { get; set; }

    /// <summary>Gets or sets a value indicating whether the user chose, rather than the automatic rule.</summary>
    public bool SelectionIsExplicit { get; set; }

    /// <summary>Gets or sets the measured integrated loudness.</summary>
    public double? SourceLufs { get; set; }

    /// <summary>Gets or sets the measured dynamic range.</summary>
    public double? SourceRangeLu { get; set; }

    /// <summary>Gets or sets the quiet end of the range.</summary>
    public double? SourceLowLufs { get; set; }

    /// <summary>Gets or sets the loud end of the range.</summary>
    public double? SourceHighLufs { get; set; }

    /// <summary>Gets or sets the measured true peak.</summary>
    public double? SourcePeakDb { get; set; }

    /// <summary>Gets or sets a value indicating whether the numbers come from a quick scan.</summary>
    public bool SourceIsEstimate { get; set; }

    /// <summary>Gets or sets the dynamic range of the generated track.</summary>
    public double? ResultRangeLu { get; set; }

    /// <summary>Gets or sets the name the generated track will carry in the player.</summary>
    public string PlannedTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the state of this track's normalized copy.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the generated file size in megabytes.</summary>
    public double OutputMb { get; set; }

    /// <summary>Gets or sets the generated file path.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets the last error or skip reason.</summary>
    public string? Note { get; set; }
}

/// <summary>One row of the report shown on the settings page.</summary>
public class ReportRow
{
    /// <summary>Gets or sets the item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the runtime in minutes.</summary>
    public double Minutes { get; set; }

    /// <summary>Gets or sets the source track description.</summary>
    public string SourceTrack { get; set; } = string.Empty;

    /// <summary>Gets or sets the measured integrated loudness.</summary>
    public double? SourceLufs { get; set; }

    /// <summary>Gets or sets the measured dynamic range.</summary>
    public double? SourceRangeLu { get; set; }

    /// <summary>Gets or sets the quiet end of the source's loudness range, LUFS.</summary>
    public double? SourceLowLufs { get; set; }

    /// <summary>Gets or sets the loud end of the source's loudness range, LUFS.</summary>
    public double? SourceHighLufs { get; set; }

    /// <summary>Gets or sets the measured true peak.</summary>
    public double? SourcePeakDb { get; set; }

    /// <summary>Gets or sets a value indicating whether the numbers come from a quick scan.</summary>
    public bool SourceIsEstimate { get; set; }

    /// <summary>Gets or sets the resulting loudness of the generated track.</summary>
    public double? ResultLufs { get; set; }

    /// <summary>Gets or sets the resulting dynamic range of the generated track.</summary>
    public double? ResultRangeLu { get; set; }

    /// <summary>Gets or sets how much the range shrank, in LU.</summary>
    public double? RangeImprovement { get; set; }

    /// <summary>Gets or sets the state.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the generated file size in megabytes.</summary>
    public double OutputMb { get; set; }

    /// <summary>Gets or sets the generated file path.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets a value indicating whether this item has its own settings.</summary>
    public bool HasOverride { get; set; }

    /// <summary>Gets or sets a value indicating whether this item is excluded.</summary>
    public bool Excluded { get; set; }

    /// <summary>Gets or sets the last error or skip reason.</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets every audio track of this film.</summary>
    public List<TrackRow> Tracks { get; set; } = new List<TrackRow>();

    /// <summary>Gets or sets how many tracks are picked for normalization.</summary>
    public int SelectedCount { get; set; }
}

/// <summary>Body for setting which audio tracks of an item get normalized.</summary>
public class TrackSelectionRequest
{
    /// <summary>Gets or sets the item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute ffprobe indexes to normalize. Empty means none.</summary>
    public List<int> StreamIndexes { get; set; } = new List<int>();

    /// <summary>
    /// Gets or sets a value indicating whether to forget the explicit choice and go back to
    /// the automatic rule.
    /// </summary>
    public bool ResetToAutomatic { get; set; }
}

/// <summary>Body for the queue endpoints.</summary>
public class QueueRequest
{
    /// <summary>Gets or sets the items to work on. Empty means every candidate.</summary>
    public List<string> ItemIds { get; set; } = new List<string>();

    /// <summary>Gets or sets a value indicating whether existing tracks are rebuilt.</summary>
    public bool Force { get; set; }
}

/// <summary>Body for setting a per-item override.</summary>
public class OverrideRequest
{
    /// <summary>Gets or sets the item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the item is excluded.</summary>
    public bool Excluded { get; set; }

    /// <summary>Gets or sets the profile. Null keeps the global one.</summary>
    public NormalizationProfile? Profile { get; set; }
}

/// <summary>Everything the page needs to tell the user whether the setup will work.</summary>
public class DiagnosticsResult
{
    /// <summary>Gets or sets the ffmpeg binary in use.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the ffmpeg version.</summary>
    public string FfmpegVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets which required filters are present.</summary>
    public Dictionary<string, bool> Filters { get; set; } = new Dictionary<string, bool>();

    /// <summary>Gets or sets warnings worth showing.</summary>
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>Gets or sets how many items the plugin would consider.</summary>
    public int CandidateCount { get; set; }
}

/// <summary>REST surface for the settings page.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("AudioNormalizer")]
[Produces(MediaTypeNames.Application.Json)]
public class AudioNormalizerController : ControllerBase
{
    private readonly ILogger<AudioNormalizerController> _logger;
    private readonly StateStore _store;
    private readonly JobQueue _queue;
    private readonly NormalizationService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly FfmpegRunner _ffmpeg;

    /// <summary>Initializes a new instance of the <see cref="AudioNormalizerController"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="store">State store.</param>
    /// <param name="queue">Job queue.</param>
    /// <param name="service">Worker.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="ffmpeg">ffmpeg wrapper.</param>
    public AudioNormalizerController(
        ILogger<AudioNormalizerController> logger,
        StateStore store,
        JobQueue queue,
        NormalizationService service,
        ILibraryManager libraryManager,
        FfmpegRunner ffmpeg)
    {
        _logger = logger;
        _store = store;
        _queue = queue;
        _service = service;
        _libraryManager = libraryManager;
        _ffmpeg = ffmpeg;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets the per-item report.</summary>
    /// <param name="onlyProblems">Only return items whose dynamic range exceeds the target.</param>
    /// <returns>The rows.</returns>
    [HttpGet("Report")]
    public ActionResult<IEnumerable<ReportRow>> GetReport([FromQuery] bool onlyProblems = false)
    {
        EnsureLoaded();
        var config = Config;
        var rows = new List<ReportRow>();
        var listed = new HashSet<Guid>();

        // Candidates first, including films nothing has been done to yet. Their audio tracks
        // come out of Jellyfin's own stream metadata and cost no ffmpeg, so a film can be
        // expanded and its tracks ticked before anything is measured. The report used to be
        // built from the store alone, which left a film invisible until after it had been
        // scanned - exactly backwards for deciding what to scan.
        foreach (var item in _service.GetCandidateItems())
        {
            listed.Add(item.Id);

            var record = _store.Get(item.Id);
            var over = config.FindOverride(item.Id);
            var profile = over?.Profile ?? config.GlobalProfile;

            var row = BuildReportRow(
                item.Id,
                string.IsNullOrEmpty(record?.ItemName) ? item.Name ?? string.Empty : record.ItemName,
                string.IsNullOrEmpty(record?.ItemKind) ? item.GetType().Name : record.ItemKind,
                record is not null && record.DurationSeconds > 0
                    ? record.DurationSeconds
                    : (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond,
                record,
                over,
                profile);

            if (onlyProblems && (!row.SourceRangeLu.HasValue || row.SourceRangeLu.Value <= profile.TargetDynamicRangeLu))
            {
                continue;
            }

            rows.Add(row);
        }

        // Records whose item is no longer a candidate: gone from the library, moved out of an
        // enabled folder, or now under the duration threshold. They stay listed so the tracks
        // they generated remain visible and deletable.
        foreach (var record in _store.All())
        {
            if (!Guid.TryParse(record.ItemId, out var id) || listed.Contains(id))
            {
                continue;
            }

            var over = config.FindOverride(id);
            var profile = over?.Profile ?? config.GlobalProfile;
            var row = BuildReportRow(id, record.ItemName, record.ItemKind, record.DurationSeconds, record, over, profile);

            if (onlyProblems && (!row.SourceRangeLu.HasValue || row.SourceRangeLu.Value <= profile.TargetDynamicRangeLu))
            {
                continue;
            }

            rows.Add(row);
        }

        return Ok(rows.OrderByDescending(r => r.SourceRangeLu ?? -1).ToList());
    }

    /// <summary>
    /// Builds one report row. <paramref name="record"/> is null for a film that has never been
    /// measured; its tracks then come from Jellyfin's stream metadata instead of the store.
    /// </summary>
    private ReportRow BuildReportRow(
        Guid id,
        string name,
        string kind,
        double durationSeconds,
        TrackRecord? record,
        ItemProfileOverride? over,
        NormalizationProfile profile)
    {
        var row = new ReportRow
        {
            ItemId = id.ToString("N"),
            Name = name,
            Kind = kind,
            Minutes = Math.Round(durationSeconds / 60.0, 1),

            // "NotMeasured" is a report-only state; the stored TrackState enum has no such
            // member because nothing is stored for a film that was never touched.
            State = record is not null ? record.RollupState().ToString() : "NotMeasured",
            HasOverride = over is not null,
            Excluded = over?.Excluded ?? false,
            Note = record?.LastError ?? record?.SkipReason
        };

        IReadOnlyList<AudioTrackInfo> tracks = record is not null && record.AudioTracks.Count > 0
            ? record.AudioTracks
            : _service.PeekTracks(id);

        // Names are allocated per item so two tracks of one film never collide, and the
        // report shows exactly what the player will display.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            var plannedTitle = OutputNaming.BuildTitle(profile, track);
            if (!taken.Add(plannedTitle))
            {
                plannedTitle += "-" + track.StreamIndex.ToString(CultureInfo.InvariantCulture);
                taken.Add(plannedTitle);
            }

            row.Tracks.Add(new TrackRow
            {
                StreamIndex = track.StreamIndex,
                Label = track.Label,
                Language = track.Language,
                Channels = track.Channels,
                IsDefault = track.IsDefault,
                IsCommentary = track.IsCommentary,
                Selected = track.Selected,
                SelectionIsExplicit = track.SelectionIsExplicit,
                SourceLufs = track.Source?.IntegratedLufs,
                SourceRangeLu = track.Source?.LoudnessRangeLu,
                SourceLowLufs = track.Source?.RangeLowLufs,
                SourceHighLufs = track.Source?.RangeHighLufs,
                SourcePeakDb = track.Source?.TruePeakDb,
                SourceIsEstimate = track.Source?.IsEstimate ?? false,
                ResultRangeLu = track.Result?.LoudnessRangeLu,
                PlannedTitle = plannedTitle,
                State = track.State.ToString(),
                OutputMb = Math.Round(track.OutputSize / 1024.0 / 1024.0, 1),
                OutputPath = track.OutputPath,
                Note = track.LastError ?? track.SkipReason
            });
        }

        row.SelectedCount = row.Tracks.Count(t => t.Selected);

        // The collapsed row summarises the selected tracks, falling back to the worst
        // track so a film with nothing selected still shows why it might be worth doing.
        var headline = row.Tracks.Where(t => t.Selected && t.SourceRangeLu.HasValue).ToList();
        if (headline.Count == 0)
        {
            headline = row.Tracks.Where(t => t.SourceRangeLu.HasValue).ToList();
        }

        var worst = headline.OrderByDescending(t => t.SourceRangeLu ?? -1).FirstOrDefault();
        if (worst is not null)
        {
            row.SourceTrack = worst.Label;
            row.SourceLufs = worst.SourceLufs;
            row.SourceRangeLu = worst.SourceRangeLu;
            row.SourceLowLufs = worst.SourceLowLufs;
            row.SourceHighLufs = worst.SourceHighLufs;
            row.SourcePeakDb = worst.SourcePeakDb;
            row.SourceIsEstimate = worst.SourceIsEstimate;
            row.ResultRangeLu = worst.ResultRangeLu;
            row.OutputMb = Math.Round(row.Tracks.Sum(t => t.OutputMb), 1);
            row.OutputPath = row.Tracks.FirstOrDefault(t => t.OutputPath is not null)?.OutputPath;
        }
        else
        {
            // Nothing measured yet, but the tracks are known - show which one would be used.
            row.SourceTrack = row.Tracks.FirstOrDefault(t => t.Selected)?.Label ?? string.Empty;
        }

        if (row.SourceRangeLu.HasValue && row.ResultRangeLu.HasValue)
        {
            row.RangeImprovement = Math.Round(row.SourceRangeLu.Value - row.ResultRangeLu.Value, 1);
        }

        return row;
    }

    /// <summary>Gets the queue status.</summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    public ActionResult<QueueStatus> GetStatus() => Ok(_queue.GetStatus());

    /// <summary>Queues measurement.</summary>
    /// <param name="request">Which items.</param>
    /// <returns>How many were queued.</returns>
    [HttpPost("Analyze")]
    public ActionResult<int> QueueAnalyze([FromBody] QueueRequest request)
    {
        EnsureLoaded();
        return Ok(_queue.Enqueue(ResolveIds(request), JobKind.Analyze, false));
    }

    /// <summary>Queues track generation.</summary>
    /// <param name="request">Which items.</param>
    /// <returns>How many were queued.</returns>
    [HttpPost("Generate")]
    public ActionResult<int> QueueGenerate([FromBody] QueueRequest request)
    {
        EnsureLoaded();
        return Ok(_queue.Enqueue(ResolveIds(request), JobKind.Generate, request.Force));
    }

    /// <summary>Empties the queue.</summary>
    /// <returns>How many jobs were dropped.</returns>
    [HttpPost("Cancel")]
    public ActionResult<int> CancelQueue() => Ok(_queue.Clear());

    /// <summary>Deletes generated tracks for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="streamIndex">One source track's index, or omitted for all of them.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome message.</returns>
    [HttpDelete("Track/{itemId}")]
    public async Task<ActionResult<string>> DeleteTrack(
        [FromRoute] Guid itemId,
        [FromQuery] int? streamIndex,
        CancellationToken cancellationToken)
    {
        EnsureLoaded();
        var outcome = await _service.DeleteGeneratedAsync(itemId, streamIndex, cancellationToken).ConfigureAwait(false);
        return outcome.Success ? Ok(outcome.Message) : BadRequest(outcome.Message);
    }

    /// <summary>
    /// Sets which audio tracks of an item get normalized. This is what a film with several
    /// language tracks needs: the automatic rule picks one, this picks exactly what you want.
    /// </summary>
    /// <param name="request">The selection.</param>
    /// <returns>No content.</returns>
    [HttpPost("Tracks")]
    public ActionResult SetTrackSelection([FromBody] TrackSelectionRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!Guid.TryParse(request.ItemId, out var id))
        {
            return BadRequest("bad item id");
        }

        var config = plugin.Configuration;
        var existing = config.FindOverride(id);

        if (request.ResetToAutomatic)
        {
            if (existing is not null)
            {
                existing.HasExplicitTrackSelection = false;
                existing.SelectedStreamIndexes.Clear();

                // An override that now carries nothing but defaults is just clutter.
                if (!existing.Excluded)
                {
                    config.ItemOverrides.Remove(existing);
                }
            }
        }
        else
        {
            if (existing is null)
            {
                existing = new ItemProfileOverride
                {
                    ItemId = id.ToString("N"),
                    ItemName = _libraryManager.GetItemById(id)?.Name ?? string.Empty,
                    Profile = config.GlobalProfile.Clone()
                };
                config.ItemOverrides.Add(existing);
            }

            existing.HasExplicitTrackSelection = true;
            existing.SelectedStreamIndexes = request.StreamIndexes.Distinct().OrderBy(i => i).ToList();
        }

        plugin.UpdateConfiguration(config);

        // Reflect the new choice in the stored record straight away, so the report does not
        // need a re-analysis to show it.
        var record = _store.Get(id);
        if (record is not null)
        {
            SourceTrackSelector.ApplySelection(record.AudioTracks, config, id);
            _store.Put(record);
        }

        return NoContent();
    }

    /// <summary>Sets or clears a per-item override.</summary>
    /// <param name="request">The override.</param>
    /// <returns>No content.</returns>
    [HttpPost("Override")]
    public ActionResult SetOverride([FromBody] OverrideRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!Guid.TryParse(request.ItemId, out var id))
        {
            return BadRequest("bad item id");
        }

        var config = plugin.Configuration;
        var existing = config.FindOverride(id);
        if (existing is not null)
        {
            config.ItemOverrides.Remove(existing);
        }

        if (request.Excluded || request.Profile is not null)
        {
            config.ItemOverrides.Add(new ItemProfileOverride
            {
                ItemId = id.ToString("N"),
                ItemName = _libraryManager.GetItemById(id)?.Name ?? string.Empty,
                Excluded = request.Excluded,
                Profile = request.Profile ?? config.GlobalProfile.Clone()
            });
        }

        plugin.UpdateConfiguration(config);
        return NoContent();
    }

    /// <summary>Gets the effective settings for one item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The profile.</returns>
    [HttpGet("Override/{itemId}")]
    public ActionResult<NormalizationProfile> GetOverride([FromRoute] Guid itemId)
    {
        var config = Config;
        var over = config.FindOverride(itemId);
        return Ok(over?.Profile ?? config.GlobalProfile);
    }

    /// <summary>Lists libraries for the picker.</summary>
    /// <returns>Library id and name pairs.</returns>
    [HttpGet("Libraries")]
    public ActionResult<IEnumerable<object>> GetLibraries()
    {
        var folders = _libraryManager.GetVirtualFolders();
        return Ok(folders.Select(f => new { id = f.ItemId, name = f.Name, type = f.CollectionType?.ToString() }).ToList());
    }

    /// <summary>
    /// Runs a full dry self-test on one item: what was resolved, the exact ffmpeg commands,
    /// and what ffmpeg said. Writes nothing. This is the endpoint to reach for first when
    /// something does not work.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="run">Whether to actually execute the measurement pass. Defaults to true.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The findings.</returns>
    [HttpGet("SelfTest/{itemId}")]
    public async Task<ActionResult<SelfTestResult>> SelfTest(
        [FromRoute] Guid itemId,
        [FromQuery] bool run = true,
        CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        return Ok(await _service.SelfTestAsync(itemId, run, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Checks the environment and reports anything that would stop the plugin working.</summary>
    /// <returns>The diagnostics.</returns>
    [HttpGet("Diagnostics")]
    public async Task<ActionResult<DiagnosticsResult>> Diagnostics()
    {
        EnsureLoaded();
        var result = new DiagnosticsResult { FfmpegPath = _ffmpeg.EncoderPath };

        foreach (var filter in FilterChainBuilder.RequiredFilters
                     .Concat(new[] { "dynaudnorm", "speechnorm", "acompressor" }))
        {
            result.Filters[filter] = await _ffmpeg.SupportsFilterAsync(filter).ConfigureAwait(false);
        }

        var config = Config;
        if (config.GlobalProfile.Engine == NormalizationEngine.Dynaudnorm && !result.Filters.GetValueOrDefault("dynaudnorm"))
        {
            result.Warnings.Add("This ffmpeg has no dynaudnorm filter. Switch the engine to Compressor or update ffmpeg.");
        }

        if (config.GlobalProfile.Engine == NormalizationEngine.SpeechNorm && !result.Filters.GetValueOrDefault("speechnorm"))
        {
            result.Warnings.Add("This ffmpeg has no speechnorm filter. Switch the engine to Dynaudnorm or Compressor.");
        }

        if (!result.Filters.GetValueOrDefault("loudnorm") || !result.Filters.GetValueOrDefault("alimiter"))
        {
            result.Warnings.Add("loudnorm or alimiter is missing, so the plugin cannot hit a loudness target or protect the ceiling.");
        }

        try
        {
            var candidates = _service.GetCandidateItems();
            result.CandidateCount = candidates.Count;

            // Sample a handful of folders: an unwritable library is the single most common
            // reason this approach cannot work, and it is worth saying so before a long run.
            var unwritable = 0;
            string? firstReason = null;
            foreach (var item in candidates.Take(25))
            {
                var folder = Path.GetDirectoryName(item.Path);

                // Declared before the &&, not with 'out var' inside it: the call is
                // short-circuited when there is no folder, so the compiler cannot prove
                // the variable was assigned.
                string? reason = folder is null ? "the item has no folder path" : null;
                if (folder is not null && FolderAccess.IsWritable(folder, out reason))
                {
                    continue;
                }

                unwritable++;

                // One concrete path and the OS message beats a bare count: "not writable"
                // on its own gives nobody anything to go and fix.
                firstReason ??= (folder ?? item.Path) + " - " + reason;
            }

            if (unwritable > 0)
            {
                result.Warnings.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{unwritable} of the first 25 media folders are not writable. Jellyfin only finds external audio tracks next to the video file, so those items cannot be processed. First one: {firstReason}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio Normalizer: diagnostics failed");
            result.Warnings.Add("Could not enumerate the library: " + ex.Message);
        }

        return Ok(result);
    }

    private void EnsureLoaded()
    {
        if (Plugin.Instance is not null)
        {
            _store.EnsureLoaded(Plugin.Instance.DataFolderPath);
        }
    }

    private IEnumerable<Guid> ResolveIds(QueueRequest request)
    {
        if (request.ItemIds.Count > 0)
        {
            foreach (var raw in request.ItemIds)
            {
                if (Guid.TryParse(raw, out var id))
                {
                    yield return id;
                }
            }

            yield break;
        }

        foreach (var item in _service.GetCandidateItems())
        {
            yield return item.Id;
        }
    }
}
