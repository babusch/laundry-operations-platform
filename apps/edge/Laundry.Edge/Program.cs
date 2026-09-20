using Laundry.Edge.Health;
using Laundry.Edge.Persistence;
using Laundry.Edge.Scans;
using Laundry.Edge.Security;
using Laundry.Edge.Synchronization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SubmissionValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WorkerDiagnostics>();
builder.Services.AddHttpClient("GatewayIdentity").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddSingleton<IGatewayTokenProvider>(services => new GatewayTokenProvider(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("GatewayIdentity"),
    services.GetRequiredService<IConfiguration>(), services.GetRequiredService<TimeProvider>()));
builder.Services.AddHttpClient<CloudDelivery>().ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddScoped<ISourceIdentityResolver, SourceIdentityResolver>();
builder.Services.AddScoped<BrowserCookieSourceAuthenticator>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = SourceEnrollmentEndpoints.AntiforgeryHeaderName;
    options.Cookie.Name = SourceEnrollmentEndpoints.AntiforgeryCookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.Path = "/";
});

builder.Services.AddDbContext<PlantDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Plant")));
builder.Services.AddHealthChecks()
    .AddCheck<PlantDatabaseHealthCheck>("plant-postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("ScanAcceptance:Enabled"))
{
    var source = app.Configuration.GetSection("ScanAcceptance").Get<DevelopmentSource>()
        ?? throw new InvalidOperationException("Configure the development scan source.");
    if (new[] { source.TenantId, source.PlantId, source.StationId }.Contains(Guid.Empty))
        throw new InvalidOperationException("Configure nonempty development tenant, plant, and station IDs.");
    app.MapScanAcceptance();
    if (app.Configuration.GetValue<bool>("SyncDiagnostics:Enabled")) app.MapSyncDiagnostics(source);
    if (app.Configuration.GetValue<bool>("SourceEnrollment:Enabled"))
        app.MapSourceEnrollment(new SourceScope(source.TenantId, source.PlantId), source.StationId);
}

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// Separate entry-point type from the cloud API for integration testing.
public partial class Program;
