using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Jellyfin.Plugin.FinityStream;

internal sealed class ArtworkChangeFilter(LibraryNotifications notifications) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerTypeInfo.FullName != "Jellyfin.Api.Controllers.ImageController"
            || !IsMutation(context.HttpContext.Request.Method)
            || !context.ActionArguments.TryGetValue("itemId", out var value)
            || value is not Guid itemId)
        {
            await next();
            return;
        }

        var executed = await next();
        if (!executed.Canceled && executed.Exception is null
            && executed.Result is IStatusCodeActionResult { StatusCode: >= 200 and < 300 })
        {
            notifications.EnqueueArtworkChange(itemId);
        }
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}
