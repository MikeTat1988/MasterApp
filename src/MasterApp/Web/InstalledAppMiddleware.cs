using MasterApp.Diagnostics;
using MasterApp.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace MasterApp.Web;

public sealed class InstalledAppMiddleware
{
    private readonly RequestDelegate _next;
    private readonly MasterAppRuntime _runtime;
    private readonly FileExtensionContentTypeProvider _contentTypes = new();

    public InstalledAppMiddleware(RequestDelegate next, MasterAppRuntime runtime)
    {
        _next = next;
        _runtime = runtime;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_runtime.TryResolveInstalledAppRequest(context.Request.Path, out var filePath, out var reason))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(reason ?? "Installed app file not found.");
            return;
        }

        if (!_contentTypes.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        context.Response.ContentType = contentType;
        await context.Response.SendFileAsync(filePath);
    }
}
