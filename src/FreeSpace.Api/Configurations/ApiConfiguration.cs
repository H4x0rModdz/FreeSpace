using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;

namespace FreeSpace.Api.Configurations;

/// <summary>MVC controllers, JSON, error format, API docs (OpenAPI + Scalar) and the HTTP pipeline.</summary>
internal static class ApiConfiguration
{
    public static IServiceCollection AddFreeSpaceApi(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddControllers()
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
            .ConfigureApiBehaviorOptions(options =>
            {
                // Keep the "code" contract on automatic model-validation errors too.
                var defaultFactory = options.InvalidModelStateResponseFactory;
                options.InvalidModelStateResponseFactory = context =>
                {
                    var result = defaultFactory(context);
                    if (result is ObjectResult { Value: ProblemDetails problem }) problem.Extensions["code"] = "validation_failed";
                    return result;
                };
            });

        services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

        // Only the configured web app may call the API from a browser. Auth uses bearer tokens, not cookies.
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<Microsoft.Extensions.Options.IOptions<AppOptions>>((cors, app) =>
            {
                if (Uri.TryCreate(app.Value.FrontendUrl, UriKind.Absolute, out var frontend))
                    cors.AddDefaultPolicy(policy => policy
                        .WithOrigins(frontend.GetLeftPart(UriPartial.Authority))
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .SetPreflightMaxAge(TimeSpan.FromHours(1)));
            });
        return services;
    }

    public static WebApplication UseFreeSpacePipeline(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        return app;
    }

    public static WebApplication MapFreeSpaceEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

        // API reference at /scalar (OpenAPI JSON at /openapi/v1.json). On in Development, opt-in elsewhere.
        if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("ApiDocs:Enabled"))
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference(options => options.WithTitle("FreeSpace API")).AllowAnonymous();
        }

        app.MapControllers();
        return app;
    }
}
