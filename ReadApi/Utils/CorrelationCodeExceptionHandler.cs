using Microsoft.AspNetCore.Diagnostics;

using NanoidDotNet;

using System.Net.Mime;

namespace TimescaleOpenTsdbAdapter.Utils;

// Stub class, exists only to be a type parameter for ILogger
public class CorrelationCodeExceptionHandler {}

public static class CorrelationCodeExceptionHandlerExtensions
{
    public static WebApplication UseCorrelationCodeExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler(builder => {
            builder.Run(async (HttpContext ctx) => {
                var correlationCode = Nanoid.Generate();

                var logger = ctx.RequestServices.GetRequiredService<ILogger<CorrelationCodeExceptionHandler>>();
                var exception = ctx.Features.Get<IExceptionHandlerPathFeature>()!.Error!;

                logger.LogError(
                    exception,
                    "Unhandled exception in request handler for user {user} requesting {method} on {path}. Correlation code \"{correlationCode}\".",
                    ctx.User.Identity?.Name ?? "<unauthenticated>",
                    ctx.Request.Method,
                    $"{ctx.Request.Path}{ctx.Request.QueryString}",
                    correlationCode);

                // Random wait helps prevent timing attacks
                await Task.Delay(TimeSpan.FromSeconds(0.5f + Random.Shared.NextDouble()));

                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                ctx.Response.ContentType = MediaTypeNames.Text.Plain;

                await ctx.Response.WriteAsync($"An unexpected error occurred. If the issue persists, contact a developer and cite correlation code \"{correlationCode}\".");
            });
        });
        return app;
    }
}
