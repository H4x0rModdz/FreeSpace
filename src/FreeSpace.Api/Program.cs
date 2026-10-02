using FreeSpace.Api.Configurations;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddFreeSpaceOptions()
    .AddFreeSpacePersistence()
    .AddFreeSpaceSecurity()
    .AddFreeSpaceRateLimiting()
    .AddFreeSpaceForwardedHeaders(builder.Configuration)
    .AddFreeSpaceServices()
    .AddFreeSpaceApi();

var app = builder.Build();

await app.ApplyMigrationsAsync();
app.UseFreeSpacePipeline();
app.MapFreeSpaceEndpoints();

app.Run();

public partial class Program;
