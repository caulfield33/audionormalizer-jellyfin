using Jellyfin.Plugin.AudioNormalizer.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.AudioNormalizer;

/// <summary>Wires the plugin's services into the server's container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Singletons: the state store holds the whole report in memory and the queue must be
        // one shared instance, or two callers would each start their own worker.
        serviceCollection.AddSingleton<StateStore>();
        serviceCollection.AddSingleton<FfmpegRunner>();
        serviceCollection.AddSingleton<NormalizationService>();
        serviceCollection.AddSingleton<JobQueue>();

        // Registered again as a hosted service so the server starts and stops the worker with it,
        // resolving to the same instance the API controller talks to.
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<JobQueue>());
    }
}
