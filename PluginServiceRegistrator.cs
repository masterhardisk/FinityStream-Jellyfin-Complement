using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FinityStream;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LibraryNotifications>();
        serviceCollection.AddScoped<ArtworkChangeFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<ArtworkChangeFilter>());
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<LibraryNotifications>());
    }
}
