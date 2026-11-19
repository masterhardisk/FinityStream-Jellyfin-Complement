using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FinityStream;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
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
            : Ok(new { installationID = id, relayURL = LibraryNotifications.ServiceUrl });
    }

    [HttpPut("Installation/{installationId:guid}/Devices")]
    public async Task<IActionResult> RegisterDevice(
        Guid installationId,
        DeviceRegistration device,
        [FromHeader(Name = "X-Device-Secret"), Required, RegularExpression(@"^[A-Za-z0-9_-]{32,128}$")] string ownerSecret,
        CancellationToken cancellationToken)
    {
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
            Content = JsonContent.Create(new { token = device.Token }),
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

    public sealed record DeviceRegistration(
        [Required, RegularExpression(@"^(?:[0-9a-fA-F]{2}){16,100}$")] string Token);
}
