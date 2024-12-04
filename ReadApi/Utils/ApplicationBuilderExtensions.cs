using CompressedStaticFiles;

using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

using System.Globalization;
using System.Text.RegularExpressions;

namespace TimescaleOpenTsdbAdapter.Utils;

public static partial class ApplicationBuilderExtensions
{
    private static readonly Regex contentHashRegex = ContentHashRegex();

    // Vite's content hashes are a suffix of the form "-ABCabc01" before the file extension
    // Note that the filenames we test this against will have the .br and .gz suffixes if serving compressed versions
    [GeneratedRegex(@"-[a-z\d]{8}\.", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ContentHashRegex();

    // Generated with https://addons.mozilla.org/en-US/firefox/addon/laboratory-by-mozilla/
    private static readonly string DefaultContentSecurityPolicy =
        "default-src 'self'; " +
        $"connect-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; " +
        "font-src 'self' data:";

    public static void UseCompressedStaticFiles(this IApplicationBuilder app, string subpath, string location) {
        var fileProvider = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), location));
        app.UseDefaultFiles(new DefaultFilesOptions // Maps, .e.g. /blarg to ./blarg/index.html
        {
            FileProvider = fileProvider,
            RequestPath = subpath
        });

        var contentSecurityPolicy = Environment.GetEnvironmentVariable("CONTENT_SECURITY_POLICY") ?? DefaultContentSecurityPolicy;

        app.UseCompressedStaticFiles(new StaticFileOptions
        {
            RequestPath = subpath,
            FileProvider = fileProvider,
            OnPrepareResponse = ctx =>
            {
                var hasContentHash = contentHashRegex.IsMatch(ctx.File.Name);
                ctx.Context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
                {
                    Private = true,
                    MaxAge = TimeSpan.FromDays(30),
                    // Cache entries that do not have a content hash must be validated before use
                    NoCache = !hasContentHash
                };
                if(ctx.File.Name.StartsWith("index.html", ignoreCase: true, CultureInfo.InvariantCulture))
                {
                    var responseHeaders = ctx.Context.Response.Headers;

                    responseHeaders.ContentSecurityPolicy = contentSecurityPolicy;

                    // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/X-Frame-Options
                    // Note this header was made obsolete by the Content-Security-Policy and is only
                    // used by very old browsers, but we put it here anyway as a best practice
                    responseHeaders.XFrameOptions = "SAMEORIGIN";

                    // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/X-Content-Type-Options
                    // This header tells very old browsers not to attempt to detect the MIME type of
                    // responses that don't include a Content-Type header, which can cause security issues.
                    // Good browsers don't do MIME type sniffing by default, but we put the header here
                    // anyway as a best practice
                    responseHeaders.XContentTypeOptions = "nosniff";

                    // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Cross-Origin-Resource-Policy
                    // Instructs the browser to block "no-cors" mode requests entirely
                    responseHeaders["Cross-Origin-Resource-Policy"] = "same-origin";

                    // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Cross-Origin-Embedder-Policy
                    // Similar to the CORP header above, but for resources like <img/> elements
                    responseHeaders["Cross-Origin-Embedder-Policy"] = "require-corp";

                    // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Cross-Origin-Opener-Policy
                    // Prevents leaking info via JS if someone opens this from another domain in a popup
                    responseHeaders["Cross-Origin-Opener-Policy"] = "same-origin";
                }
            }
        });
    }
}
