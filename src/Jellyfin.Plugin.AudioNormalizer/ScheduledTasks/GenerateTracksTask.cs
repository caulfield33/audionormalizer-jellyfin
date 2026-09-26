using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioNormalizer.Models;
using Jellyfin.Plugin.AudioNormalizer.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioNormalizer.ScheduledTasks;

/// <summary>Builds companion tracks for everything that still needs one.</summary>
public class GenerateTracksTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly NormalizationService _service;
    private readonly StateStore _store;
    private readonly ILogger<GenerateTracksTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="GenerateTracksTask"/> class.</summary>
    /// <param name="service">Worker.</param>
    /// <param name="store">State store.</param>
    /// <param name="logger">Logger.</param>
    public GenerateTracksTask(NormalizationService service, StateStore store, ILogger<GenerateTracksTask> logger)
    {
        _service = service;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Build normalized audio tracks";

    /// <inheritdoc />
    public string Key => "AudioNormalizerGenerate";

    /// <inheritdoc />
    public string Description =>
        "Writes a normalized companion audio track next to each film that needs one. " +
        "Original files are never modified. Expect roughly ten to twenty minutes per film.";

    /// <inheritdoc />
    public string Category => "Audio Normalizer";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            _store.EnsureLoaded(Plugin.Instance.DataFolderPath);
        }

        var config = Plugin.Instance?.Configuration;
        var items = _service.GetCandidateItems();

        var pending = items
            .Where(i =>
            {
                var r = _store.Get(i.Id);
                return r is null || r.RollupState() is not (TrackState.Done or TrackState.Skipped);
            })
            .ToList();

        _logger.LogInformation("Audio Normalizer: {Count} item(s) to build", pending.Count);

        var budgetBytes = (long)((config?.MaxTotalOutputGb ?? 0) * 1024 * 1024 * 1024);
        long written = 0;

        for (var i = 0; i < pending.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var before = TotalOutput(_store.Get(pending[i].Id));
            await _service.GenerateAsync(pending[i].Id, false, null, cancellationToken).ConfigureAwait(false);
            var after = TotalOutput(_store.Get(pending[i].Id));
            written += Math.Max(0, after - before);

            progress.Report((i + 1) * 100.0 / Math.Max(1, pending.Count));

            if (budgetBytes > 0 && written >= budgetBytes)
            {
                _logger.LogInformation(
                    "Audio Normalizer: stopping, the {Limit} GB per-run limit was reached",
                    config?.MaxTotalOutputGb);
                break;
            }
        }

        _store.Flush();
        progress.Report(100);
    }

    private static long TotalOutput(Jellyfin.Plugin.AudioNormalizer.Models.TrackRecord? record)
    {
        if (record is null)
        {
            return 0;
        }

        long total = 0;
        foreach (var t in record.AudioTracks)
        {
            total += t.OutputSize;
        }

        return total;
    }
}
