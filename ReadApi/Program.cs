using TimescaleOpenTsdbAdapter.Controllers;
using TimescaleOpenTsdbAdapter.Database;
using TimescaleOpenTsdbAdapter.Dtos;
using TimescaleOpenTsdbAdapter.Services;
using TimescaleOpenTsdbAdapter.Utils;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TimescaleOpenTsdbAdapter;
using CompressedStaticFiles;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

static void ConfigureJson(JsonSerializerOptions options)
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    // Required for serialising "NaN" values for queries with fill policy = NaN
    options.NumberHandling |= JsonNumberHandling.AllowNamedFloatingPointLiterals;
}

// Configures for minimal API endpoints only. Controller endpoints are configured below
builder.Services.Configure<JsonOptions>(o => ConfigureJson(o.SerializerOptions));

builder.Services.AddCompressedStaticFiles(options => {
    options.EnableImageSubstitution = false;
});

builder.Services.AddDbContextFactory<MetricsContext>();

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(Settings.TimescaleConnectionString));

builder.Services.AddSingleton<TagsetCacheService>();
builder.Services.AddHostedService(p => p.GetRequiredService<TagsetCacheService>());

if(Settings.HousekeepingInterval.HasValue) builder.Services.AddHostedService<HousekeepingService>();

builder.Services.AddControllers()
    .AddJsonOptions(o => ConfigureJson(o.JsonSerializerOptions));

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(o =>
{
    o.MapType<DateTimeOffset>(() => new() { Type = "string" });
    o.MapType<AggregatorFunction>(() => new() { Type = "string" });
    o.MapType<Downsample>(() => new() { Type = "string" });
    o.MapType<FilterType>(() => new() { Type = "string" });
    o.MapType<List<(long, double)>>(() => new() { Type = "object" });
});

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

var app = builder.Build();

if(!builder.Environment.IsDevelopment()) {
    // In dev builds, we want the developer exception page (stack trace and other nice info right in the browser)
    app.UseCorrelationCodeExceptionHandler();

    app.UseHsts();
}

app.UseRouting();

app.UseSwagger();
app.UseSwaggerUI();

app.UseResponseCompression();

app.MapControllers();

app.MapGet("/api/health", (TagsetCacheService tagsetCacheService) => {
    var lastCacheUpdate = tagsetCacheService.LastSuccessfulUpdate;
    if (!lastCacheUpdate.HasValue)
        return Results.Text("Tagset cache has not initialised yet", statusCode: 503);

    var cacheAge = DateTimeOffset.UtcNow - lastCacheUpdate.Value;
    if (DateTimeOffset.UtcNow - lastCacheUpdate.Value > Settings.TagsetCacheUpdateInterval * 2)
        return Results.Text($"Tagset cache is stale (Last updated {cacheAge.TotalSeconds:f2}s ago)", statusCode: 503);

    return Results.Text("We cool");
}).AllowAnonymous();

static void UseStaticFiles(IApplicationBuilder app)
{
    // Cannot use "wwwroot" since .net 9 as it conflicts with Microsoft.NET.Sdk.StaticWebAssets
    app.UseCompressedStaticFiles(subpath: "", location: "frontend");
}

UseStaticFiles(app);

app.Use(async (ctx, next) =>
{
    if(ctx.GetEndpoint() is null) {
        // None of the assets/endpoints have matched, so we are probably
        // looking at a virtual frontend route
        // Route to index.html
        ctx.Request.Path = "/index.html";
    }
    await next();
});
// Need to run the static file middleware again to serve index.html in case above
UseStaticFiles(app);

app.Run();
