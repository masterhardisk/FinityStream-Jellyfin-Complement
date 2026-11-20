using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FinityStream;

[ApiController]
[Authorize]
[Route("FinityStream/Notifications")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DeviceEnrollmentController : ControllerBase
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    [HttpGet("Installation")]
    public async Task<IActionResult> GetInstallation(CancellationToken cancellationToken)
    {
        var service = HttpContext.RequestServices.GetRequiredService<LibraryNotifications>();
        var id = await service.EnsureInstallationAsync(Http, cancellationToken);
        return id is null
            ? StatusCode(503)
            : Ok(new { installationID = Guid.ParseExact(id, "N"), relayURL = LibraryNotifications.ServiceUrl });
    }

    [HttpPut("Installation/{installationId:guid}/Devices")]
    public async Task<IActionResult> RegisterDevice(
        Guid installationId,
        DeviceRegistration device,
        [FromHeader(Name = "X-Device-Secret"), Required, RegularExpression(@"^[A-Za-z0-9_-]{32,128}$")] string ownerSecret,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty)
        {
            return Forbid();
        }

        var service = HttpContext.RequestServices.GetRequiredService<LibraryNotifications>();
        var id = await service.EnsureInstallationAsync(Http, cancellationToken);
        if (id != installationId.ToString("N")
            || Plugin.Instance?.Configuration is not { Enabled: true } config)
        {
            return StatusCode(503);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri(LibraryNotifications.ServiceUrl, $"v1/servers/{id}/devices"))
        {
            Content = JsonContent.Create(new { token = device.Token, silent_only = device.SilentOnly, user_id = userId }),
        };
        request.Headers.Add("X-Device-Secret", ownerSecret);
        request.Headers.Add("X-Enrollment-Secret", config.EnrollmentSecret);
        try
        {
            using var response = await Http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? NoContent() : StatusCode(502);
        }
        catch (HttpRequestException)
        {
            return StatusCode(502);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504);
        }
    }

    [AllowAnonymous]
    [HttpGet("SignIn")]
    public IActionResult SignInAvailability() =>
        Plugin.Instance?.Configuration is { Enabled: true } ? Ok(true) : NotFound();

    [AllowAnonymous]
    [HttpPost("SignIn")]
    public async Task<IActionResult> RequestSignIn(
        SignInRequest input,
        [FromServices] IQuickConnect quickConnect,
        [FromServices] IUserManager users,
        CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is not { Enabled: true } config)
        {
            return NotFound();
        }

        MediaBrowser.Model.QuickConnect.QuickConnectResult pending;
        try
        {
            pending = quickConnect.CheckRequestStatus(input.Secret);
        }
        catch (ResourceNotFoundException)
        {
            return NotFound();
        }

        if (pending.Authenticated || pending.DateAdded < DateTime.UtcNow.AddMinutes(-2))
        {
            return BadRequest();
        }

        var user = users.GetUserByName(input.Username);
        if (user is null)
        {
            return Accepted(true);
        }

        var service = HttpContext.RequestServices.GetRequiredService<LibraryNotifications>();
        var id = await service.EnsureInstallationAsync(Http, cancellationToken);
        if (id is null)
        {
            return StatusCode(503);
        }

        // The relay needs a stable deduplication identifier, never the Quick Connect secret.
        var requestId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(input.Secret)).AsSpan(0, 16));
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(LibraryNotifications.ServiceUrl, $"v1/servers/{id}/sign-in"))
        {
            Content = JsonContent.Create(new { request_id = requestId, user_id = user.Id, code = pending.Code }),
        };
        request.Headers.Add("X-Enrollment-Secret", config.EnrollmentSecret);
        try
        {
            using var response = await Http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? Accepted(true) : StatusCode((int)response.StatusCode == 429 ? 429 : 502);
        }
        catch (HttpRequestException)
        {
            return StatusCode(502);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504);
        }
    }

    public sealed record SignInRequest(
        [Required, StringLength(128, MinimumLength = 16)] string Secret,
        [Required, StringLength(128)] string Username);

    public sealed record DeviceRegistration(
        [Required, RegularExpression(@"^(?:[0-9a-fA-F]{2}){16,100}$")] string Token,
        [property: System.Text.Json.Serialization.JsonPropertyName("silent_only")] bool? SilentOnly);
}
