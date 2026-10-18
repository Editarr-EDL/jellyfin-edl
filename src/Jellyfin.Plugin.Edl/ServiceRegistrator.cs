using Jellyfin.Plugin.Edl.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Edl;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ILibraryItemResolver, JellyfinLibraryItemResolver>();
        serviceCollection.AddSingleton<IEdlLookupService, EdlLookupService>();
        serviceCollection.AddSingleton<IEdlParser, EdlParser>();
    }
}
