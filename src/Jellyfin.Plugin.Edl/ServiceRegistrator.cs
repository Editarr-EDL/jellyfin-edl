using Jellyfin.Plugin.Edl.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Edl;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IEdlParser, EdlParser>();
        serviceCollection.AddSingleton<EdlExportFormat>();
        serviceCollection.AddSingleton<McfExportFormat>();
        serviceCollection.AddSingleton<AuthorizedMediaResolver>();
        serviceCollection.AddSingleton<ExportSelector>();
    }
}
