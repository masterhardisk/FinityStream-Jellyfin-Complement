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
using Microsoft.Extensions.Caching.Memory;

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

    private static readonly MemoryCache SignIns = new(new MemoryCacheOptions { SizeLimit = 2048 });
    private static readonly object SignInCacheLock = new();

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

        var requestKey = (user.Id, pending.Code);
        if (SignIns.TryGetValue(requestKey, out PendingSignIn? existing) && existing?.Secret != input.Secret)
        {
            return Conflict();
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
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode == 429 ? 429 : 502);
            }

            lock (SignInCacheLock)
            {
                if (!SignIns.TryGetValue(requestKey, out PendingSignIn? _))
                {
                    SignIns.Set(requestKey, new PendingSignIn(input.Secret), new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(3),
                        Size = 1,
                    });
                }
            }
            return Accepted(true);
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

    [HttpGet("SignIn/{code}")]
    public Task<IActionResult> GetSignInStatus(string code, CancellationToken cancellationToken) =>
        ResolveSignIn(code, approve: false, cancellationToken);

    [HttpPost("SignIn/{code}")]
    public Task<IActionResult> ApproveSignIn(string code, CancellationToken cancellationToken) =>
        ResolveSignIn(code, approve: true, cancellationToken);

    private async Task<IActionResult> ResolveSignIn(string code, bool approve, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty)
        {
            return Forbid();
        }

        if (!SignIns.TryGetValue((userId, code), out PendingSignIn? pending) || pending is null)
        {
            return new JsonResult("expired");
        }

        await pending.Gate.WaitAsync(cancellationToken);
        try
        {
            if (pending.Approved)
            {
                return new JsonResult("alreadyApproved");
            }

            if (Plugin.Instance?.Configuration is not { Enabled: true })
            {
                return new JsonResult("expired");
            }

            var quickConnect = HttpContext.RequestServices.GetRequiredService<IQuickConnect>();
            try
            {
                var state = quickConnect.CheckRequestStatus(pending.Secret);
                if (state.Authenticated)
                {
                    return new JsonResult("alreadyApproved");
                }
                if (state.Code != code || state.DateAdded < DateTime.UtcNow.AddMinutes(-2))
                {
                    return new JsonResult("expired");
                }
                if (!approve)
                {
                    return new JsonResult("pending");
                }
                if (await quickConnect.AuthorizeRequest(userId, code))
                {
                    pending.Approved = true;
                    return new JsonResult("approved");
                }
                return new JsonResult("expired");
            }
            catch (ResourceNotFoundException)
            {
                return new JsonResult("expired");
            }
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    private sealed class PendingSignIn(string secret)
    {
        public string Secret { get; } = secret;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Approved { get; set; }
    }

    public sealed record SignInRequest(
        [Required, StringLength(128, MinimumLength = 16)] string Secret,
        [Required, StringLength(128)] string Username);

    public sealed record DeviceRegistration(
        [Required, RegularExpression(@"^(?:[0-9a-fA-F]{2}){16,100}$")] string Token,
        [property: System.Text.Json.Serialization.JsonPropertyName("silent_only")] bool? SilentOnly);
}
