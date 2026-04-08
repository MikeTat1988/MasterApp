using MasterApp.Hosting;
using Microsoft.AspNetCore.Http;

namespace MasterApp.Web;

public sealed class HostedAppMiddleware
{
    private readonly RequestDelegate _next;
    private readonly MasterAppRuntime _runtime;

    public HostedAppMiddleware(RequestDelegate next, MasterAppRuntime runtime)
    {
        _next = next;
        _runtime = runtime;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/apps", out var remainder))
        {
            await _next(context);
            return;
        }

        var trimmed = remainder.Value?.Trim('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            context.Response.Redirect("/store.html");
            return;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var appId = parts[0];
        var relativePath = parts.Length > 1 ? string.Join('/', parts.Skip(1)) : string.Empty;

        if (await _runtime.TryHandleHostedAppRequestAsync(context, appId, relativePath))
        {
            return;
        }

        await _next(context);
    }
}
