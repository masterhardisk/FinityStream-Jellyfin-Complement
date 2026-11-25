using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.FinityStream;

// Runs after the response is executed, so failed mutations never trigger a refresh.
internal sealed class UserChangeFilter(LibraryNotifications notifications) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var executed = await next();
        var request = context.HttpContext.Request;
        if (executed.Canceled || executed.Exception is not null
            || context.HttpContext.Response.StatusCode is < 200 or >= 300
            || !(HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
                || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method))
            || request.Path.StartsWithSegments("/FinityStream/Notifications")
            || context.HttpContext.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value, out var userId))
        {
            return;
        }

        notifications.EnqueueUserChange(userId);
    }
}
