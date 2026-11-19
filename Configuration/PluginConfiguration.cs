using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinityStream.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }

    public string EnrollmentSecret { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;
}
