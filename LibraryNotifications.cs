using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading.Channels;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinityStream;

internal sealed class LibraryNotifications(
    ILibraryManager library,
    IServerApplicationHost host,
    ILogger<LibraryNotifications> logger) : BackgroundService
{
    private static readonly string? ConfiguredWebhookUrl = typeof(LibraryNotifications).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "FinityStreamWebhookUrl")?.Value;

    private readonly Channel<LibraryEvent> _events = Channel.CreateBounded<LibraryEvent>(
        new BoundedChannelOptions(1024) { SingleReader = true });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(ConfiguredWebhookUrl, UriKind.Absolute, out var webhookUrl)
            || webhookUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(webhookUrl.UserInfo)
            || !string.IsNullOrEmpty(webhookUrl.Query)
            || !string.IsNullOrEmpty(webhookUrl.Fragment))
        {
            logger.LogWarning("FinityStream webhook URL is missing or invalid in this build");
            return;
        }

        using var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        library.ItemAdded += OnItemAdded;
        try
        {
            await foreach (var item in _events.Reader.ReadAllAsync(stoppingToken))
            {
                var config = Plugin.Instance?.Configuration;
                if (config?.Enabled != true)
                {
                    continue;
                }

                if (!IsValidSecret(config.WebhookSecret))
                {
                    logger.LogWarning("FinityStream webhook secret is invalid");
                    continue;
                }

                await SendAsync(http, webhookUrl, config.WebhookSecret, item, stoppingToken);
            }
        }
        finally
        {
            library.ItemAdded -= OnItemAdded;
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs args)
    {
        if (Plugin.Instance?.Configuration.Enabled != true || args.Item.Id == Guid.Empty)
        {
            return;
        }

        var itemType = args.Item switch
        {
            Movie => "Movie",
            Episode => "Episode",
            _ => null,
        };
        if (itemType is not null && !_events.Writer.TryWrite(new(args.Item.Id, itemType)))
        {
            logger.LogWarning("FinityStream webhook queue is full");
        }
    }

    private async Task SendAsync(
        HttpClient http, Uri webhookUrl, string secret, LibraryEvent item, CancellationToken cancellationToken)
    {
        var payload = new
        {
            ServerId = host.SystemId,
            ItemId = item.Id.ToString("N"),
            NotificationType = "ItemAdded",
            ItemType = item.Type,
        };

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Plugin.Instance?.Configuration is not { Enabled: true } current
                || current.WebhookSecret != secret)
            {
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
                {
                    Content = JsonContent.Create(payload, options: new System.Text.Json.JsonSerializerOptions()),
                };
                request.Headers.Add("X-Webhook-Secret", secret);
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    return;
                }

                var status = (int)response.StatusCode;
                if (status is not (408 or 429) && status < 500)
                {
                    logger.LogWarning("FinityStream webhook rejected event with HTTP {Status}", status);
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // A retry uses the same event ID so the relay can deduplicate it.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient timed out.
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), cancellationToken);
            }
        }

        logger.LogWarning("FinityStream webhook failed after four attempts for item {ItemId}", item.Id);
    }

    private static bool IsValidSecret(string value) =>
        value.Length is >= 32 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private sealed record LibraryEvent(Guid Id, string Type);
}
