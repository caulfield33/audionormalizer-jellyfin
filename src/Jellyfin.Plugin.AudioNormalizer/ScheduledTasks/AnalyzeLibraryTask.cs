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

/// <summary>Measures every candidate item so the report can show real numbers.</summary>
public class AnalyzeLibraryTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly NormalizationService _service;
    private readonly StateStore _store;
    private readonly ILogger<AnalyzeLibraryTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="AnalyzeLibraryTask"/> class.</summary>
    /// <param name="service">Worker.</param>
    /// <param name="store">State store.</param>
    /// <param name="logger">Logger.</param>
    public AnalyzeLibraryTask(NormalizationService service, StateStore store, ILogger<AnalyzeLibraryTask> logger)
    {
        _service = service;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Measure audio loudness";

    /// <inheritdoc />
    public string Key => "AudioNormalizerAnalyze";

    /// <inheritdoc />
    public string Description =>
        "Measures loudness and dynamic range for every film and episode, so the Audio Normalizer " +
        "report can show which ones have quiet dialogue and loud effects. Writes nothing to your media.";

    /// <inheritdoc />
    public string Category => "Audio Normalizer";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    };

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            _store.EnsureLoaded(Plugin.Instance.DataFolderPath);
        }

        var items = _service.GetCandidateItems();

        // Only measure what has not been measured, so a weekly run over a big library is cheap
        // after the first pass.
        var pending = items.Where(i =>
        {
            var r = _store.Get(i.Id);
            return r is null || r.AudioTracks.Count == 0 || r.AudioTracks.TrueForAll(t => t.Source is null);
        }).ToList();

        _logger.LogInformation(
            "Audio Normalizer: {Pending} of {Total} items still need measuring",
            pending.Count,
            items.Count);

        for (var i = 0; i < pending.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _service.AnalyzeAsync(pending[i].Id, null, cancellationToken).ConfigureAwait(false);
            progress.Report((i + 1) * 100.0 / Math.Max(1, pending.Count));
        }

        _store.Flush();
        progress.Report(100);
    }
}
