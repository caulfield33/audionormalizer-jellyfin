using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AudioNormalizer.Configuration;
using Jellyfin.Plugin.AudioNormalizer.Models;

namespace Jellyfin.Plugin.AudioNormalizer.Services;

/// <summary>
/// Works out what to call the generated file.
///
/// Jellyfin only picks up an external audio file when all of this holds, which was read
/// out of Emby.Naming/MediaInfoResolver rather than guessed:
///
/// * it sits in the same folder as the video (or the item's metadata folder);
/// * its name starts with the video's file name without extension, case-insensitively;
/// * the next character is a media flag delimiter, and the only delimiter Jellyfin
///   defines is '.';
/// * its extension is in NamingOptions.AudioFileExtensions;
/// * every dot-separated piece after the prefix is then parsed: a piece that looks like
///   a language becomes the language, "default"/"forced"/"foreign"/"cc"/"hi"/"sdh"
///   become flags, and whatever is left becomes the displayed title.
///
/// So the title must avoid those flag words and must not itself look like a language name,
/// or the track shows up unnamed, or worse, silently marked forced.
///
/// When several tracks of one film are normalized, their file names must also differ from
/// each other, which is what <see cref="BuildTitle"/> and the collision guard handle.
/// </summary>
public static class OutputNaming
{
    /// <summary>
    /// Words Jellyfin matches as a SUBSTRING of a name piece. Verified in
    /// ExternalPathParser: MediaDefaultFlags and MediaForcedFlags are tested with
    /// <c>Contains</c>, so a title merely containing one of these is turned into a flag and
    /// the whole piece is removed from the displayed name.
    /// </summary>
    private static readonly string[] SubstringFlags = { "default", "forced", "foreign" };

    /// <summary>
    /// Words Jellyfin matches only as the WHOLE piece: MediaHearingImpairedFlags is tested
    /// with <c>Equals</c>.
    /// </summary>
    private static readonly string[] ExactFlags = { "cc", "hi", "sdh" };

    /// <summary>Fallback marker used when nothing usable can be derived.</summary>
    public const string Marker = "AudioNormalizer";

    /// <summary>Gets the file extension for a container.</summary>
    /// <param name="container">The container.</param>
    /// <param name="codec">The codec, used when the container is a raw stream.</param>
    /// <returns>The extension including the dot.</returns>
    public static string ExtensionFor(OutputContainer container, OutputCodec codec) => container switch
    {
        OutputContainer.Mka => ".mka",
        OutputContainer.M4a => ".m4a",
        OutputContainer.Raw => codec switch
        {
            OutputCodec.Ac3 => ".ac3",
            OutputCodec.Eac3 => ".eac3",
            OutputCodec.Flac => ".flac",
            OutputCodec.Opus => ".opus",
            _ => ".aac"
        },
        _ => ".mka"
    };

    /// <summary>
    /// The ffmpeg muxer name for a container.
    ///
    /// This has to be passed explicitly with -f. The encode writes to a temporary
    /// "<c>.an-part</c>" file so a half-written track is never picked up by the library,
    /// and ffmpeg cannot guess a muxer from that extension: it fails outright with
    /// "Unable to choose an output format".
    /// </summary>
    /// <param name="container">The container.</param>
    /// <param name="codec">The codec, used when the container is a raw stream.</param>
    /// <returns>The ffmpeg muxer name.</returns>
    public static string MuxerFor(OutputContainer container, OutputCodec codec) => container switch
    {
        OutputContainer.Mka => "matroska",
        OutputContainer.M4a => "ipod",
        OutputContainer.Raw => codec switch
        {
            OutputCodec.Ac3 => "ac3",
            OutputCodec.Eac3 => "eac3",
            OutputCodec.Flac => "flac",
            OutputCodec.Opus => "opus",
            _ => "adts"
        },
        _ => "matroska"
    };

    /// <summary>
    /// Builds the title shown in the player, from the configured prefix and the source
    /// track's own name: "AN - Ukrainian 5.1".
    /// </summary>
    /// <param name="profile">Settings.</param>
    /// <param name="track">The source track, or null for a generic name.</param>
    /// <returns>A title safe for Jellyfin's parser.</returns>
    public static string BuildTitle(NormalizationProfile profile, AudioTrackInfo? track)
    {
        var prefix = Clean(profile.TrackTitlePrefix);
        if (prefix.Length == 0)
        {
            prefix = "AN";
        }

        if (!profile.AppendSourceNameToTitle || track is null)
        {
            return Guard(prefix);
        }

        var source = DescribeForTitle(track, profile);
        return Guard(source.Length == 0 ? prefix : prefix + " - " + source);
    }

    /// <summary>
    /// Builds the full path of the generated track for one source stream.
    /// </summary>
    /// <param name="sourcePath">Path of the video file.</param>
    /// <param name="profile">Settings.</param>
    /// <param name="track">The source track.</param>
    /// <param name="takenNames">
    /// Titles already used by other tracks of the same film, so two tracks never collide on
    /// one file name. Pass null when uniqueness does not matter.
    /// </param>
    /// <returns>The path to write.</returns>
    public static string BuildOutputPath(
        string sourcePath,
        NormalizationProfile profile,
        AudioTrackInfo? track,
        ISet<string>? takenNames = null)
    {
        var folder = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = ExtensionFor(profile.Container, profile.Codec);

        var title = BuildTitle(profile, track);

        if (takenNames is not null)
        {
            var candidate = title;
            if (!takenNames.Add(candidate))
            {
                // Two source tracks produced the same name. Fall back to the stream index,
                // which is unique by definition.
                candidate = track is null
                    ? title + "-2"
                    : string.Create(CultureInfo.InvariantCulture, $"{title}-{track.StreamIndex}");
                var n = 2;
                while (!takenNames.Add(candidate))
                {
                    candidate = string.Create(CultureInfo.InvariantCulture, $"{title}-{n++}");
                }
            }

            title = candidate;
        }

        var parts = new List<string> { stem };

        var lang = SanitizeLanguage(track?.Language);
        if (!string.IsNullOrEmpty(lang))
        {
            parts.Add(lang);
        }

        parts.Add(title);

        if (profile.MakeDefaultTrack)
        {
            // Jellyfin reads this piece as the default flag and strips it from the title.
            parts.Add("default");
        }

        return Path.Combine(folder, string.Join('.', parts) + ext);
    }

    /// <summary>The path of the temporary file the encode writes to.</summary>
    /// <param name="finalPath">The final path.</param>
    /// <returns>A sibling temp path.</returns>
    public static string TempPathFor(string finalPath)
    {
        // A sibling, so the final move is a rename on the same filesystem and therefore atomic.
        // The extension is not one Jellyfin scans for, so a half-written file is never picked up.
        return finalPath + ".an-part";
    }

    /// <summary>
    /// Checks whether a path looks like a file this plugin generated. Used only as a
    /// secondary guard: the authoritative list is what the state store recorded.
    /// </summary>
    /// <param name="path">Path to test.</param>
    /// <param name="profile">Settings, for the configured prefix.</param>
    /// <returns>True when it matches the plugin's naming.</returns>
    public static bool LooksGenerated(string path, NormalizationProfile profile)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var prefix = Clean(profile.TrackTitlePrefix);
        return prefix.Length > 0 && name.Contains('.' + prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Makes a title safe for Jellyfin's external-file parser: no dots, no reserved flag
    /// words, nothing that would be mistaken for a language code.
    /// </summary>
    /// <param name="raw">The proposed title.</param>
    /// <returns>A safe title piece.</returns>
    public static string SanitizeTitle(string? raw) => Guard(Clean(raw));

    private static string Clean(string? raw)
    {
        var value = raw ?? string.Empty;

        // Dots would split the title into extra pieces and change how it is parsed.
        value = value.Replace('.', '-');
        value = Regex.Replace(value, @"[\\/:*?""<>|]", "-");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value;
    }

    private static string Guard(string value)
    {
        // Strip the substring-matched flags first. A source track literally called
        // "Default" would otherwise make Jellyfin drop the whole name and silently mark
        // the generated track as the default one.
        value = StripSubstringFlags(value);

        if (value.Length == 0)
        {
            value = Marker;
        }

        foreach (var reserved in ExactFlags)
        {
            if (value.Equals(reserved, StringComparison.OrdinalIgnoreCase))
            {
                // Would be swallowed as a hearing-impaired flag and never shown as a name.
                return value + "-track";
            }
        }

        if (LooksLikeLanguageToken(value))
        {
            // Would be parsed as the language and disappear from the title.
            return value + "-track";
        }

        return value;
    }

    /// <summary>
    /// Removes any occurrence of the substring-matched flag words and tidies up whatever
    /// separators that leaves behind.
    /// </summary>
    private static string StripSubstringFlags(string value)
    {
        foreach (var flag in SubstringFlags)
        {
            var idx = value.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                value = value.Remove(idx, flag.Length);
                idx = value.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            }
        }

        // "AN -  " and similar leftovers.
        value = Regex.Replace(value, @"\s+", " ");
        value = Regex.Replace(value, @"(^[\s\-]+)|([\s\-]+$)", string.Empty);
        return value.Trim();
    }

    /// <summary>
    /// Turns a source track into the bit that goes after the prefix. Prefers the track's own
    /// title, then language plus channel layout, then whatever the profile falls back to.
    /// </summary>
    private static string DescribeForTitle(AudioTrackInfo track, NormalizationProfile profile)
    {
        // A container title is the nicest name, but only once the flag words are out of it:
        // a track called "Default" or "Forced English" would otherwise poison the file name.
        if (!string.IsNullOrWhiteSpace(track.Title))
        {
            var fromTitle = StripSubstringFlags(Clean(track.Title));
            if (fromTitle.Length > 0)
            {
                return fromTitle;
            }
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Language))
        {
            parts.Add(track.Language.Trim());
        }

        var layout = track.Channels switch
        {
            1 => "mono",
            2 => "stereo",
            6 => "5-1",
            8 => "7-1",
            > 0 => track.Channels.ToString(CultureInfo.InvariantCulture) + "ch",
            _ => string.Empty
        };

        if (layout.Length > 0)
        {
            parts.Add(layout);
        }

        if (parts.Count == 0)
        {
            return StripSubstringFlags(Clean(profile.TrackTitle));
        }

        return StripSubstringFlags(Clean(string.Join(' ', parts)));
    }

    private static string? SanitizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var lang = language.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(lang, "^[a-z]{2,3}(-[a-z]{2,4})?$", RegexOptions.IgnoreCase))
        {
            return null;
        }

        // "hi" is Hindi but Jellyfin also treats it as the hearing-impaired flag. Use the
        // three letter form so the track is not mislabelled.
        if (lang.Equals("hi", StringComparison.OrdinalIgnoreCase))
        {
            return "hin";
        }

        return lang;
    }

    private static bool LooksLikeLanguageToken(string value) =>
        value.Length is 2 or 3 && value.All(char.IsLetter);
}
