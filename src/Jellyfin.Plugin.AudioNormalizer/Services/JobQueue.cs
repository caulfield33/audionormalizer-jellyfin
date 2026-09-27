using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioNormalizer.Configuration;
using Jellyfin.Plugin.AudioNormalizer.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>What kind of work a queue entry represents.</summary>
public enum JobKind
{
    /// <summary>Measure the source only.</summary>
    Analyze = 0,

    /// <summary>Measure if needed, then build the track.</summary>
    Generate = 1
}

/// <summary>One queued unit of work.</summary>
public sealed class QueuedJob
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name, for the status view.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of work.</summary>
    public JobKind Kind { get; set; }

    /// <summary>Gets or sets a value indicating whether an existing track is rebuilt.</summary>
    public bool Force { get; set; }

    /// <summary>Gets or sets progress, 0-100.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets when the job started.</summary>
    public DateTime? StartedUtc { get; set; }
}

/// <summary>A snapshot of the queue for the settings page.</summary>
public sealed class QueueStatus
{
    /// <summary>Gets or sets the number of jobs waiting.</summary>
    public int Pending { get; set; }

    /// <summary>Gets or sets the jobs currently running.</summary>
    public List<QueuedJob> Running { get; set; } = new List<QueuedJob>();

    /// <summary>Gets or sets a value indicating whether the queue is paused for playback.</summary>
    public bool PausedForPlayback { get; set; }

    /// <summary>Gets or sets how many jobs finished since the server started.</summary>
    public int Completed { get; set; }

    /// <summary>Gets or sets how many jobs failed since the server started.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the last message worth showing.</summary>
    public string LastMessage { get; set; } = string.Empty;
}

/// <summary>
/// Runs queued work in the background, one or a few jobs at a time, and gets out of the way
/// while somebody is watching something.
/// </summary>
public sealed class JobQueue : IHostedService, IDisposable
{
    private readonly ILogger<JobQueue> _logger;
    private readonly NormalizationService _service;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly StateStore _store;

    private readonly ConcurrentQueue<QueuedJob> _queue = new();
    private readonly ConcurrentDictionary<Guid, QueuedJob> _running = new();
    private readonly HashSet<Guid> _queued = new();
    private readonly object _queuedLock = new();
    private readonly SemaphoreSlim _signal = new(0);

    private CancellationTokenSource? _cts;
    private Task? _pump;
    private int _completed;
    private int _failed;
    private string _lastMessage = string.Empty;
    private bool _pausedForPlayback;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="JobQueue"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="service">The worker.</param>
    /// <param name="sessionManager">Used to notice active playback.</param>
    /// <param name="libraryManager">Used to name queued items.</param>
    /// <param name="store">State store.</param>
    public JobQueue(
        ILogger<JobQueue> logger,
        NormalizationService service,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        StateStore store)
    {
        _logger = logger;
        _service = service;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _store = store;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            _store.EnsureLoaded(Plugin.Instance.DataFolderPath);
        }

        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio Normalizer: queue did not stop cleanly");
            }
        }

        _store.Flush();
    }

    /// <summary>Adds work to the queue, ignoring items already queued or running.</summary>
    /// <param name="itemIds">Items to enqueue.</param>
    /// <param name="kind">Kind of work.</param>
    /// <param name="force">Rebuild existing tracks.</param>
    /// <returns>How many jobs were actually added.</returns>
    public int Enqueue(IEnumerable<Guid> itemIds, JobKind kind, bool force)
    {
        var added = 0;
        foreach (var id in itemIds)
        {
            lock (_queuedLock)
            {
                if (!_queued.Add(id))
                {
                    continue;
                }
            }

            var name = _libraryManager.GetItemById(id)?.Name ?? id.ToString("N");
            _queue.Enqueue(new QueuedJob { ItemId = id, ItemName = name, Kind = kind, Force = force });

            var record = _store.GetOrCreate(id);
            foreach (var track in record.AudioTracks)
            {
                if (track.Selected && track.State is not TrackState.Running)
                {
                    track.State = TrackState.Queued;
                }
            }

            _store.Put(record);

            added++;
            _signal.Release();
        }

        if (added > 0)
        {
            _logger.LogInformation("Audio Normalizer: queued {Count} item(s) for {Kind}", added, kind);
        }

        return added;
    }

    /// <summary>Empties the queue. Jobs already running are left to finish.</summary>
    /// <returns>How many jobs were dropped.</returns>
    public int Clear()
    {
        var dropped = 0;
        while (_queue.TryDequeue(out var job))
        {
            lock (_queuedLock)
            {
                _queued.Remove(job.ItemId);
            }

            var record = _store.Get(job.ItemId);
            if (record is not null)
            {
                foreach (var track in record.AudioTracks)
                {
                    if (track.State == TrackState.Queued)
                    {
                        track.State = track.Source is null ? TrackState.Unknown : TrackState.Analyzed;
                    }
                }

                _store.Put(record);
            }

            dropped++;
        }

        return dropped;
    }

    /// <summary>Gets a snapshot for the settings page.</summary>
    /// <returns>The status.</returns>
    public QueueStatus GetStatus() => new QueueStatus
    {
        Pending = _queue.Count,
        Running = _running.Values.ToList(),
        PausedForPlayback = _pausedForPlayback,
        Completed = _completed,
        Failed = _failed,
        LastMessage = _lastMessage
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _signal.Dispose();
    }

    private async Task PumpAsync(CancellationToken token)
    {
        var workers = new List<Task>();

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_queue.TryDequeue(out var job))
            {
                continue;
            }

            // Wait out any active playback before starting. Audio jobs are not heavy, but a
            // library-wide run competing with a live transcode is exactly the kind of thing
            // that makes a server feel broken.
            var holder = HoldingSession();
            if (holder is not null)
            {
                var heldSince = DateTime.UtcNow;

                // Logged because a session that never sends a stop pins the queue for hours and
                // nothing else records it: the page only shows the flag while somebody is
                // looking at it, so overnight the stall is invisible.
                _logger.LogInformation("Audio Normalizer: holding the queue, {Device} is playing", holder);

                while (!token.IsCancellationRequested && holder is not null)
                {
                    _pausedForPlayback = true;
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    holder = HoldingSession();
                }

                _logger.LogInformation(
                    "Audio Normalizer: resuming, queue was held for {Minutes:F1} min",
                    (DateTime.UtcNow - heldSince).TotalMinutes);
            }

            _pausedForPlayback = false;
            if (token.IsCancellationRequested)
            {
                break;
            }

            workers.RemoveAll(t => t.IsCompleted);
            var limit = Math.Clamp(Config.MaxParallelJobs, 1, 8);
            while (workers.Count >= limit && !token.IsCancellationRequested)
            {
                var finished = await Task.WhenAny(workers).ConfigureAwait(false);
                workers.Remove(finished);
            }

            workers.Add(Task.Run(() => RunJobAsync(job, token), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: workers ended with an error");
        }
    }

    private async Task RunJobAsync(QueuedJob job, CancellationToken token)
    {
        // Kept in a local as well: StartedUtc is nullable on the job, and the elapsed time is
        // needed in the finally where unwrapping it again would only add noise.
        var startedUtc = DateTime.UtcNow;
        job.StartedUtc = startedUtc;
        _running[job.ItemId] = job;

        var progress = new Progress<double>(p => job.Progress = p);

        try
        {
            var outcome = job.Kind == JobKind.Analyze
                ? await _service.AnalyzeAsync(job.ItemId, progress, token).ConfigureAwait(false)
                : await _service.GenerateAsync(job.ItemId, job.Force, progress, token).ConfigureAwait(false);

            if (outcome.Success)
            {
                Interlocked.Increment(ref _completed);
            }
            else
            {
                Interlocked.Increment(ref _failed);
            }

            _lastMessage = job.ItemName + ": " + outcome.Message;
        }
        catch (OperationCanceledException)
        {
            _lastMessage = job.ItemName + ": cancelled";
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            _lastMessage = job.ItemName + ": " + ex.Message;
            _logger.LogError(ex, "Audio Normalizer: job failed for {Name}", job.ItemName);
        }
        finally
        {
            _running.TryRemove(job.ItemId, out _);
            lock (_queuedLock)
            {
                _queued.Remove(job.ItemId);
            }

            _store.Flush();

            // How long a film actually took. Without this there is no way to tell a slow
            // encode from a queue that spent the night paused.
            _logger.LogInformation(
                "Audio Normalizer: {Kind} for {Name} took {Minutes:F1} min",
                job.Kind,
                job.ItemName,
                (DateTime.UtcNow - startedUtc).TotalMinutes);
        }
    }

    /// <summary>
    /// The device currently blocking the queue, or null when nothing is playing. Returns the
    /// name rather than a bool so the hold can say who caused it.
    /// </summary>
    private string? HoldingSession()
    {
        if (!Config.PauseWhilePlaybackActive)
        {
            return null;
        }

        try
        {
            var session = _sessionManager.Sessions
                .FirstOrDefault(s => s.NowPlayingItem is not null && !(s.PlayState?.IsPaused ?? false));
            if (session is null)
            {
                return null;
            }

            return string.IsNullOrEmpty(session.DeviceName) ? "a client" : session.DeviceName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: could not read sessions");
            return null;
        }
    }
}
