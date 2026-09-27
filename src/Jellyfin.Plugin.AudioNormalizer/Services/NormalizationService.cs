using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioNormalizer.Configuration;
using Jellyfin.Plugin.AudioNormalizer.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>Outcome of one unit of work.</summary>
public sealed class WorkOutcome
{
    /// <summary>Gets or sets a value indicating whether the work completed.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a value indicating whether the item was deliberately left alone.</summary>
    public bool Skipped { get; set; }

    /// <summary>Gets or sets an explanation for the report.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Everything the self-test found about one item, in one object. This is the first thing to
/// look at when a film does not behave: it reports what the plugin resolved, the exact ffmpeg
/// command it would run, and what ffmpeg said back.
/// </summary>
public sealed class SelfTestResult
{
    /// <summary>Gets or sets the item name.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the source file path.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets why the item was refused, when it was.</summary>
    public string? Refusal { get; set; }

    /// <summary>Gets or sets every audio track, as readable lines, with the selection marked.</summary>
    public List<string> AudioStreams { get; set; } = new List<string>();

    /// <summary>Gets or sets the tracks that would be normalized.</summary>
    public List<string> SelectedStreams { get; set; } = new List<string>();

    /// <summary>Gets or sets where each generated track would be written.</summary>
    public List<string> OutputPaths { get; set; } = new List<string>();

    /// <summary>Gets or sets whether the media folder accepts writes.</summary>
    public bool FolderWritable { get; set; }

    /// <summary>Gets or sets free space on the target volume, in gigabytes.</summary>
    public double FreeSpaceGb { get; set; }

    /// <summary>Gets or sets the ffmpeg binary in use.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the analysis command, shell-quoted and ready to paste.</summary>
    public string AnalysisCommand { get; set; } = string.Empty;

    /// <summary>Gets or sets the encode command for the first selected track.</summary>
    public string EncodeCommand { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the analysis pass actually ran and succeeded.</summary>
    public bool AnalysisRan { get; set; }

    /// <summary>Gets or sets the measured numbers per track, as readable lines.</summary>
    public List<string> Measurements { get; set; } = new List<string>();

    /// <summary>Gets or sets the dynaudnorm max gain resolved for the first selected track.</summary>
    public double ResolvedMaxGain { get; set; }

    /// <summary>Gets or sets the tail of ffmpeg's own output.</summary>
    public string FfmpegOutput { get; set; } = string.Empty;

    /// <summary>Gets or sets anything that looks wrong.</summary>
    public List<string> Problems { get; set; } = new List<string>();
}

/// <summary>
/// Does the actual work: measure an item's audio tracks, build a normalized companion for
/// each track the user selected, verify it and tell the library about it. Every guard that
/// protects the user's files lives here.
/// </summary>
public sealed class NormalizationService
{
    private readonly ILogger<NormalizationService> _logger;
    private readonly FfmpegRunner _ffmpeg;
    private readonly StateStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="NormalizationService"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="ffmpeg">ffmpeg wrapper.</param>
    /// <param name="store">State store.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="fileSystem">File system abstraction, needed for the refresh's directory service.</param>
    public NormalizationService(
        ILogger<NormalizationService> logger,
        FfmpegRunner ffmpeg,
        StateStore store,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _ffmpeg = ffmpeg;
        _store = store;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _fileSystem = fileSystem;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Measures every audio track of an item, in one ffmpeg call, and records the numbers so
    /// the report can show them per track before the user chooses which to normalize.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WorkOutcome> AnalyzeAsync(Guid itemId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var ctx = Resolve(itemId, out var refusal);
        if (ctx is null)
        {
            return Skip(itemId, refusal);
        }

        var record = _store.GetOrCreate(itemId);
        StampItem(record, ctx);
        MergeTracks(record, ctx.Tracks);

        var config = Config;

        // Only the tracks that will actually be normalized. A remux with six audio tracks
        // otherwise costs six full decodes to produce five numbers nobody looks at. Falls back
        // to everything when the selection is empty, so the item is not left with no data.
        var measurable = config.MeasureSelectedTracksOnly
            ? record.AudioTracks.Where(t => t.Selected).ToList()
            : record.AudioTracks.ToList();
        if (measurable.Count == 0)
        {
            measurable = record.AudioTracks.ToList();
        }

        var indexes = measurable.Select(t => t.StreamIndex).ToList();
        if (indexes.Count == 0)
        {
            return new WorkOutcome { Message = "no audio tracks to measure" };
        }

        // Quick scan listens to a share of the film spread over several windows instead of
        // decoding all of it. Analysis is not cheaper than encoding - both decode every sample
        // of the track - so hearing less is the only lever. See PluginConfiguration.QuickScan.
        //
        // windowSeconds is declared up front rather than with 'out var' inside the ternary:
        // only one branch assigns it, so the compiler could not prove it was ever set.
        double windowSeconds = 0;
        var offsets = config.QuickScan
            ? FilterChainBuilder.SampleOffsets(
                ctx.DurationSeconds,
                config.QuickScanWindows,
                config.QuickScanCoveragePercent,
                out windowSeconds)
            : Array.Empty<double>();
        var sampled = offsets.Count > 0;

        string graph;
        List<string> labels;
        List<int> ordinals;
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info" };

        if (sampled)
        {
            graph = FilterChainBuilder.BuildSampledAnalysisGraph(indexes, offsets.Count, out labels, out ordinals);

            // One input per window. Seeking before -i uses the container index, so only these
            // regions are read off disk rather than the whole file.
            foreach (var offset in offsets)
            {
                args.Add("-ss");
                args.Add(offset.ToString("F3", CultureInfo.InvariantCulture));
                args.Add("-t");
                args.Add(windowSeconds.ToString("F3", CultureInfo.InvariantCulture));
                args.Add("-i");
                args.Add(ctx.Item.Path);
            }
        }
        else
        {
            graph = FilterChainBuilder.BuildMultiTrackAnalysisGraph(indexes, out labels);
            ordinals = Enumerable.Range(0, indexes.Count).ToList();
            args.Add("-i");
            args.Add(ctx.Item.Path);
        }

        args.Add("-filter_complex");
        args.Add(graph);

        foreach (var label in labels)
        {
            args.Add("-map");
            args.Add("[" + label + "]");
        }

        args.Add("-f");
        args.Add("null");
        args.Add("-");

        var result = await _ffmpeg.RunAsync(
            args,

            // Progress is driven by the output timestamp, which for a sampled run only spans
            // the windows, not the film.
            sampled ? offsets.Count * windowSeconds : ctx.DurationSeconds,
            progress,
            config.ProcessNiceness,
            TimeoutFor(ctx.DurationSeconds),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            record.LastError = result.TimedOut ? "analysis timed out" : FfmpegOutputParser.Summarize(result.StdErr);
            _store.Put(record);
            _logger.LogWarning("Audio Normalizer: analysis failed for {Name}: {Error}", ctx.Item.Name, record.LastError);
            return new WorkOutcome { Message = record.LastError };
        }

        // Keyed by filter index, because ffmpeg prints the summaries out of order.
        var measured = FfmpegOutputParser.ParseEbur128Multi(result.StdErr);
        if (measured.Count == 0)
        {
            record.LastError = "could not read any loudness summary from ffmpeg";
            _store.Put(record);
            return new WorkOutcome { Message = record.LastError };
        }

        var applied = 0;
        for (var i = 0; i < indexes.Count; i++)
        {
            if (!measured.TryGetValue(ordinals[i], out var m))
            {
                continue;
            }

            var track = record.FindTrack(indexes[i]);
            if (track is null)
            {
                continue;
            }

            m.IsEstimate = sampled;
            track.Source = m;
            track.AnalyzedUtc = DateTime.UtcNow;
            track.LastError = null;
            if (track.State is TrackState.Unknown or TrackState.Failed)
            {
                track.State = TrackState.Analyzed;
            }

            applied++;
        }

        record.AnalyzedUtc = DateTime.UtcNow;
        record.LastError = null;
        _store.Put(record);

        _logger.LogInformation(
            "Audio Normalizer: {Mode} measured {Count} track(s) of {Name}",
            sampled ? "quick scan" : "full scan",
            applied,
            ctx.Item.Name);

        return new WorkOutcome { Success = true, Message = "measured " + applied.ToString(CultureInfo.InvariantCulture) + " track(s)" };
    }

    /// <summary>Builds a normalized companion for every selected track of an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="force">Rebuild even when an up to date track already exists.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WorkOutcome> GenerateAsync(Guid itemId, bool force, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var ctx = Resolve(itemId, out var refusal);
        if (ctx is null)
        {
            return Skip(itemId, refusal);
        }

        var record = _store.GetOrCreate(itemId);
        StampItem(record, ctx);
        MergeTracks(record, ctx.Tracks);

        var selected = record.SelectedTracks().ToList();
        if (selected.Count == 0)
        {
            record.SkipReason = "no audio track is selected for this item";
            _store.Put(record);
            return new WorkOutcome { Success = true, Skipped = true, Message = record.SkipReason };
        }

        // Measure first: the encode needs loudnorm's numbers and auto strength needs the range.
        if (selected.Any(t => t.Source is null))
        {
            var analyzed = await AnalyzeAsync(itemId, null, cancellationToken).ConfigureAwait(false);
            if (!analyzed.Success)
            {
                return analyzed;
            }

            record = _store.GetOrCreate(itemId);
            selected = record.SelectedTracks().ToList();
        }

        var folderCheck = CheckTargetFolder(ctx, selected);
        if (folderCheck is not null)
        {
            record.LastError = folderCheck;
            _store.Put(record);
            return new WorkOutcome { Message = folderCheck };
        }

        // One name set per item, so two tracks of the same film can never land on one file.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perTrackProfile = ctx.Profile.Clone();
        if (selected.Count > 1 && perTrackProfile.MakeDefaultTrack)
        {
            // Flagging several tracks default is meaningless and confuses players.
            perTrackProfile.MakeDefaultTrack = false;
            _logger.LogInformation(
                "Audio Normalizer: {Name} has {Count} tracks selected, so none is marked default",
                ctx.Item.Name,
                selected.Count);
        }

        var made = 0;
        var failed = 0;
        var skipped = 0;
        var anyWritten = false;

        for (var i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var track = selected[i];
            IProgress<double>? trackProgress = progress is null
                ? null
                : new Progress<double>(p => progress.Report(((i * 100.0) + p) / selected.Count));

            var outcome = await GenerateTrackAsync(ctx, record, track, perTrackProfile, taken, force, trackProgress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                failed++;
            }
            else if (outcome.Skipped)
            {
                skipped++;
            }
            else
            {
                made++;
                anyWritten = true;
            }
        }

        _store.Put(record);

        if (anyWritten && Config.RefreshItemAfterGenerate)
        {
            await RefreshItemAsync(ctx.Item, cancellationToken).ConfigureAwait(false);
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"{made} built, {skipped} skipped, {failed} failed");

        return new WorkOutcome { Success = failed == 0, Skipped = made == 0 && failed == 0, Message = message };
    }

    /// <summary>Removes generated tracks and forgets about them.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="streamIndex">One track's source index, or null for all of them.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<WorkOutcome> DeleteGeneratedAsync(Guid itemId, int? streamIndex, CancellationToken cancellationToken)
    {
        var record = _store.Get(itemId);
        if (record is null)
        {
            return new WorkOutcome { Success = true, Skipped = true, Message = "nothing to delete" };
        }

        var targets = streamIndex.HasValue
            ? record.AudioTracks.Where(t => t.StreamIndex == streamIndex.Value).ToList()
            : record.AudioTracks.ToList();

        var removed = 0;
        foreach (var track in targets)
        {
            if (string.IsNullOrEmpty(track.OutputPath))
            {
                continue;
            }

            // Only ever delete a path this plugin recorded as its own output.
            if (File.Exists(track.OutputPath))
            {
                try
                {
                    File.Delete(track.OutputPath);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audio Normalizer: could not delete {Path}", track.OutputPath);
                    return new WorkOutcome { Message = ex.Message };
                }
            }

            track.OutputPath = null;
            track.OutputSize = 0;
            track.Result = null;
            track.ProfileSignature = null;
            track.GeneratedUtc = null;
            track.State = track.Source is null ? TrackState.Unknown : TrackState.Analyzed;
        }

        _store.Put(record);

        if (removed > 0)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is not null)
            {
                await RefreshItemAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }

        return new WorkOutcome
        {
            Success = true,
            Skipped = removed == 0,
            Message = removed == 0 ? "nothing to delete" : "deleted " + removed.ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Runs everything up to but not including the write, and reports what happened at each
    /// step. Nothing is created or modified.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="runAnalysis">Whether to actually invoke ffmpeg for the measurement pass.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The findings.</returns>
    public async Task<SelfTestResult> SelfTestAsync(Guid itemId, bool runAnalysis, CancellationToken cancellationToken)
    {
        var result = new SelfTestResult { FfmpegPath = _ffmpeg.EncoderPath };

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            result.Refusal = "item not found in the library";
            return result;
        }

        result.ItemName = item.Name ?? string.Empty;
        result.SourcePath = item.Path ?? string.Empty;

        var ctx = Resolve(itemId, out var refusal);
        if (ctx is null)
        {
            result.Refusal = refusal;
            return result;
        }

        foreach (var t in ctx.Tracks)
        {
            result.AudioStreams.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{(t.Selected ? "[x]" : "[ ]")} index {t.StreamIndex}: {t.Label}{(t.IsDefault ? " [default]" : string.Empty)}{(t.IsCommentary ? " [commentary]" : string.Empty)}"));
        }

        var selected = ctx.Tracks.Where(t => t.Selected).ToList();
        if (selected.Count == 0)
        {
            result.Problems.Add("No audio track is selected, so nothing would be generated for this item.");
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in selected)
        {
            result.SelectedStreams.Add(string.Create(CultureInfo.InvariantCulture, $"index {t.StreamIndex}: {t.Label}"));
            result.OutputPaths.Add(OutputNaming.BuildOutputPath(ctx.Item.Path, ctx.Profile, t, taken));
        }

        var folder = Path.GetDirectoryName(ctx.Item.Path);

        // Declared up front, not with 'out var' inside the &&: the right-hand side is
        // short-circuited, so the compiler cannot prove the variable was ever assigned.
        string? folderReason = folder is null ? "the item has no folder path" : null;
        result.FolderWritable = folder is not null && FolderAccess.IsWritable(folder, out folderReason);
        if (!result.FolderWritable)
        {
            result.Problems.Add(
                "The media folder is not writable (" + (folder ?? "?") + "): " + folderReason
                + ". Jellyfin only discovers external audio next to the video file, so this item cannot be processed at all.");
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder ?? ".")) ?? ".");
            result.FreeSpaceGb = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0, 1);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: free space unknown during self test");
        }

        var record = _store.Get(itemId);
        var first = selected.FirstOrDefault();
        var range = (first is null ? null : record?.FindTrack(first.StreamIndex)?.Source?.LoudnessRangeLu) ?? 15.0;
        result.ResolvedMaxGain = FilterChainBuilder.ResolveMaxGain(ctx.Profile, range);

        var indexes = ctx.Tracks.Select(t => t.StreamIndex).ToList();
        var graph = FilterChainBuilder.BuildMultiTrackAnalysisGraph(indexes, out var labels);
        var analysisArgs = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
            "-i", ctx.Item.Path,
            "-filter_complex", graph
        };
        foreach (var label in labels)
        {
            analysisArgs.Add("-map");
            analysisArgs.Add("[" + label + "]");
        }

        analysisArgs.Add("-f");
        analysisArgs.Add("null");
        analysisArgs.Add("-");
        result.AnalysisCommand = Quote(_ffmpeg.EncoderPath, analysisArgs);

        if (first is not null)
        {
            var previewTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outPath = OutputNaming.BuildOutputPath(ctx.Item.Path, ctx.Profile, first, previewTaken);
            result.EncodeCommand = Quote(
                _ffmpeg.EncoderPath,
                BuildEncodeArgs(ctx, first, ctx.Profile, null, range, OutputNaming.TempPathFor(outPath)));
        }

        foreach (var filter in FilterChainBuilder.RequiredFilters)
        {
            if (!await _ffmpeg.SupportsFilterAsync(filter).ConfigureAwait(false))
            {
                result.Problems.Add("This ffmpeg build has no '" + filter + "' filter.");
            }
        }

        if (!runAnalysis)
        {
            return result;
        }

        var run = await _ffmpeg.RunAsync(
            analysisArgs,
            ctx.DurationSeconds,
            null,
            Config.ProcessNiceness,
            TimeoutFor(ctx.DurationSeconds),
            cancellationToken).ConfigureAwait(false);

        result.AnalysisRan = run.Success;
        result.FfmpegOutput = Tail(run.StdErr, 40);

        if (!run.Success)
        {
            result.Problems.Add(run.TimedOut ? "The analysis pass timed out." : "ffmpeg exited with " + run.ExitCode);
            return result;
        }

        var measured = FfmpegOutputParser.ParseEbur128Multi(run.StdErr);
        for (var i = 0; i < indexes.Count; i++)
        {
            if (!measured.TryGetValue(i, out var m))
            {
                result.Problems.Add(string.Create(CultureInfo.InvariantCulture, $"No measurement came back for stream {indexes[i]}."));
                continue;
            }

            result.Measurements.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"index {indexes[i]}: {m.IntegratedLufs:F1} LUFS, range {m.LoudnessRangeLu:F1} LU (quiet {m.RangeLowLufs:F1} / loud {m.RangeHighLufs:F1}), peak {m.TruePeakDb:F1} dBTP"));

            var track = ctx.Tracks.FirstOrDefault(t => t.StreamIndex == indexes[i]);
            if (track is { Selected: true } && m.LoudnessRangeLu < ctx.Profile.SkipIfDynamicRangeBelowLu)
            {
                result.Problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Stream {indexes[i]} would be skipped: its range of {m.LoudnessRangeLu:F1} LU is below the {ctx.Profile.SkipIfDynamicRangeBelowLu:F1} LU threshold."));
            }
        }

        return result;
    }

    /// <summary>Re-probes an item so newly written tracks show up straight away.</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RefreshItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            // A fresh DirectoryService matters: the server's probe compares the folder listing
            // against the item's recorded AudioFiles, and a cached listing would not contain
            // the files we just wrote, so nothing would change.
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                // Default is enough. The server's probe provider explicitly reports "changed"
                // when the set of external audio files differs, so this stays cheap and does
                // not drag in remote metadata lookups.
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.None,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
                EnableRemoteContentProbe = false,
                IsAutomated = true
            };

            await item.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed refresh only delays the tracks appearing until the next library scan.
            _logger.LogWarning(ex, "Audio Normalizer: refresh failed for {Name}", item.Name);
        }
    }

    /// <summary>
    /// The audio tracks of an item as Jellyfin already knows them, with the current selection
    /// rule applied. No ffmpeg, no filesystem access and no exclusion check, so the report can
    /// list a film that has never been measured and still let its tracks be ticked.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The tracks, or an empty list when the streams cannot be read.</returns>
    public IReadOnlyList<AudioTrackInfo> PeekTracks(Guid itemId)
    {
        try
        {
            return SourceTrackSelector.BuildTrackList(_mediaSourceManager.GetMediaStreams(itemId), Config, itemId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: could not read audio streams for {ItemId}", itemId);
            return Array.Empty<AudioTrackInfo>();
        }
    }

    /// <summary>
    /// Whether an item has no usable measurement yet. One predicate shared by the scheduled task
    /// and the API, so "measure the library" means the same thing however it is started.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True when nothing has been measured for it.</returns>
    public bool NeedsMeasuring(Guid itemId)
    {
        var record = _store.Get(itemId);
        return record is null
            || record.AudioTracks.Count == 0
            || record.AudioTracks.TrueForAll(t => t.Source is null);
    }

    /// <summary>Lists items the scheduled tasks should consider.</summary>
    /// <returns>Candidate items.</returns>
    public IReadOnlyList<BaseItem> GetCandidateItems()
    {
        var config = Config;
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode },
            MediaTypes = new[] { Jellyfin.Data.Enums.MediaType.Video },
            IsVirtualItem = false,
            EnableTotalRecordCount = false
        };

        if (config.EnabledLibraryIds.Count > 0)
        {
            var ids = config.EnabledLibraryIds
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToArray();
            if (ids.Length > 0)
            {
                query.AncestorIds = ids;
            }
        }

        var minTicks = TimeSpan.FromMinutes(Math.Max(0, config.MinimumDurationMinutes)).Ticks;

        return _libraryManager.GetItemList(query)
            .Where(i => i.IsFileProtocol && !string.IsNullOrEmpty(i.Path))
            .Where(i => (i.RunTimeTicks ?? 0) >= minTicks)
            .ToList();
    }

    /// <summary>Deletes generated tracks whose source item is gone.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many files were removed.</returns>
    public int CleanupOrphans(CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var record in _store.All())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceGone = string.IsNullOrEmpty(record.SourcePath) || !File.Exists(record.SourcePath);
            if (!sourceGone)
            {
                continue;
            }

            var failedAny = false;
            foreach (var track in record.AudioTracks)
            {
                if (string.IsNullOrEmpty(track.OutputPath) || !File.Exists(track.OutputPath))
                {
                    continue;
                }

                if (!Config.DeleteOrphanedTracks)
                {
                    continue;
                }

                try
                {
                    File.Delete(track.OutputPath);
                    removed++;
                    _logger.LogInformation("Audio Normalizer: removed orphaned track {Path}", track.OutputPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Audio Normalizer: could not remove orphan {Path}", track.OutputPath);
                    failedAny = true;
                }
            }

            if (!failedAny && Guid.TryParse(record.ItemId, out var id))
            {
                _store.Remove(id);
            }
        }

        _store.Flush();
        return removed;
    }

    /// <summary>Renders an argument list the way a shell would need it, for copy and paste.</summary>
    /// <param name="exe">The executable.</param>
    /// <param name="args">Its arguments.</param>
    /// <returns>A pasteable command line.</returns>
    public static string Quote(string exe, IEnumerable<string> args)
    {
        static string One(string a) =>
            a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=')
                ? a
                : "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        return One(exe) + " " + string.Join(' ', args.Select(One));
    }

    private async Task<WorkOutcome> GenerateTrackAsync(
        ItemContext ctx,
        TrackRecord record,
        AudioTrackInfo track,
        NormalizationProfile profile,
        ISet<string> takenNames,
        bool force,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var outputPath = OutputNaming.BuildOutputPath(ctx.Item.Path, profile, track, takenNames);
        var signature = profile.SignatureKey + "|src:" + track.StreamIndex.ToString(CultureInfo.InvariantCulture);

        if (!force && IsUpToDate(record, track, outputPath, signature))
        {
            track.State = TrackState.Done;
            return new WorkOutcome { Success = true, Skipped = true, Message = "up to date" };
        }

        var sourceRange = track.Source?.LoudnessRangeLu ?? 15.0;

        // A quick scan samples the film, so its range is a floor rather than the truth: windows
        // can miss the loudest scene entirely. Letting an estimate skip a film would quietly
        // drop exactly the titles this is meant to find, so only a full measurement may skip.
        if (sourceRange < profile.SkipIfDynamicRangeBelowLu && !(track.Source?.IsEstimate ?? false))
        {
            track.State = TrackState.Skipped;
            track.SkipReason = string.Create(
                CultureInfo.InvariantCulture,
                $"range is already {sourceRange:F1} LU, below the {profile.SkipIfDynamicRangeBelowLu:F1} LU threshold");
            return new WorkOutcome { Success = true, Skipped = true, Message = track.SkipReason };
        }

        if (Config.DryRun)
        {
            track.SkipReason = "dry run: would write " + outputPath;
            return new WorkOutcome { Success = true, Skipped = true, Message = track.SkipReason };
        }

        var loudnorm = await MeasureChainAsync(ctx, track, profile, sourceRange, cancellationToken).ConfigureAwait(false);

        var tempPath = OutputNaming.TempPathFor(outputPath);
        TryDelete(tempPath);

        var args = BuildEncodeArgs(ctx, track, profile, loudnorm, sourceRange, tempPath);

        track.State = TrackState.Running;
        _store.Put(record);

        FfmpegResult result;
        try
        {
            result = await _ffmpeg.RunAsync(
                args,
                ctx.DurationSeconds,
                progress,
                Config.ProcessNiceness,
                TimeoutFor(ctx.DurationSeconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Never leave a partial file where a future run might mistake it for finished work.
            TryDelete(tempPath);
            track.State = track.Source is null ? TrackState.Unknown : TrackState.Analyzed;
            _store.Put(record);
            throw;
        }

        if (!result.Success)
        {
            TryDelete(tempPath);
            track.State = TrackState.Failed;
            track.LastError = result.TimedOut ? "encode timed out" : FfmpegOutputParser.Summarize(result.StdErr);
            _logger.LogWarning(
                "Audio Normalizer: encode failed for {Name} stream {Index}: {Error}",
                ctx.Item.Name,
                track.StreamIndex,
                track.LastError);
            return new WorkOutcome { Message = track.LastError };
        }

        var info = new FileInfo(tempPath);
        if (!info.Exists || info.Length < 1024)
        {
            TryDelete(tempPath);
            track.State = TrackState.Failed;
            track.LastError = "ffmpeg reported success but produced no usable file";
            return new WorkOutcome { Message = track.LastError };
        }

        try
        {
            // Same-directory rename, so the finished file appears atomically. Jellyfin can never
            // see a half-written track.
            File.Move(tempPath, outputPath, true);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            track.State = TrackState.Failed;
            track.LastError = "could not move the finished file into place: " + ex.Message;
            _logger.LogError(ex, "Audio Normalizer: move failed for {Path}", outputPath);
            return new WorkOutcome { Message = track.LastError };
        }

        track.OutputPath = outputPath;
        track.OutputSize = new FileInfo(outputPath).Length;
        track.ProfileSignature = signature;
        track.GeneratedUtc = DateTime.UtcNow;
        track.State = TrackState.Done;
        track.LastError = null;
        track.SkipReason = null;
        _store.Put(record);

        track.Result = await VerifyAsync(outputPath, cancellationToken).ConfigureAwait(false);
        _store.Put(record);

        _logger.LogInformation(
            "Audio Normalizer: wrote {Path} ({Size} MB)",
            outputPath,
            (track.OutputSize / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture));

        return new WorkOutcome { Success = true, Message = "generated" };
    }

    private async Task<LoudnormMeasurement?> MeasureChainAsync(
        ItemContext ctx,
        AudioTrackInfo track,
        NormalizationProfile profile,
        double sourceRange,
        CancellationToken cancellationToken)
    {
        var pre = FilterChainBuilder.BuildPreChain(profile, track.Channels, track.ChannelLayout, sourceRange);
        var chain = new List<string>(pre)
        {
            FilterChainBuilder.BuildLoudnorm(profile, null) + ":print_format=json"
        };

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
            "-i", ctx.Item.Path,
            "-map", "0:" + track.StreamIndex.ToString(CultureInfo.InvariantCulture),
            "-vn", "-sn", "-dn",
            "-af", string.Join(',', chain),
            "-f", "null", "-"
        };

        var result = await _ffmpeg.RunAsync(
            args,
            ctx.DurationSeconds,
            null,
            Config.ProcessNiceness,
            TimeoutFor(ctx.DurationSeconds),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            // Not fatal: without measurements loudnorm falls back to its single pass mode,
            // which still hits the target, just less precisely.
            _logger.LogWarning(
                "Audio Normalizer: chain measurement failed for {Name} stream {Index}, falling back to single pass",
                ctx.Item.Name,
                track.StreamIndex);
            return null;
        }

        var measured = FfmpegOutputParser.ParseLoudnorm(result.StdErr);
        return measured.IsValid ? measured : null;
    }

    private async Task<LoudnessMeasurement?> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
            "-i", path,
            "-map", "0:a:0",
            "-af", "ebur128=peak=true:framelog=quiet",
            "-f", "null", "-"
        };

        try
        {
            var result = await _ffmpeg.RunAsync(
                args,
                0,
                null,
                Config.ProcessNiceness,
                TimeSpan.FromMinutes(Config.JobTimeoutFloorMinutes),
                cancellationToken).ConfigureAwait(false);

            return result.Success ? FfmpegOutputParser.ParseEbur128(result.StdErr) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: verification pass failed for {Path}", path);
            return null;
        }
    }

    private static List<string> BuildEncodeArgs(
        ItemContext ctx,
        AudioTrackInfo track,
        NormalizationProfile profile,
        LoudnormMeasurement? loudnorm,
        double sourceRange,
        string tempPath)
    {
        var chain = new List<string>();
        chain.AddRange(FilterChainBuilder.BuildPreChain(profile, track.Channels, track.ChannelLayout, sourceRange));
        chain.Add(FilterChainBuilder.BuildLoudnorm(profile, loudnorm));
        chain.AddRange(FilterChainBuilder.BuildPostChain(profile, sourceRange));

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-y",
            "-i", ctx.Item.Path,
            "-map", "0:" + track.StreamIndex.ToString(CultureInfo.InvariantCulture),

            // Audio only. Without these a stray cover image or chapter stream ends up in
            // the file and it stops being a single-stream external track, which is what
            // the server expects.
            "-vn", "-sn", "-dn", "-map_chapters", "-1",

            "-af", string.Join(',', chain),

            // Always explicit. loudnorm resamples to 192 kHz when left to itself.
            "-ar", profile.SampleRate.ToString(CultureInfo.InvariantCulture)
        };

        var channels = profile.Downmix == DownmixMode.KeepLayout ? Math.Max(1, track.Channels) : 2;
        args.Add("-ac");
        args.Add(channels.ToString(CultureInfo.InvariantCulture));

        args.Add("-c:a");
        args.Add(CodecName(profile.Codec));

        if (profile.Codec != OutputCodec.Flac)
        {
            args.Add("-b:a");
            args.Add(profile.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
        }

        if (!string.IsNullOrWhiteSpace(track.Language))
        {
            args.Add("-metadata:s:a:0");
            args.Add("language=" + track.Language);
        }

        args.Add("-metadata:s:a:0");
        args.Add("title=" + OutputNaming.BuildTitle(profile, track));

        // The file name decides the default flag; keep the in-file disposition neutral so the
        // two cannot disagree.
        args.Add("-disposition:a:0");
        args.Add("0");

        // Explicit muxer: the temp file's extension is deliberately one the library does not
        // scan, which means ffmpeg cannot infer the format from it.
        args.Add("-f");
        args.Add(OutputNaming.MuxerFor(profile.Container, profile.Codec));

        args.Add(tempPath);
        return args;
    }

    private static string CodecName(OutputCodec codec) => codec switch
    {
        OutputCodec.Aac => "aac",
        OutputCodec.Eac3 => "eac3",
        OutputCodec.Ac3 => "ac3",
        OutputCodec.Flac => "flac",
        OutputCodec.Opus => "libopus",
        _ => "aac"
    };

    private static TimeSpan TimeoutFor(double durationSeconds)
    {
        var config = Config;
        var scaled = TimeSpan.FromSeconds(Math.Max(0, durationSeconds) * Math.Max(1.0, config.JobTimeoutRealtimeFactor));
        var floor = TimeSpan.FromMinutes(Math.Max(1, config.JobTimeoutFloorMinutes));
        return scaled > floor ? scaled : floor;
    }

    private string? CheckTargetFolder(ItemContext ctx, IReadOnlyCollection<AudioTrackInfo> selected)
    {
        var folder = Path.GetDirectoryName(ctx.Item.Path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return "the media folder does not exist";
        }

        // Jellyfin only discovers external audio in the media's own folder, so a read-only
        // library cannot be served by this approach at all. Say so plainly instead of
        // failing later with a permissions error.
        if (!FolderAccess.IsWritable(folder, out var reason))
        {
            return "the media folder is not writable (" + folder + "): " + reason
                + ". Jellyfin only finds external audio next to the video, so this item cannot be processed.";
        }

        var estimate = selected.Sum(_ => EstimateOutputBytes(ctx));
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder);
            var margin = (long)(Config.MinimumFreeSpaceGb * 1024 * 1024 * 1024);
            if (drive.AvailableFreeSpace - estimate < margin)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"not enough free space: need about {estimate / 1024 / 1024} MB plus a {Config.MinimumFreeSpaceGb:F1} GB margin");
            }
        }
        catch (Exception ex)
        {
            // Network shares often refuse to report free space. Carry on rather than blocking.
            _logger.LogDebug(ex, "Audio Normalizer: free space unknown for {Folder}", folder);
        }

        return null;
    }

    private static long EstimateOutputBytes(ItemContext ctx)
    {
        var kbps = ctx.Profile.Codec == OutputCodec.Flac ? 900 : ctx.Profile.BitrateKbps;
        return (long)(ctx.DurationSeconds * kbps * 1000 / 8 * 1.1);
    }

    private static bool IsUpToDate(TrackRecord record, AudioTrackInfo track, string outputPath, string signature)
    {
        if (track.OutputPath is null || !string.Equals(track.OutputPath, outputPath, StringComparison.Ordinal))
        {
            return false;
        }

        if (!File.Exists(outputPath))
        {
            return false;
        }

        if (!string.Equals(track.ProfileSignature, signature, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Config.RebuildWhenSourceChanges)
        {
            return true;
        }

        try
        {
            var info = new FileInfo(record.SourcePath);
            if (!info.Exists)
            {
                return false;
            }

            // A re-encoded or replaced source must not keep an old companion track around.
            if (info.Length != record.SourceSize)
            {
                return false;
            }

            return Math.Abs((info.LastWriteTimeUtc - record.SourceModifiedUtc).TotalSeconds) <= 2;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private void StampItem(TrackRecord record, ItemContext ctx)
    {
        record.ItemId = ctx.Item.Id.ToString("N");
        record.ItemName = ctx.Item.Name ?? string.Empty;
        record.ItemKind = ctx.Item.GetType().Name;
        record.SourcePath = ctx.Item.Path;
        record.DurationSeconds = ctx.DurationSeconds;

        try
        {
            var info = new FileInfo(ctx.Item.Path);
            if (info.Exists)
            {
                record.SourceSize = info.Length;
                record.SourceModifiedUtc = info.LastWriteTimeUtc;
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: could not stat {Path}", ctx.Item.Path);
        }

        _store.Put(record);
    }

    /// <summary>
    /// Folds the freshly probed track list into the stored one, keeping measurements and
    /// output state for tracks that are still there, and dropping ones that are gone.
    /// </summary>
    private static void MergeTracks(TrackRecord record, List<AudioTrackInfo> fresh)
    {
        var merged = new List<AudioTrackInfo>(fresh.Count);

        foreach (var f in fresh)
        {
            var existing = record.FindTrack(f.StreamIndex);
            if (existing is null)
            {
                merged.Add(f);
                continue;
            }

            // Descriptive fields come from the probe; everything earned stays.
            existing.Label = f.Label;
            existing.Codec = f.Codec;
            existing.Channels = f.Channels;
            existing.ChannelLayout = f.ChannelLayout;
            existing.Language = f.Language;
            existing.Title = f.Title;
            existing.IsDefault = f.IsDefault;
            existing.IsCommentary = f.IsCommentary;
            existing.Selected = f.Selected;
            existing.SelectionIsExplicit = f.SelectionIsExplicit;
            merged.Add(existing);
        }

        record.AudioTracks = merged;
    }

    private WorkOutcome Skip(Guid itemId, string reason)
    {
        var record = _store.GetOrCreate(itemId);
        record.SkipReason = reason;
        _store.Put(record);
        return new WorkOutcome { Success = true, Skipped = true, Message = reason };
    }

    private ItemContext? Resolve(Guid itemId, out string refusal)
    {
        refusal = string.Empty;

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            refusal = "item not found";
            return null;
        }

        if (!item.IsFileProtocol || string.IsNullOrEmpty(item.Path))
        {
            refusal = "not a local file";
            return null;
        }

        if (!File.Exists(item.Path))
        {
            refusal = "source file missing";
            return null;
        }

        var config = Config;
        var profile = config.GetEffectiveProfile(itemId);
        if (profile is null)
        {
            refusal = "excluded in settings";
            return null;
        }

        IReadOnlyList<MediaStream> streams;
        try
        {
            streams = _mediaSourceManager.GetMediaStreams(itemId);
        }
        catch (Exception ex)
        {
            refusal = "could not read media streams: " + ex.Message;
            return null;
        }

        var tracks = SourceTrackSelector.BuildTrackList(streams, config, itemId);
        if (tracks.Count == 0)
        {
            refusal = "no internal audio tracks";
            return null;
        }

        var duration = (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
        if (duration <= 0)
        {
            refusal = "unknown duration";
            return null;
        }

        return new ItemContext(item, tracks, profile, duration);
    }

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var all = text.Split('\n');
        return all.Length <= lines ? text : string.Join('\n', all.Skip(all.Length - lines));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Nothing useful to do; the next run overwrites it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private sealed record ItemContext(BaseItem Item, List<AudioTrackInfo> Tracks, NormalizationProfile Profile, double DurationSeconds);
}
