using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpace.Api;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Tenants;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddOptions<JwtOptions>().BindConfiguration("Jwt").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<AuthOptions>().BindConfiguration("Auth").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<RateLimitOptions>().BindConfiguration("RateLimiting").ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<DatabaseOptions>().BindConfiguration("Database");

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

app.Run();

public partial class Program;
