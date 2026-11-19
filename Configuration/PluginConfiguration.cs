using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinityStream.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }

    public string WebhookSecret { get; set; } = string.Empty;
}
