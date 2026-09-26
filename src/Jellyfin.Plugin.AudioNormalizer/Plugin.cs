using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.AudioNormalizer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AudioNormalizer;

/// <summary>
/// Generates a loudness-normalized companion audio track next to each film, leaving the
/// original file untouched, so quiet dialogue and loud explosions sit closer together.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Server paths.</param>
    /// <param name="xmlSerializer">Serializer used for the config file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running instance, so services without DI access can read the config.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Audio Normalizer";

    /// <inheritdoc />
    public override Guid Id => new Guid("2a968ad7-6168-44c8-b149-cf94eb870b25");

    /// <inheritdoc />
    public override string Description =>
        "Builds a second, loudness-normalized audio track for each film so dialogue stays audible " +
        "without explosions being painful. The original track is never modified.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                prefix)
        };
        yield return new PluginPageInfo
        {
            Name = "audionormalizerjs",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.js",
                prefix)
        };
    }
}
