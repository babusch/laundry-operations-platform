using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Laundry.Edge.Station;

public static class StationApplicationHosting
{
    public static void UseStationApplication(this WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = SetCachePolicy });
        app.UseRouting();
        app.Use(ServeApplicationShellWhenNoEndpointAsync);
    }

    private static void SetCachePolicy(StaticFileResponseContext context)
    {
        SetCachePolicy(context.Context.Response, context.Context.Request.Path);
    }

    private static async Task ServeApplicationShellWhenNoEndpointAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint() is not null ||
            !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) ||
            context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var shell = environment.WebRootFileProvider.GetFileInfo("index.html");
        if (!shell.Exists)
        {
            await next(context);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        SetCachePolicy(context.Response, context.Request.Path);
        await context.Response.SendFileAsync(shell, context.RequestAborted);
    }

    private static void SetCachePolicy(HttpResponse response, PathString requestPath)
    {
        response.GetTypedHeaders().CacheControl =
            requestPath.StartsWithSegments("/assets")
                ? new CacheControlHeaderValue
                {
                    Public = true,
                    MaxAge = TimeSpan.FromDays(365),
                    Extensions = { new NameValueHeaderValue("immutable") }
                }
                : new CacheControlHeaderValue { NoCache = true };
    }
}
