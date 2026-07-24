using MasterApp.Hosting;
using Microsoft.AspNetCore.Http;

namespace MasterApp.Web;

public sealed class RemoteAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly MasterAppRuntime _runtime;

    public RemoteAccessMiddleware(RequestDelegate next, MasterAppRuntime runtime)
    {
        _next = next;
        _runtime = runtime;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_runtime.IsLoopbackRequest(context))
        {
            await _next(context);
            return;
        }

        if (!_runtime.IsRemoteClientAllowed(context, out var reason))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(reason);
            return;
        }

        if (_runtime.IsAnonymousRemotePath(context.Request.Path) || _runtime.HasRemoteAccessSession(context))
        {
            await _next(context);
            return;
        }

        if ((context.Request.Path.Value ?? string.Empty).StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("""{"ok":false,"message":"QR session required for remote access."}""");
            return;
        }

        context.Response.Redirect("/phone/scan");
    }
}
