using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinityStream.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }

    public bool NotifyMovies { get; set; } = true;

    public bool NotifyEpisodes { get; set; } = true;

    public bool GroupEpisodes { get; set; } = true;

    public string EnrollmentSecret { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;
}
