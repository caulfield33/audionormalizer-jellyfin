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

    /// <summary>Gets or sets the jobs waiting, in the order they will run.</summary>
    public List<QueuedJob> PendingJobs { get; set; } = new List<QueuedJob>();

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

    // One per running job, so a single item can be stopped without touching the others.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancels = new();

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

        // A restart in the middle of a run leaves tracks marked queued or running for ever,
        // and the report believes it. Clear that before the pump starts.
        ResetPendingStates(includeRunning: true);

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

    /// <summary>
    /// Stops everything: drops what is waiting, cancels what is running, and puts the state of
    /// every affected track back so the report stops claiming the work is queued.
    /// </summary>
    /// <returns>How many jobs were dropped or cancelled.</returns>
    public int Clear()
    {
        var stopped = 0;

        while (_queue.TryDequeue(out var job))
        {
            lock (_queuedLock)
            {
                _queued.Remove(job.ItemId);
            }

            stopped++;
        }

        // Running jobs are cancelled rather than left to finish. The button says stop, and a
        // cancelled encode cleans up its own part file, so nothing is left half written.
        foreach (var pair in _jobCancels)
        {
            try
            {
                pair.Value.Cancel();
                stopped++;
            }
            catch (ObjectDisposedException)
            {
                // The job finished between the snapshot and the cancel. Nothing to do.
            }
        }

        // A sweep of the whole store, not just the jobs above: an item whose job was refused
        // early - excluded, file missing, no audio - never had its track states cleared, so the
        // table went on showing "queued" for work that had already been abandoned.
        ResetPendingStates(includeRunning: true);

        _logger.LogInformation("Audio Normalizer: queue stopped, {Count} job(s) dropped or cancelled", stopped);
        return stopped;
    }

    /// <summary>Takes specific items out of the queue, cancelling them if they are running.</summary>
    /// <param name="itemIds">Items to remove.</param>
    /// <returns>How many were removed.</returns>
    public int Dequeue(IEnumerable<Guid> itemIds)
    {
        var wanted = new HashSet<Guid>(itemIds);
        if (wanted.Count == 0)
        {
            return 0;
        }

        var removed = 0;

        // A ConcurrentQueue cannot drop from the middle, so it is drained and refilled. Anything
        // enqueued meanwhile simply lands behind the survivors, which is harmless.
        var keep = new List<QueuedJob>();
        while (_queue.TryDequeue(out var job))
        {
            if (wanted.Contains(job.ItemId))
            {
                lock (_queuedLock)
                {
                    _queued.Remove(job.ItemId);
                }

                ClearQueuedState(job.ItemId);
                removed++;
            }
            else
            {
                keep.Add(job);
            }
        }

        foreach (var job in keep)
        {
            _queue.Enqueue(job);
        }

        foreach (var id in wanted)
        {
            if (_jobCancels.TryGetValue(id, out var cts))
            {
                try
                {
                    cts.Cancel();
                    removed++;
                }
                catch (ObjectDisposedException)
                {
                    // Finished on its own in the meantime.
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Puts tracks left claiming to be queued back to where they were. Called after a stop and
    /// at startup, because a server restart in the middle of a run leaves the same lie behind.
    /// </summary>
    /// <param name="includeRunning">Also reset tracks stuck in the running state.</param>
    private void ResetPendingStates(bool includeRunning)
    {
        foreach (var record in _store.All())
        {
            var touched = false;
            foreach (var track in record.AudioTracks)
            {
                var stale = track.State == TrackState.Queued
                    || (includeRunning && track.State == TrackState.Running);
                if (!stale)
                {
                    continue;
                }

                track.State = track.Source is null ? TrackState.Unknown : TrackState.Analyzed;
                touched = true;
            }

            if (touched)
            {
                _store.Put(record);
            }
        }
    }

    /// <summary>Resets the queued tracks of one item.</summary>
    /// <param name="itemId">The item.</param>
    private void ClearQueuedState(Guid itemId)
    {
        var record = _store.Get(itemId);
        if (record is null)
        {
            return;
        }

        var touched = false;
        foreach (var track in record.AudioTracks)
        {
            if (track.State != TrackState.Queued)
            {
                continue;
            }

            track.State = track.Source is null ? TrackState.Unknown : TrackState.Analyzed;
            touched = true;
        }

        if (touched)
        {
            _store.Put(record);
        }
    }

    /// <summary>Gets a snapshot for the settings page.</summary>
    /// <returns>The status.</returns>
    public QueueStatus GetStatus() => new QueueStatus
    {
        Pending = _queue.Count,
        PendingJobs = _queue.ToList(),
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

            // A stop that arrived while this job waited - for playback to end, or for a worker
            // slot - must drop it. Once dequeued it is invisible to Clear(), so without this it
            // would start anyway and look like the stop was ignored. That is exactly what
            // "I stopped it and it kept going" was.
            lock (_queuedLock)
            {
                if (!_queued.Contains(job.ItemId))
                {
                    ClearQueuedState(job.ItemId);
                    continue;
                }
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

        // Its own cancellation source, linked to the pump's, so one item can be stopped without
        // disturbing the others.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _jobCancels[job.ItemId] = jobCts;

        var progress = new Progress<double>(p => job.Progress = p);

        try
        {
            var outcome = job.Kind == JobKind.Analyze
                ? await _service.AnalyzeAsync(job.ItemId, progress, jobCts.Token).ConfigureAwait(false)
                : await _service.GenerateAsync(job.ItemId, job.Force, progress, jobCts.Token).ConfigureAwait(false);

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
            _jobCancels.TryRemove(job.ItemId, out _);
            _running.TryRemove(job.ItemId, out _);
            lock (_queuedLock)
            {
                _queued.Remove(job.ItemId);
            }

            // Whatever happened, this item is no longer queued. A job refused early - excluded,
            // file missing, no audio - never set its tracks to anything, so they kept claiming
            // to be queued long after the queue had emptied.
            ClearQueuedState(job.ItemId);

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
