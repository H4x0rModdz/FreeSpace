using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpace.Api;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Auth;
using FreeSpace.Api.StorageAccounts;
using FreeSpace.Api.Tenants;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using FreeSpace.Infrastructure.Storage;
using FreeSpace.Infrastructure.Storage.Google;
using FreeSpace.Infrastructure.Storage.S3;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddOptions<JwtOptions>().BindConfiguration("Jwt").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<AuthOptions>().BindConfiguration("Auth").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<RateLimitOptions>().BindConfiguration("RateLimiting").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<DatabaseOptions>().BindConfiguration("Database");
services.AddOptions<AppOptions>().BindConfiguration("App");
services.AddOptions<StorageOptions>().BindConfiguration("Storage").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<EncryptionOptions>().BindConfiguration("Encryption").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<GoogleOptions>().BindConfiguration("Google");
services.AddOptions<S3Options>().BindConfiguration("Storage:S3");

services.AddSingleton(TimeProvider.System);
services.AddHttpContextAccessor();
services.AddScoped<CurrentUser>();
services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<CurrentUser>());

services.AddDbContext<AppDbContext>((sp, options) =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Connection string 'Postgres' is not configured.");
    PersistenceSetup.Configure(options, connectionString);
});

services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
services.AddSingleton<TokenService>();
services.AddScoped<SessionIssuer>();
services.AddScoped<AuditLog>();

services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
services.AddSingleton<IGoogleApi, GoogleApi>();
services.AddSingleton<GoogleDriveStorageProvider>();
services.AddSingleton<S3StorageProvider>();
services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<GoogleDriveStorageProvider>());
services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<S3StorageProvider>());
services.AddSingleton<StorageProviderRegistry>();
services.AddScoped<StorageAccountService>();
services.AddHostedService<QuotaSyncWorker>();

services.AddFreeSpaceAuth();
services.AddFreeSpaceRateLimiting();
services.AddFreeSpaceForwardedHeaders(builder.Configuration);
services.AddProblemDetails();
services.AddValidation();
services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
services.AddOpenApi();

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapTenantEndpoints();
api.MapInvitationEndpoints();
api.MapAuditEndpoints();
api.MapStorageAccountEndpoints();

app.Run();

public partial class Program;
