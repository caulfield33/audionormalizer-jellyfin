using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.AudioNormalizer.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>
/// Durable per-item state, kept as a single JSON file in the plugin's data folder.
/// Everything the report shows and every "already done, skip it" decision comes from here.
/// </summary>
public sealed class StateStore : IDisposable
{
    private const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions { WriteIndented = false };

    private readonly ILogger<StateStore> _logger;
    private readonly ConcurrentDictionary<string, TrackRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileLock = new();
    private readonly Timer _flushTimer;
    private string _path = string.Empty;
    private int _dirty;
    private bool _loaded;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="StateStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public StateStore(ILogger<StateStore> logger)
    {
        _logger = logger;
        // Writes are coalesced: a library-wide run touches thousands of records and we do not
        // want thousands of file rewrites.
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Loads the store from the plugin data folder. Safe to call repeatedly.</summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    public void EnsureLoaded(string dataFolder)
    {
        if (_loaded)
        {
            return;
        }

        lock (_fileLock)
        {
            if (_loaded)
            {
                return;
            }

            Directory.CreateDirectory(dataFolder);
            _path = Path.Combine(dataFolder, "state.json");
            _loaded = true;

            if (!File.Exists(_path))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_path);
                var doc = JsonSerializer.Deserialize<StateFile>(json);
                if (doc?.Records is not null)
                {
                    foreach (var r in doc.Records)
                    {
                        if (!string.IsNullOrEmpty(r.ItemId))
                        {
                            _records[r.ItemId] = r;
                        }
                    }
                }

                _logger.LogInformation("Audio Normalizer: loaded {Count} records", _records.Count);
            }
            catch (Exception ex)
            {
                // A corrupt state file must never stop the plugin loading. Move it aside and start fresh;
                // the worst case is that everything gets re-analyzed.
                _logger.LogError(ex, "Audio Normalizer: state file unreadable, starting fresh");
                TryQuarantine();
            }
        }
    }

    /// <summary>Gets every record.</summary>
    /// <returns>All records.</returns>
    public IReadOnlyList<TrackRecord> All() => _records.Values.ToList();

    /// <summary>Gets one record.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The record, or null.</returns>
    public TrackRecord? Get(Guid itemId) =>
        _records.TryGetValue(itemId.ToString("N"), out var r) ? r : null;

    /// <summary>Gets a record, creating an empty one if needed.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The record.</returns>
    public TrackRecord GetOrCreate(Guid itemId)
    {
        var key = itemId.ToString("N");
        return _records.GetOrAdd(key, _ => new TrackRecord { ItemId = key });
    }

    /// <summary>Stores a record and marks the store dirty.</summary>
    /// <param name="record">The record.</param>
    public void Put(TrackRecord record)
    {
        if (string.IsNullOrEmpty(record.ItemId))
        {
            return;
        }

        _records[record.ItemId] = record;
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Drops a record.</summary>
    /// <param name="itemId">Item id.</param>
    public void Remove(Guid itemId)
    {
        if (_records.TryRemove(itemId.ToString("N"), out _))
        {
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    /// <summary>Writes the store to disk if anything changed.</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0 || string.IsNullOrEmpty(_path))
        {
            return;
        }

        lock (_fileLock)
        {
            try
            {
                var doc = new StateFile { Schema = CurrentSchema, Records = _records.Values.ToList() };
                var json = JsonSerializer.Serialize(doc, SerializerOptions);

                // Write to a sibling temp file and move it into place, so a crash or a full disk
                // can never leave a half-written state file behind.
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audio Normalizer: could not save state");
                Interlocked.Exchange(ref _dirty, 1);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Dispose();
        Flush();
    }

    private void TryQuarantine()
    {
        try
        {
            var bad = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(_path, bad, true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio Normalizer: could not quarantine the bad state file");
        }
    }

    private sealed class StateFile
    {
        public int Schema { get; set; }

        public List<TrackRecord> Records { get; set; } = new List<TrackRecord>();
    }
}
