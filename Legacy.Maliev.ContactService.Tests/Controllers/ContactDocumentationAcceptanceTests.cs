using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

/// <summary>Consumes actual service documentation over HTTP, without mocking document generation.</summary>
public sealed class ContactDocumentationAcceptanceTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", false)]
    public async Task Documentation_RespectsEnvironmentBoundary(string environment, bool exposed)
    {
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var factory = parent.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = Client(factory);
        using var response = await client.GetAsync("/messages/openapi/v1.json");
        Assert.Equal(exposed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        if (!exposed) return;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Legacy MALIEV ContactRequest Service API", body.RootElement.GetProperty("info").GetProperty("title").GetString());
        using var ui = await client.GetAsync("/messages/scalar");
        Assert.Equal(HttpStatusCode.Found, ui.StatusCode);
        using var page = await client.GetAsync(new Uri(ui.RequestMessage!.RequestUri!, ui.Headers.Location!));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("messages/openapi/v1.json", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documentation_DescribesActualBearerAuthorization()
    {
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var factory = parent.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(factory);
        using var response = await client.GetAsync("/messages/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemes = body.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.Contains(schemes.EnumerateObject(), scheme =>
            scheme.Value.TryGetProperty("type", out var type) && type.GetString() == "http" &&
            scheme.Value.TryGetProperty("scheme", out var bearer) && bearer.GetString() == "bearer");
        foreach (var path in body.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!IsContactPath(path.Name)) continue;
            foreach (var operation in path.Value.EnumerateObject().Where(item => item.Name is "get" or "post" or "put" or "delete"))
            {
                Assert.True(operation.Value.TryGetProperty("security", out var requirement) && requirement.GetArrayLength() > 0,
                    $"Protected service operation {path.Name} {operation.Name} must advertise its authentication requirement: {operation.Value}");
            }
        }
    }

    [Fact]
    public async Task Documentation_ConsumesMaintainedOperationSummaries()
    {
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var factory = parent.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(factory);
        using var response = await client.GetAsync("/messages/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operationCount = 0;
        foreach (var path in body.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!IsContactPath(path.Name)) continue;
            foreach (var operation in path.Value.EnumerateObject().Where(item => item.Name is "get" or "post" or "put" or "delete"))
            {
                operationCount++;
                Assert.True(operation.Value.TryGetProperty("summary", out var summary) && !string.IsNullOrWhiteSpace(summary.GetString()),
                    $"Operation {path.Name} {operation.Name} must consume its maintained description: {operation.Value}");
            }
        }
        Assert.Equal(10, operationCount);
    }

    private static bool IsContactPath(string path) => path.Equals("/Messages", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/Messages/{messageId}", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/contact-requests", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Documentation_ExplainsQueriesAndActualEmptyPageResponse()
    {
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var factory = parent.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(factory);
        using var body = JsonDocument.Parse(await client.GetStringAsync("/messages/openapi/v1.json"));
        foreach (var path in body.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!IsContactPath(path.Name) || path.Name.Contains('{')) continue;
            var operation = path.Value.GetProperty("get");
            foreach (var name in new[] { "sort", "search", "index", "size" })
            {
                var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == name);
                Assert.True(parameter.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()));
            }
            Assert.Equal("No messages exist on the selected page.", operation.GetProperty("responses").GetProperty("404").GetProperty("description").GetString());
        }
    }

    [Fact]
    public async Task Documentation_ExplainsCreatePayloadWithoutIdentityOrStorageSecrets()
    {
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var factory = parent.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(factory);
        using var body = JsonDocument.Parse(await client.GetStringAsync("/messages/openapi/v1.json"));
        var operation = body.RootElement.GetProperty("paths").GetProperty("/Messages").GetProperty("post");
        Assert.True(operation.GetProperty("requestBody").TryGetProperty("description", out var description));
        Assert.Contains("contact details", description.GetString(), StringComparison.Ordinal);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });
}
