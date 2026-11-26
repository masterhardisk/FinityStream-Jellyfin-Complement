using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinityStream;

internal sealed class LibraryNotifications(
    ILibraryManager library,
    IServerApplicationHost host,
    ILogger<LibraryNotifications> logger) : BackgroundService
{
    internal static readonly Uri ServiceUrl = new("https://finitystream.gerardcontador.com/");
    private static readonly Uri WebhookUrl = new(ServiceUrl, "v1/webhooks/jellyfin");
    private static readonly Uri RegistrationUrl = new(ServiceUrl, "v1/installations");

    private readonly Channel<LibraryEvent> _events = Channel.CreateBounded<LibraryEvent>(
        new BoundedChannelOptions(1024) { SingleReader = true });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        library.ItemAdded += OnItemAdded;
        library.ItemUpdated += OnItemUpdated;
        library.ItemRemoved += OnItemRemoved;
        try
        {
            await Parallel.ForEachAsync(
                _events.Reader.ReadAllAsync(stoppingToken),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = stoppingToken },
                async (item, cancellationToken) => await ProcessEventAsync(http, item, cancellationToken));
        }
        finally
        {
            library.ItemAdded -= OnItemAdded;
            library.ItemUpdated -= OnItemUpdated;
            library.ItemRemoved -= OnItemRemoved;
        }
    }

    private async Task ProcessEventAsync(HttpClient http, LibraryEvent item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.Enabled != true)
        {
            return;
        }

        if (item.EventType == "ItemAdded" && item.Notify)
        {
            item = await WaitForPrimaryArtworkAsync(item, cancellationToken);
        }

        config = Plugin.Instance?.Configuration;
        if (config?.Enabled != true)
        {
            return;
        }

        var serverId = host.SystemId;
        if (!IsValidSecret(config.WebhookSecret) || config.EnrollmentSecret.Length > 0)
        {
            var installationId = await EnsureInstallationAsync(http, cancellationToken);
            if (installationId is null)
            {
                return;
            }

            serverId = installationId;
        }

        await SendAsync(http, config.WebhookSecret, serverId, item, cancellationToken);
    }

    private async Task<LibraryEvent> WaitForPrimaryArtworkAsync(
        LibraryEvent item, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = library.GetItemById(item.Id);
            BaseItem? artworkItem = current;
            if (current is Episode episode)
            {
                var seriesId = episode.SeriesId;
                var series = seriesId == Guid.Empty ? null : library.GetItemById(seriesId);
                item = item with
                {
                    Title = LimitTitle(episode.Name),
                    SeriesId = seriesId == Guid.Empty ? null : seriesId.ToString("N"),
                    SeriesTitle = LimitTitle(episode.SeriesName ?? series?.Name),
                    SeasonId = item.SeasonId is null && Plugin.Instance?.Configuration.GroupEpisodes == true
                        && episode.SeasonId != Guid.Empty ? episode.SeasonId.ToString("N") : item.SeasonId,
                };

                // Episode alerts use the series poster. Do not fall back to an episode still
                // while Jellyfin is still resolving the episode's series relationship.
                artworkItem = series;
            }
            else if (current is not null)
            {
                item = item with { Title = NotificationTitle(current) };
            }

            if (artworkItem?.HasImage(ImageType.Primary, 0) == true)
            {
                return item;
            }

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }

        logger.LogWarning(
            "No series or item primary artwork became available for Jellyfin item {ItemId} within ten minutes; sending its notification anyway",
            item.Id);
        return item;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs args) => Enqueue(args, "ItemAdded");

    private void OnItemUpdated(object? sender, ItemChangeEventArgs args) => Enqueue(args, "ItemUpdated");

    private void OnItemRemoved(object? sender, ItemChangeEventArgs args) => Enqueue(args, "ItemRemoved");

    private void Enqueue(ItemChangeEventArgs args, string eventType)
    {
        if (Plugin.Instance?.Configuration is not { Enabled: true } config || args.Item.Id == Guid.Empty)
        {
            return;
        }

        var itemType = args.Item switch
        {
            Movie => "Movie",
            Episode => "Episode",
            _ => args.Item.GetType().Name,
        };
        var notify = itemType switch
        {
            "Movie" => config.NotifyMovies,
            "Series" or "Episode" => config.NotifyEpisodes,
            _ => false,
        };
        var seasonId = config.GroupEpisodes && args.Item is Episode episode && episode.SeasonId != Guid.Empty
            ? episode.SeasonId.ToString("N")
            : null;

        var episodeInfo = args.Item as Episode;
        var seriesId = episodeInfo is not null && episodeInfo.SeriesId != Guid.Empty
            ? episodeInfo.SeriesId.ToString("N")
            : null;
        QueueEvent(new(args.Item.Id, itemType, eventType, Guid.NewGuid(), seasonId, notify,
            NotificationTitle(args.Item), seriesId, LimitTitle(episodeInfo?.SeriesName)));
    }

    internal void EnqueueArtworkChange(Guid itemId)
    {
        if (itemId != Guid.Empty && Plugin.Instance?.Configuration.Enabled == true)
        {
            QueueEvent(new(itemId, "Artwork", "LibraryChanged", Guid.NewGuid(), null, false));
        }
    }

    internal void EnqueueUserChange(Guid userId)
    {
        if (userId != Guid.Empty && Plugin.Instance?.Configuration.Enabled == true)
        {
            QueueEvent(new(userId, "User", "UserChanged", Guid.NewGuid(), null, false,
                UserId: userId.ToString("N")));
        }
    }

    private void QueueEvent(LibraryEvent item)
    {
        if (!_events.Writer.TryWrite(item))
        {
            logger.LogWarning("FinityStream webhook queue is full");
        }
    }

    private async Task SendAsync(
        HttpClient http, string secret, string serverId, LibraryEvent item, CancellationToken cancellationToken)
    {
        var payload = new
        {
            ServerId = serverId,
            ItemId = item.Id.ToString("N"),
            NotificationType = item.EventType,
            EventId = item.EventId.ToString("N"),
            ItemType = item.Type,
            item.SeasonId,
            item.Notify,
            item.Title,
            item.SeriesId,
            item.SeriesTitle,
            item.UserId,
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
                using var request = new HttpRequestMessage(HttpMethod.Post, WebhookUrl)
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

    private readonly SemaphoreSlim _registrationLock = new(1);

    internal async Task<string?> EnsureInstallationAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await _registrationLock.WaitAsync(cancellationToken);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin?.Configuration is not { Enabled: true } config)
            {
                return null;
            }
            if (!IsValidSecret(config.WebhookSecret))
            {
                config.WebhookSecret = GenerateSecret();
                config.EnrollmentSecret = GenerateSecret();
                plugin.SaveConfiguration();
            }
            return config.EnrollmentSecret.Length == 43
                ? await RegisterInstallationAsync(http, config.WebhookSecret, config.EnrollmentSecret, cancellationToken)
                : null;
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    private async Task<string?> RegisterInstallationAsync(
        HttpClient http, string webhookSecret, string enrollmentSecret,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RegistrationUrl);
            request.Headers.Add("X-Webhook-Secret", webhookSecret);
            request.Headers.Add("X-Enrollment-Secret", enrollmentSecret);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("FinityStream automatic registration failed with HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var installation = await response.Content.ReadFromJsonAsync<Installation>(cancellationToken);
            return Guid.TryParse(installation?.ServerId, out var id) ? id.ToString("N") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("FinityStream automatic registration request failed");
            return null;
        }
    }

    private static string GenerateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Installation([property: JsonPropertyName("server_id")] string ServerId);

    private static bool IsValidSecret(string value) =>
        value.Length is >= 32 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string? LimitTitle(string? value) =>
        value is { Length: > 150 } ? string.Concat(value.EnumerateRunes().Take(150).Select(rune => rune.ToString())) : value;

    private static string? NotificationTitle(BaseItem item)
    {
        var title = LimitTitle(item.Name);
        if (item is Movie or Series && item.ProductionYear is > 0 && !string.IsNullOrWhiteSpace(title))
        {
            var suffix = $" ({item.ProductionYear})";
            return title.EndsWith(suffix, StringComparison.Ordinal) ? title : title + suffix;
        }

        return title;
    }

    private sealed record LibraryEvent(
        Guid Id, string Type, string EventType, Guid EventId, string? SeasonId, bool Notify,
        string? Title = null, string? SeriesId = null, string? SeriesTitle = null, string? UserId = null);
}
