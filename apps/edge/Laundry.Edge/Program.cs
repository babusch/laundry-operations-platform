using Laundry.Edge.Health;
using Laundry.Edge.Orders;
using Laundry.Edge.Persistence;
using Laundry.Edge.Scans;
using Laundry.Edge.Security;
using Laundry.Edge.Station;
using Laundry.Edge.Textiles;
using Laundry.Edge.Synchronization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var downloadOnce = args.Contains("--download-orders-once", StringComparer.Ordinal);
var verifyOrderAccess = args.Contains("--verify-local-order-access", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(x => x is not ("--download-orders-once" or "--verify-local-order-access")).ToArray());
if (verifyOrderAccess)
{
    try
    {
        if (downloadOnce) throw new InvalidOperationException();
        DevelopmentOrderAccessCommand.Configure(builder);
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Local order access verification requires the fixed Development installation and cannot combine commands.");
        Environment.ExitCode = 1;
        return;
    }
}
if (downloadOnce)
{
    Environment.ExitCode = await DevelopmentOrderDownloadCommand.RunAsync(builder.Environment, builder.Configuration);
    return;
}
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
builder.Services.AddScoped<LocalOrderList>();
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

builder.Services.AddDevelopmentOperatorIdentity(builder.Configuration, builder.Environment);
var app = builder.Build();

app.UseDevelopmentOperatorBoundary();
if (OperatorAuthentication.Enabled(app.Configuration, app.Environment)) app.UseAuthentication();
app.MapDevelopmentOperatorIdentity();

app.UseStationApplication();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("ScanAcceptance:Enabled"))
{
    var source = app.Configuration.GetSection("ScanAcceptance").Get<DevelopmentSource>()
        ?? throw new InvalidOperationException("Configure the development scan source.");
    if (new[] { source.TenantId, source.PlantId, source.StationId }.Contains(Guid.Empty))
        throw new InvalidOperationException("Configure nonempty development tenant, plant, and station IDs.");
    app.MapScanAcceptance();
    if (app.Configuration.GetValue<bool>("DevelopmentItemLookup:Enabled")) app.MapDevelopmentItemLookup();
    if (app.Configuration.GetValue<bool>("DevelopmentScanPreview:Enabled") &&
        app.Configuration.GetValue<bool>("DevelopmentItemLookup:Enabled") && app.Configuration.GetValue<bool>("LocalOrderList:Enabled"))
        app.MapDevelopmentScanPreview();
    if (app.Configuration.GetValue<bool>("LocalOrderList:Enabled")) app.MapLocalOrderList();
    if (app.Configuration.GetValue<bool>("SyncDiagnostics:Enabled")) app.MapSyncDiagnostics(source);
    if (app.Configuration.GetValue<bool>("SourceEnrollment:Enabled"))
        app.MapSourceEnrollment(new SourceScope(source.TenantId, source.PlantId), source.StationId);
}

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

if (verifyOrderAccess)
{
    Environment.ExitCode = await DevelopmentOrderAccessCommand.RunAsync(app);
    return;
}
app.Run();

// Separate entry-point type from the cloud API for integration testing.
public partial class Program;
