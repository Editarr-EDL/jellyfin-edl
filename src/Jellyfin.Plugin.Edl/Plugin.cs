using System;
using Jellyfin.Plugin.Edl.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Edl;

public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "EDL Sidecar API";

    public override Guid Id => Guid.Parse("8b8e1f77-94f8-4e8f-8eaf-c7a8d8bb8d80");

    public override string Description => "Resolves and returns sidecar EDL files for library video items.";
}
