using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpace.Api.Auth;

namespace FreeSpace.Tests.Infrastructure;

public static class ApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public const string Password = "correct-horse-battery";

    public static string UniqueEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    public static async Task<TokenPair> RegisterAsync(this HttpClient client, string? email = null, string name = "Test User")
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { name, email = email ?? UniqueEmail(), password = Password }, Json);
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TokenPair>(Json))!;
    }

    public static HttpClient WithToken(this HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static async Task<T> GetJsonAsync<T>(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await response.EnsureStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Json);

    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient client, string url, object body) =>
        client.PatchAsJsonAsync(url, body, Json);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    public static async Task EnsureStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            Assert.Fail($"Expected {(int)expected} {expected} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Returns the ProblemDetails <c>code</c> extension.</summary>
    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
