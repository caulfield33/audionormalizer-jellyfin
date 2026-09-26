using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AudioNormalizer.Configuration;
using Jellyfin.Plugin.AudioNormalizer.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>
/// Turns the server's stream list into the plugin's own track list, and decides which of
/// them get normalized when the user has not chosen for themselves.
/// </summary>
public static class SourceTrackSelector
{
    /// <summary>
    /// Builds the full track list for an item, with the selection applied.
    /// </summary>
    /// <param name="streams">All streams reported for the item.</param>
    /// <param name="config">Plugin settings.</param>
    /// <param name="itemId">The item, used to look up an explicit per-item selection.</param>
    /// <returns>Every internal audio track, in stream order.</returns>
    public static List<AudioTrackInfo> BuildTrackList(
        IReadOnlyList<MediaStream> streams,
        PluginConfiguration config,
        Guid itemId)
    {
        var tracks = streams
            .Where(s => s.Type == MediaStreamType.Audio)
            // Never offer an external track as a source: that would mean normalizing a
            // track this plugin generated earlier, and then normalizing that again.
            .Where(s => !s.IsExternal)
            .OrderBy(s => s.Index)
            .Select(s => new AudioTrackInfo
            {
                StreamIndex = s.Index,
                Codec = s.Codec,
                Channels = s.Channels ?? 0,
                ChannelLayout = s.ChannelLayout,
                Language = s.Language,
                Title = s.Title,
                IsDefault = s.IsDefault,
                IsCommentary = IsCommentary(s, config),
                Label = Describe(s)
            })
            .ToList();

        ApplySelection(tracks, config, itemId);
        return tracks;
    }

    /// <summary>
    /// Applies the user's explicit per-item choice, or the automatic rule when they have
    /// not made one. Existing measurements on the tracks are left alone.
    /// </summary>
    /// <param name="tracks">The track list to mark up.</param>
    /// <param name="config">Plugin settings.</param>
    /// <param name="itemId">The item.</param>
    public static void ApplySelection(List<AudioTrackInfo> tracks, PluginConfiguration config, Guid itemId)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var over = config.FindOverride(itemId);

        if (over is { HasExplicitTrackSelection: true })
        {
            foreach (var t in tracks)
            {
                t.Selected = over.SelectedStreamIndexes.Contains(t.StreamIndex);
                t.SelectionIsExplicit = true;
            }

            return;
        }

        foreach (var t in tracks)
        {
            t.Selected = false;
            t.SelectionIsExplicit = false;
        }

        var candidates = tracks.ToList();
        if (config.SkipCommentaryTracks)
        {
            var filtered = candidates.Where(t => !t.IsCommentary).ToList();
            if (filtered.Count > 0)
            {
                candidates = filtered;
            }
        }

        var chosen = config.SourceSelection switch
        {
            SourceTrackSelection.DefaultTrack => candidates.FirstOrDefault(t => t.IsDefault) ?? candidates.FirstOrDefault(),
            SourceTrackSelection.PreferredLanguage => ByLanguage(candidates, config) ?? MostChannels(candidates),
            SourceTrackSelection.MostChannels => MostChannels(candidates),
            _ => MostChannels(candidates)
        };

        if (chosen is not null)
        {
            chosen.Selected = true;
        }
    }

    /// <summary>Builds a short human readable label for the report.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>A label.</returns>
    public static string Describe(MediaStream stream)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(stream.Codec))
        {
            parts.Add(stream.Codec.ToUpperInvariant());
        }

        parts.Add(ChannelsLabel(stream.Channels ?? 0));

        if (!string.IsNullOrEmpty(stream.Language))
        {
            parts.Add(stream.Language);
        }

        if (!string.IsNullOrEmpty(stream.Title))
        {
            parts.Add(stream.Title);
        }

        return string.Join(" / ", parts.Where(p => p.Length > 0));
    }

    /// <summary>Builds the same label from the plugin's own track model.</summary>
    /// <param name="track">The track.</param>
    /// <returns>A label.</returns>
    public static string Describe(AudioTrackInfo track)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(track.Codec))
        {
            parts.Add(track.Codec.ToUpperInvariant());
        }

        parts.Add(ChannelsLabel(track.Channels));

        if (!string.IsNullOrEmpty(track.Language))
        {
            parts.Add(track.Language);
        }

        if (!string.IsNullOrEmpty(track.Title))
        {
            parts.Add(track.Title);
        }

        return string.Join(" / ", parts.Where(p => p.Length > 0));
    }

    private static string ChannelsLabel(int channels) => channels switch
    {
        0 => string.Empty,
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => channels.ToString(CultureInfo.InvariantCulture) + "ch"
    };

    private static AudioTrackInfo? MostChannels(List<AudioTrackInfo> candidates) =>
        candidates
            .OrderByDescending(t => t.Channels)
            .ThenByDescending(t => t.IsDefault)
            .FirstOrDefault();

    private static AudioTrackInfo? ByLanguage(List<AudioTrackInfo> candidates, PluginConfiguration config)
    {
        foreach (var lang in config.PreferredSourceLanguages)
        {
            if (string.IsNullOrWhiteSpace(lang))
            {
                continue;
            }

            var hit = candidates
                .Where(t => string.Equals(t.Language, lang.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Channels)
                .FirstOrDefault();
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static bool IsCommentary(MediaStream stream, PluginConfiguration config)
    {
        var haystack = ((stream.Title ?? string.Empty) + " " + (stream.DisplayTitle ?? string.Empty)).ToLowerInvariant();
        if (haystack.Trim().Length == 0)
        {
            return false;
        }

        return config.CommentaryKeywords.Any(k =>
            !string.IsNullOrWhiteSpace(k) && haystack.Contains(k.Trim().ToLowerInvariant(), StringComparison.Ordinal));
    }
}
