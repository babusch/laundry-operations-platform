using Laundry.Api.Integrations.Scans;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

const string managementBootstrapFlag = "--initialize-development-management-access";
const string customerBootstrapFlag = "--initialize-development-customer";
const string publicationBootstrapFlag = "--initialize-development-order-publications";
var initializeManagement = args.Contains(managementBootstrapFlag, StringComparer.Ordinal);
var initializeCustomer = args.Contains(customerBootstrapFlag, StringComparer.Ordinal);
var initializePublications = args.Contains(publicationBootstrapFlag, StringComparer.Ordinal);
if ((initializeManagement ? 1 : 0) + (initializeCustomer ? 1 : 0) + (initializePublications ? 1 : 0) > 1 ||
    args.Count(value => value == publicationBootstrapFlag) > 1)
    throw new InvalidOperationException("Choose one Development setup command.");
var builder = WebApplication.CreateBuilder(args.Where(value => value != managementBootstrapFlag && value != customerBootstrapFlag && value != publicationBootstrapFlag).ToArray());

if (initializePublications)
{
    Environment.ExitCode = await DevelopmentInitialOrderPublication.ExecuteCommandAsync(builder.Environment, builder.Configuration);
    return;
}

if (initializeCustomer)
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Customer bootstrap is Development-only.");
    var options = new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(
        builder.Configuration.GetConnectionString("Laundry"),
        provider => provider.MigrationsHistoryTable("__customer_migrations", "customers")).Options;
    await using var database = new CustomerDbContext(options);
    if ((await database.Database.GetPendingMigrationsAsync()).Any())
        throw new InvalidOperationException("Apply the customer migration explicitly before setup.");
    var created = await new CustomerStore(database).InitializeDevelopmentAsync(
        ManagementAccessStore.DevelopmentTenant, ManagementAccessStore.DevelopmentPlant);
    Console.WriteLine(created ? "Development customer and plant-service eligibility created with audit history." :
        "Development customer already exists; no changes made.");
    return;
}

if (initializeManagement)
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Membership bootstrap is Development-only.");
    var subject = builder.Configuration["ManagementBootstrap:Subject"]
        ?? throw new InvalidOperationException("Supply the development demo user's Keycloak ID.");
    var options = new DbContextOptionsBuilder<ManagementAccessDbContext>().UseNpgsql(
        builder.Configuration.GetConnectionString("Laundry"),
        provider => provider.MigrationsHistoryTable("__management_access_migrations", "identity_access")).Options;
    await using var database = new ManagementAccessDbContext(options);
    if ((await database.Database.GetPendingMigrationsAsync()).Any())
        throw new InvalidOperationException("Apply the management access migration explicitly before provisioning.");
    var created = await new ManagementAccessStore(database).InitializeDevelopmentAsync(subject);
    Console.WriteLine(created ? "Development management assignment created with audit history." :
        "Development management assignment already exists; no changes made.");
    return;
}

builder.Services.AddSingleton<ScanContractValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddGatewayAuthentication(builder.Configuration);
builder.Services.AddDevelopmentManagementAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddDevelopmentManagementAccess(builder.Configuration, builder.Environment);
builder.Services.AddDevelopmentCustomers(builder.Configuration, builder.Environment);
builder.Services.AddDevelopmentOrderPlanning(builder.Configuration, builder.Environment);
builder.Services.AddDataProtection();
builder.Services.AddDbContext<ScanDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Laundry")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:Laundry before using the database.")));
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

app.UseOrderPlanDownloadBoundary();
app.UseDevelopmentManagementBoundary();
app.UseDevelopmentOrderBoundary();
app.UseAuthentication();
app.UseAuthorization();
app.MapDevelopmentManagementSession();
app.MapDevelopmentManagementAccess();
app.MapDevelopmentCustomers();
app.MapDevelopmentOrderPlanning();
app.MapOrderPlanDownload();
Laundry.Api.Management.DevelopmentManagementPages.MapDevelopmentManagementPages(app);

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("ScanIngestion:Enabled"))
{
    app.MapScanIngestion();
}

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// WebApplicationFactory uses this entry point in integration tests.
public partial class Program;
