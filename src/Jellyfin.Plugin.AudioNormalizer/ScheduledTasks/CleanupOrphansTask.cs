using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioNormalizer.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioNormalizer.ScheduledTasks;

/// <summary>Removes generated tracks whose film is no longer there.</summary>
public class CleanupOrphansTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly NormalizationService _service;
    private readonly StateStore _store;
    private readonly ILogger<CleanupOrphansTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="CleanupOrphansTask"/> class.</summary>
    /// <param name="service">Worker.</param>
    /// <param name="store">State store.</param>
    /// <param name="logger">Logger.</param>
    public CleanupOrphansTask(NormalizationService service, StateStore store, ILogger<CleanupOrphansTask> logger)
    {
        _service = service;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Remove orphaned normalized tracks";

    /// <inheritdoc />
    public string Key => "AudioNormalizerCleanup";

    /// <inheritdoc />
    public string Description =>
        "Deletes normalized tracks this plugin created whose original film has since been removed " +
        "or moved. Only files recorded by the plugin are touched.";

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
            DayOfWeek = DayOfWeek.Monday,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        }
    };

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            _store.EnsureLoaded(Plugin.Instance.DataFolderPath);
        }

        var removed = _service.CleanupOrphans(cancellationToken);
        _logger.LogInformation("Audio Normalizer: removed {Count} orphaned track(s)", removed);
        progress.Report(100);
        return Task.CompletedTask;
    }
}
