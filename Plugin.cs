using Jellyfin.Plugin.FinityStream.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FinityStream;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer serializer)
        : base(applicationPaths, serializer)
    {
        Instance = this;
    }

    internal static Plugin? Instance { get; private set; }

    public override string Name => "FinityStream Notifications";

    public override Guid Id => new("f8faee20-de25-47cc-b2d0-6413e3d78673");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "FinityStreamNotifications",
            EmbeddedResourcePath = "Jellyfin.Plugin.FinityStream.Configuration.config.html",
        };
    }
}
