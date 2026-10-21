using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.ContactService.Api.Authorization;
using Legacy.Maliev.ContactService.Data;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

/// <summary>Real production RS256/HTTP/PG contracts using disposable synthetic data only.</summary>
/// <param name="postgres">The shared disposable PostgreSQL fixture used for production authentication, HTTP, and persistence assertions.</param>
public sealed class ContactRuntimeParityBoundaryTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    private const string PipelineAllowedOrigin = "https://contact-pipeline.example.test";

    /// <summary>Checks that production CORS preflight handles configured and blocked origins before protected mutation authorization.</summary>
    /// <param name="allowed">Whether the preflight Origin matches the configured allowed origin rather than the blocked test origin.</param>
    /// <returns>A task that completes after verifying that production CORS preflight handles configured and blocked origins before protected mutation authorization.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionPipeline_PreflightPrecedesProtectedMutationAuthorization(bool allowed)
    {
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var host = NewPipelineHost(parent);
        AssertProductionPipelinePolicy(host);
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/Messages/1");
        request.Headers.Add("Origin", allowed ? PipelineAllowedOrigin : "https://blocked.example.invalid");
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        if (allowed)
        {
            Assert.Equal(PipelineAllowedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            Assert.Contains("PUT", response.Headers.GetValues("Access-Control-Allow-Methods"));
            var headers = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
            Assert.Contains("authorization", headers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("content-type", headers, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
    }

    /// <summary>Checks that unauthorized anonymous mutations retain configured CORS headers and persist no message.</summary>
    /// <returns>A task that completes after verifying that unauthorized anonymous mutations retain configured CORS headers and persist no message.</returns>
    [Fact]
    public async Task ProductionPipeline_AnonymousMutationIncludesConfiguredCorsOnUnauthorizedResponse()
    {
        await postgres.ResetAsync();
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var host = NewPipelineHost(parent);
        AssertProductionPipelinePolicy(host);
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/Messages/1")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", PipelineAllowedOrigin);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PipelineAllowedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.Messages.CountAsync());
    }

    /// <summary>Checks anonymous healthy liveness and readiness responses with the approved sanitized wire shape.</summary>
    /// <param name="probe">The anonymous health endpoint suffix: liveness for plain Healthy text or readiness for sanitized JSON.</param>
    /// <returns>A task that completes after verifying anonymous healthy liveness and readiness responses with the approved sanitized wire shape.</returns>
    [Theory]
    [InlineData("liveness")]
    [InlineData("readiness")]
    public async Task ProductionPipeline_HealthyProbesRemainAnonymousWithSanitizedReadiness(string probe)
    {
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var host = NewPipelineHost(parent);
        AssertProductionPipelinePolicy(host);
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        using var response = await client.GetAsync("/messages/" + probe);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (probe == "liveness")
        {
            Assert.Equal("Healthy", body);
            return;
        }
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        AssertReadinessShape(json.RootElement, "Healthy");
        Assert.NotEmpty(json.RootElement.GetProperty("checks").EnumerateObject());
        Assert.All(json.RootElement.GetProperty("checks").EnumerateObject(), check =>
            Assert.Equal("Healthy", check.Value.GetProperty("status").GetString()));
    }

    /// <summary>Checks that dependency failures produce sanitized unhealthy readiness while liveness remains healthy.</summary>
    /// <param name="throwFailure">Whether the controlled readiness check throws rather than returning an unhealthy result with private diagnostics.</param>
    /// <returns>A task that completes after verifying that dependency failures produce sanitized unhealthy readiness while liveness remains healthy.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionPipeline_ControlledDependencyFailureReturnsSanitized503WithHealthyLiveness(bool throwFailure)
    {
        const string privateMarker = "private-contact-readiness-marker";
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var host = NewPipelineHost(parent, services => services.AddHealthChecks().AddCheck("controlled-readiness", () =>
        {
            if (throwFailure) throw new InvalidOperationException(privateMarker);
            return HealthCheckResult.Unhealthy(privateMarker, new InvalidOperationException(privateMarker),
                new Dictionary<string, object> { ["private-diagnostic"] = privateMarker });
        }));
        AssertProductionPipelinePolicy(host);
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        using var response = await client.GetAsync("/messages/readiness");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(privateMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-diagnostic", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        AssertReadinessShape(json.RootElement, "Unhealthy");
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("checks").GetProperty("controlled-readiness").GetProperty("status").GetString());
        using var liveness = await client.GetAsync("/messages/liveness");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal("Healthy", await liveness.Content.ReadAsStringAsync());
    }

    private static WebApplicationFactory<Program> NewPipelineHost(ContactRuntimeFactory parent, Action<IServiceCollection>? configureServices = null) =>
        parent.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CORS:AllowedOrigins:0", PipelineAllowedOrigin);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CORS:AllowedOrigins:0"] = PipelineAllowedOrigin }));
            builder.ConfigureTestServices(services => configureServices?.Invoke(services));
        });

    private static void AssertProductionPipelinePolicy(WebApplicationFactory<Program> host)
    {
        Assert.Equal("Production", host.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        Assert.Contains(PipelineAllowedOrigin, host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>>()
            .Value.GetPolicy("__DefaultCorsPolicy")!.Origins);
    }

    private static void AssertReadinessShape(JsonElement root, string status)
    {
        Assert.Equal(new[] { "checks", "status", "totalDuration" }, root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(status, root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("totalDuration").GetDouble() >= 0);
        foreach (var check in root.GetProperty("checks").EnumerateObject())
        {
            Assert.Equal(new[] { "duration", "status" }, check.Value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.True(check.Value.GetProperty("duration").GetDouble() >= 0);
        }
    }

    /// <summary>Checks literal wildcard characters, Thai text, and significant spaces across each original searchable text field.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <param name="field">The searchable field populated with test text: firstName, lastName, company, email, telephone, or messageContent.</param>
    /// <returns>A task that completes after verifying literal wildcard characters, Thai text, and significant spaces across each original searchable text field.</returns>
    [Theory]
    [InlineData("/Messages", "firstName")]
    [InlineData("/Messages", "lastName")]
    [InlineData("/Messages", "company")]
    [InlineData("/Messages", "email")]
    [InlineData("/Messages", "telephone")]
    [InlineData("/Messages", "messageContent")]
    [InlineData("/messages/v1/contact-requests", "firstName")]
    [InlineData("/messages/v1/contact-requests", "lastName")]
    [InlineData("/messages/v1/contact-requests", "company")]
    [InlineData("/messages/v1/contact-requests", "email")]
    [InlineData("/messages/v1/contact-requests", "telephone")]
    [InlineData("/messages/v1/contact-requests", "messageContent")]
    public async Task SourceSearch_AllTextFieldsPreserveLiteralCharactersAndSignificantSpaces(string route, string field)
    {
        foreach (var search in new[] { "%", "_", "\\", "ข้อความ", "  Padded  " })
        {
            await postgres.ResetAsync();
            await using var db = postgres.CreateContext();
            var matched = new ContactRequest();
            var unrelated = new ContactRequest();
            SetSourceSearchField(matched, field, "Prefix" + search + "Suffix");
            SetSourceSearchField(unrelated, field, search == "  Padded  " ? "Padded" : "Unrelated");
            db.Messages.AddRange(matched, unrelated);
            await db.SaveChangesAsync();
            await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
            using var client = factory.AuthenticatedClient();
            using var response = await client.GetAsync(route + "?search=" + Uri.EscapeDataString(search));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("totalRecords").GetInt32());
            var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(matched.Id, item.GetProperty("id").GetInt32());
            Assert.Equal("Prefix" + search + "Suffix", item.GetProperty(field).GetString());
            Assert.Equal(2, await db.Messages.CountAsync());
        }
    }

    /// <summary>Checks that numeric search matches substrings of message identifiers rather than requiring identifier equality.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <returns>A task that completes after verifying that numeric search matches substrings of message identifiers rather than requiring identifier equality.</returns>
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task SourceSearch_IdUsesSubstringRatherThanNumericEquality(string route)
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        db.Messages.AddRange(new ContactRequest { Id = 21001 }, new ContactRequest { Id = 31001 }, new ContactRequest { Id = 777 });
        await db.SaveChangesAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(route + "?search=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("totalRecords").GetInt32());
        Assert.Equal(new[] { 21001, 31001 }, json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToArray());
        Assert.Equal(3, await db.Messages.CountAsync());
    }

    /// <summary>Checks that search does not expand into the contact country field and leaves the stored message unchanged.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <returns>A task that completes after verifying that search does not expand into the contact country field and leaves the stored message unchanged.</returns>
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task SourceSearch_DoesNotExpandToCountry(string route)
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        db.Messages.Add(new ContactRequest { Country = "OnlyExcludedMarker" });
        await db.SaveChangesAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(route + "?search=OnlyExcludedMarker");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("OnlyExcludedMarker", (await db.Messages.AsNoTracking().SingleAsync()).Country);
    }

    /// <summary>Checks that a JSON null creation body is rejected without changing stored messages or their versions.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <returns>A task that completes after verifying that a JSON null creation body is rejected without changing stored messages or their versions.</returns>
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task SourceNullPost_JsonNullReturnsBadRequestWithoutChangingStoredMessages(string route)
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        db.Messages.AddRange(new ContactRequest { Id = 500, FirstName = "Synthetic survivor" },
            new ContactRequest { Id = 800, MessageContent = "Synthetic unrelated" });
        await db.SaveChangesAsync();
        var before = await SnapshotSourceRowsWithVersionAsync(db);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var body = new StringContent("null", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(route, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var fresh = postgres.CreateContext();
        Assert.Equal(before, await SnapshotSourceRowsWithVersionAsync(fresh));
    }

    /// <summary>Checks that a JSON null update is rejected before existing or missing lookup without changing persisted rows.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <param name="targetExists">Whether the update targets persisted message 500 rather than absent message 999.</param>
    /// <returns>A task that completes after verifying that a JSON null update is rejected before existing or missing lookup without changing persisted rows.</returns>
    [Theory]
    [InlineData("/Messages", true)]
    [InlineData("/Messages", false)]
    [InlineData("/messages/v1/contact-requests", true)]
    [InlineData("/messages/v1/contact-requests", false)]
    public async Task SourceNullPut_JsonNullReturnsBadRequestWithoutChangingStoredMessages(string route, bool targetExists)
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        db.Messages.AddRange(new ContactRequest { Id = 500, FirstName = "Synthetic selected", MessageContent = "Retained selected" },
            new ContactRequest { Id = 800, FirstName = "Synthetic survivor", MessageContent = "Retained unrelated" });
        await db.SaveChangesAsync();
        var before = await SnapshotSourceRowsWithVersionAsync(db);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var body = new StringContent("null", Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(route + (targetExists ? "/500" : "/999"), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var fresh = postgres.CreateContext();
        Assert.Equal(before, await SnapshotSourceRowsWithVersionAsync(fresh));
    }

    /// <summary>Checks that case-insensitive text search returns exactly twenty matches from a 101-message corpus without changing rows.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <param name="field">The searchable contact field containing HelloWorld in twenty rows and unrelated text in eighty-one distractors.</param>
    /// <returns>A task that completes after verifying that case-insensitive text search returns exactly twenty matches from a 101-message corpus without changing rows.</returns>
    [Theory]
    [InlineData("/Messages", "company")]
    [InlineData("/Messages", "email")]
    [InlineData("/Messages", "firstName")]
    [InlineData("/Messages", "lastName")]
    [InlineData("/Messages", "messageContent")]
    [InlineData("/Messages", "telephone")]
    [InlineData("/messages/v1/contact-requests", "company")]
    [InlineData("/messages/v1/contact-requests", "email")]
    [InlineData("/messages/v1/contact-requests", "firstName")]
    [InlineData("/messages/v1/contact-requests", "lastName")]
    [InlineData("/messages/v1/contact-requests", "messageContent")]
    [InlineData("/messages/v1/contact-requests", "telephone")]
    public async Task SourceSearch_MixedTwentyMatchesAndEightyOneDistractorsPreserveExactCardinality(string route, string field)
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        for (var i = 0; i < 20; i++)
        {
            var matched = new ContactRequest { Id = 500 + i };
            SetSourceSearchField(matched, field, "HelloWorld");
            db.Messages.Add(matched);
        }

        for (var i = 0; i <= 80; i++)
        {
            var unrelated = new ContactRequest { Id = 800 + i };
            SetSourceSearchField(unrelated, field, "Something else");
            db.Messages.Add(unrelated);
        }

        await db.SaveChangesAsync();
        Assert.Equal(101, await db.Messages.CountAsync());
        var before = await SnapshotSourceRowsWithVersionAsync(db);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(route + "?search=hello");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(20, items.Length);
        Assert.Equal(20, json.RootElement.GetProperty("totalRecords").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("totalItems", out _));
        Assert.Equal(1, json.RootElement.GetProperty("pageIndex").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("totalPages").GetInt32());
        Assert.False(json.RootElement.GetProperty("hasPreviousPage").GetBoolean());
        Assert.False(json.RootElement.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(Enumerable.Range(500, 20).ToArray(), items.Select(item => item.GetProperty("id").GetInt32()).ToArray());
        Assert.All(items, item => Assert.Equal("HelloWorld", item.GetProperty(field).GetString()));
        await using var fresh = postgres.CreateContext();
        Assert.Equal(before, await SnapshotSourceRowsWithVersionAsync(fresh));
    }

    /// <summary>Checks that a unique identifier-substring search returns only the selected message from the retained 101-row corpus.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <returns>A task that completes after verifying that a unique identifier-substring search returns only the selected message from the retained 101-row corpus.</returns>
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task SourceSearch_UniqueIdSubstringFixtureReturnsExactlySelectedMessage(string route)
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        db.Messages.AddRange(Enumerable.Range(500, 20).Concat(Enumerable.Range(800, 81))
            .Select(id => new ContactRequest { Id = id }));
        await db.SaveChangesAsync();
        Assert.Equal(101, await db.Messages.CountAsync());
        var before = await SnapshotSourceRowsWithVersionAsync(db);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(route + "?search=512");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("totalRecords").GetInt32());
        var selected = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(512, selected.GetProperty("id").GetInt32());
        await using var fresh = postgres.CreateContext();
        Assert.Equal(before, await SnapshotSourceRowsWithVersionAsync(fresh));
    }

    private static async Task<string> SnapshotSourceRowsWithVersionAsync(ContactRequestDbContext db)
    {
        var rows = await db.Messages.AsNoTracking().OrderBy(message => message.Id).Select(message => new
        {
            message.Id,
            message.FirstName,
            message.LastName,
            message.Company,
            message.Email,
            message.Telephone,
            message.Country,
            message.MessageContent,
            message.CreatedDate,
            message.ModifiedDate,
            Version = EF.Property<uint>(message, "Version")
        }).ToArrayAsync();
        return JsonSerializer.Serialize(rows);
    }

    /// <summary>Checks paging at Int32 boundaries without offset wraparound or changes to stored messages and versions.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <param name="index">The requested page index, including zero and large values that would overflow a 32-bit offset.</param>
    /// <param name="size">The requested page size used to exercise large offsets, full pages, and alias-specific zero-size behavior.</param>
    /// <param name="expectedItems">The expected returned item count; zero means the selected page must return not found.</param>
    /// <returns>A task that completes after verifying paging at Int32 boundaries without offset wraparound or changes to stored messages and versions.</returns>
    [Theory]
    [InlineData("/Messages", 1073741825, 4, 0)]
    [InlineData("/Messages", 1431655767, 3, 0)]
    [InlineData("/Messages", int.MaxValue, 4, 0)]
    [InlineData("/messages/v1/contact-requests", 1073741825, 4, 0)]
    [InlineData("/messages/v1/contact-requests", 1431655767, 3, 0)]
    [InlineData("/messages/v1/contact-requests", int.MaxValue, 4, 0)]
    [InlineData("/Messages", 3, 2, 0)]
    [InlineData("/messages/v1/contact-requests", 3, 2, 0)]
    [InlineData("/Messages", 1, int.MaxValue, 4)]
    [InlineData("/messages/v1/contact-requests", 1, int.MaxValue, 4)]
    [InlineData("/Messages", 0, 0, 0)]
    [InlineData("/messages/v1/contact-requests", 0, 0, 1)]
    public async Task Pagination_ValidInt32OffsetsDoNotWrapOrChangeStoredMessages(string route, int index, int size, int expectedItems)
    {
        await postgres.ResetAsync();
        using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using (var seed = postgres.CreateContext())
        {
            seed.Messages.AddRange(Enumerable.Range(501, 4).Select(id => new ContactRequest
            {
                Id = id,
                FirstName = "Synthetic first " + id,
                LastName = "Synthetic last " + id,
                Company = "Synthetic company " + id,
                Email = "synthetic" + id + "@example.test",
                Telephone = "Synthetic phone " + id,
                Country = "Synthetic country " + id,
                MessageContent = "Synthetic retained message " + id,
                CreatedDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified),
                ModifiedDate = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Unspecified)
            }));
            await seed.SaveChangesAsync(expiry.Token);
        }

        string before;
        await using (var snapshot = postgres.CreateContext())
        {
            before = await SnapshotSourceRowsWithVersionAsync(snapshot);
        }

        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var response = await client.GetAsync(route + "?index=" + index + "&size=" + size, expiry.Token);
        Assert.Equal(expectedItems == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode);
        if (expectedItems != 0)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(expiry.Token));
            var page = json.RootElement;
            Assert.Equal(new[] { "hasNextPage", "hasPreviousPage", "items", "pageIndex", "totalPages", "totalRecords" },
                page.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
            Assert.Equal(Enumerable.Range(501, expectedItems).ToArray(),
                page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToArray());
            Assert.Equal(4, page.GetProperty("totalRecords").GetInt32());
            Assert.Equal(1, page.GetProperty("pageIndex").GetInt32());
            Assert.Equal(expectedItems == 1 ? 4 : 1, page.GetProperty("totalPages").GetInt32());
            Assert.False(page.GetProperty("hasPreviousPage").GetBoolean());
            Assert.Equal(expectedItems == 1, page.GetProperty("hasNextPage").GetBoolean());
        }

        await using var fresh = postgres.CreateContext();
        Assert.Equal(before, await SnapshotSourceRowsWithVersionAsync(fresh));
    }

    private static void SetSourceSearchField(ContactRequest message, string field, string value)
    {
        switch (field)
        {
            case "firstName": message.FirstName = value; break;
            case "lastName": message.LastName = value; break;
            case "company": message.Company = value; break;
            case "email": message.Email = value; break;
            case "telephone": message.Telephone = value; break;
            case "messageContent": message.MessageContent = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    /// <summary>Checks that page JSON retains totalRecords and the source navigation metadata without introducing totalItems.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <returns>A task that completes after verifying that page JSON retains totalRecords and the source navigation metadata without introducing totalItems.</returns>
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task Pagination_EmitsSourceTotalRecords_NotRenamedTotalItems(string route)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        await CreateAsync(client, route, "first");
        await CreateAsync(client, route, "second");

        using var response = await client.GetAsync(route + "?index=1&size=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("totalRecords", out var count),
            "Latest committed source pagination exposes totalRecords.");
        Assert.Equal(2, count.GetInt32());
        Assert.False(body.RootElement.TryGetProperty("totalItems", out _));
        Assert.Equal(1, body.RootElement.GetProperty("pageIndex").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("totalPages").GetInt32());
        Assert.False(body.RootElement.GetProperty("hasPreviousPage").GetBoolean());
        Assert.True(body.RootElement.GetProperty("hasNextPage").GetBoolean());
        Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
    }

    /// <summary>Checks that MVC binds a whitespace-only search to the unfiltered default listing.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <returns>A task that completes after verifying that MVC binds a whitespace-only search to the unfiltered default listing.</returns>
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task Search_WhitespaceQueryUsesMvcNullDefault(string route)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var spaced = await CreateAsync(client, route, "literal space");
        var unspaced = await CreateAsync(client, route, "nospace");

        using var response = await client.GetAsync(route + "?search=%20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { spaced, unspaced }, body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32()).ToArray());
    }

    /// <summary>Checks that omitted and empty search queries return both stored messages in identifier order.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <param name="query">The suffix appended to the collection route: empty text omits search, while ?search= supplies an empty value.</param>
    /// <returns>A task that completes after verifying that omitted and empty search queries return both stored messages in identifier order.</returns>
    [Theory]
    [InlineData("/Messages", "")]
    [InlineData("/messages/v1/contact-requests", "")]
    [InlineData("/Messages", "?search=")]
    [InlineData("/messages/v1/contact-requests", "?search=")]
    public async Task Search_AbsentAndEmptyQueriesUseUnfilteredDefault(string route, string query)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var first = await CreateAsync(client, route, "first");
        var second = await CreateAsync(client, route, "second");
        using var response = await client.GetAsync(route + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { first, second }, body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32()).ToArray());
    }

    /// <summary>Checks create, replacement, read, and delete behavior with server identity, controlled dates, and null-field omission.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <returns>A task that completes after verifying create, replacement, read, and delete behavior with server identity, controlled dates, and null-field omission.</returns>
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task Crud_PersistsServerIdentityAndDates_AndPreservesNullOmission(string route)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var created = await client.PostAsJsonAsync(route, new
        {
            Id = 999999,
            FirstName = "Synthetic",
            MessageContent = "fixture",
            CreatedDate = "1900-01-01T00:00:00",
            ModifiedDate = "1900-01-01T00:00:00",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.NotEqual(999999, id);
        Assert.Equal($"/Messages/{id}", created.Headers.Location?.AbsolutePath);
        Assert.False(body.RootElement.TryGetProperty("lastName", out _));
        Assert.False(body.RootElement.TryGetProperty("email", out _));
        Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime,
            body.RootElement.GetProperty("createdDate").GetDateTime());

        await using (var db = postgres.CreateContext())
        {
            var stored = await db.Messages.AsNoTracking().SingleAsync();
            Assert.Equal(id, stored.Id);
            Assert.Equal("Synthetic", stored.FirstName);
            Assert.Null(stored.LastName);
            Assert.Null(stored.Email);
            Assert.Equal(DateTimeKind.Unspecified, stored.CreatedDate!.Value.Kind);
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var updated = await client.PutAsJsonAsync(route + "/" + id,
            new { FirstName = (string?)null, MessageContent = "updated", Country = "TH" });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var read = await client.GetAsync(route + "/" + id);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.False(readBody.RootElement.TryGetProperty("firstName", out _));
        Assert.Equal("TH", readBody.RootElement.GetProperty("country").GetString());
        Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime,
            readBody.RootElement.GetProperty("createdDate").GetDateTime());
        Assert.Equal(ContactRuntimeFactory.InitialTime.AddMinutes(1).UtcDateTime,
            readBody.RootElement.GetProperty("modifiedDate").GetDateTime());

        using var deleted = await client.DeleteAsync(route + "/" + id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await client.GetAsync(route + "/" + id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var emptyDb = postgres.CreateContext();
        Assert.Empty(await emptyDb.Messages.AsNoTracking().ToArrayAsync());
    }

    /// <summary>Checks that invalid authentication or a missing permission prevents contact creation before persistence.</summary>
    /// <param name="scenario">The rejected caller scenario: anonymous, wrong-key, wrong-issuer, wrong-audience, expired, or no-permission.</param>
    /// <param name="expected">The expected unauthorized or forbidden HTTP status for the rejected caller.</param>
    /// <returns>A task that completes after verifying that invalid authentication or a missing permission prevents contact creation before persistence.</returns>
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-issuer", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-audience", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("no-permission", HttpStatusCode.Forbidden)]
    public async Task ProductionAuthentication_DeniesInvalidCallerBeforePersistence(string scenario, HttpStatusCode expected)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient(scenario);
        using var response = await client.PostAsJsonAsync("/Messages", new { MessageContent = "synthetic-denied" });

        Assert.Equal(expected, response.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.Messages.CountAsync());
    }

    /// <summary>Checks that PostgreSQL search treats percent, underscore, and backslash characters literally.</summary>
    /// <param name="search">The literal percent, underscore, or backslash character sent as the search query.</param>
    /// <param name="matched">The seeded message text containing the literal search character and expected to be the only returned row.</param>
    /// <returns>A task that completes after verifying that PostgreSQL search treats percent, underscore, and backslash characters literally.</returns>
    [Theory]
    [InlineData("%", "literal%")]
    [InlineData("_", "literal_")]
    [InlineData("\\", "literal\\")]
    public async Task Search_WildcardsAreLiteralUnderPostgres(string search, string matched)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var expected = await CreateAsync(client, "/Messages", matched);
        await CreateAsync(client, "/Messages", "unmatched");
        using var response = await client.GetAsync("/Messages?search=" + Uri.EscapeDataString(search));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, Assert.Single(body.RootElement.GetProperty("items").EnumerateArray())
            .GetProperty("id").GetInt32());
    }

    /// <summary>Checks that an empty message collection and a page beyond the last message both return legacy not found.</summary>
    /// <returns>A task that completes after verifying that an empty message collection and a page beyond the last message both return legacy not found.</returns>
    [Fact]
    public async Task Pagination_EmptyAndOutOfRangeKeepLegacyNotFound()
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var empty = await client.GetAsync("/Messages");
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        await CreateAsync(client, "/Messages", "one");
        using var beyond = await client.GetAsync("/Messages?index=2&size=1");
        Assert.Equal(HttpStatusCode.NotFound, beyond.StatusCode);
    }

    /// <summary>Checks that legacy updates to absent nonpositive identifiers return not found without altering the surviving message.</summary>
    /// <param name="route">The legacy message collection route with its original or lowercase casing.</param>
    /// <param name="missingId">The absent nonpositive message identifier, zero or minus one, used in the update route.</param>
    /// <returns>A task that completes after verifying that legacy updates to absent nonpositive identifiers return not found without altering the surviving message.</returns>
    [Theory]
    [InlineData("/Messages", 0)]
    [InlineData("/Messages", -1)]
    [InlineData("/messages", 0)]
    [InlineData("/messages", -1)]
    public async Task LegacyUpdate_MissingNonpositiveIdentifierPreservesSourceNotFound(string route, int missingId)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var id = await CreateAsync(client, route, "source-must-remain");
        await using var beforeDb = postgres.CreateContext();
        var before = await beforeDb.Messages.AsNoTracking().SingleAsync();

        using var response = await client.PutAsJsonAsync($"{route}/{missingId}", new { MessageContent = "must-not-persist" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var afterDb = postgres.CreateContext();
        var after = await afterDb.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(id, after.Id);
        Assert.Equal(before.MessageContent, after.MessageContent);
        Assert.Equal(before.CreatedDate, after.CreatedDate);
        Assert.Equal(before.ModifiedDate, after.ModifiedDate);
    }

    /// <summary>Checks alias-specific rejection of nonpositive update identifiers while preserving the original stored message.</summary>
    /// <param name="route">The contact collection alias under test: /Messages or /messages/v1/contact-requests.</param>
    /// <param name="invalidId">The nonpositive message identifier, zero or minus one, submitted in the update route.</param>
    /// <param name="expectedStatus">The expected route-specific status: not found for the legacy alias or bad request for the versioned alias.</param>
    /// <returns>A task that completes after verifying alias-specific rejection of nonpositive update identifiers while preserving the original stored message.</returns>
    [Theory]
    [InlineData("/Messages", 0, HttpStatusCode.NotFound)]
    [InlineData("/Messages", -1, HttpStatusCode.NotFound)]
    [InlineData("/messages/v1/contact-requests", 0, HttpStatusCode.BadRequest)]
    [InlineData("/messages/v1/contact-requests", -1, HttpStatusCode.BadRequest)]
    public async Task Update_NonpositiveIdentifierPreservesRouteStatusWithoutChangingStoredMessages(string route, int invalidId, HttpStatusCode expectedStatus)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var id = await CreateAsync(client, route, "original");
        await using var beforeDb = postgres.CreateContext();
        var before = await beforeDb.Messages.AsNoTracking().SingleAsync();

        using var response = await client.PutAsJsonAsync($"{route}/{invalidId}", new { MessageContent = "must-not-persist" });

        Assert.Equal(expectedStatus, response.StatusCode);
        await using var afterDb = postgres.CreateContext();
        var after = await afterDb.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(id, after.Id);
        Assert.Equal(before.MessageContent, after.MessageContent);
        Assert.Equal(before.CreatedDate, after.CreatedDate);
        Assert.Equal(before.ModifiedDate, after.ModifiedDate);
    }

    private static async Task<int> CreateAsync(HttpClient client, string route, string content)
    {
        using var response = await client.PostAsJsonAsync(route, new { MessageContent = content });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetInt32();
    }

    /// <summary>Checks that a stale tracked entity cannot overwrite a committed PostgreSQL xmin version.</summary>
    /// <returns>A task that completes after verifying that a stale tracked entity cannot overwrite a committed PostgreSQL xmin version.</returns>
    [Fact]
    public async Task Persistence_StaleTrackedUpdateCannotOverwriteCommittedXminVersion()
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var id = await CreateAsync(client, "/Messages", "original");
        await using var first = postgres.CreateContext();
        await using var stale = postgres.CreateContext();
        var firstRepository = new ContactRequestRepository(first);
        var staleRepository = new ContactRequestRepository(stale);
        var firstRow = await firstRepository.GetByIdForUpdateAsync(id, CancellationToken.None);
        var staleRow = await staleRepository.GetByIdForUpdateAsync(id, CancellationToken.None);
        Assert.NotNull(firstRow);
        Assert.NotNull(staleRow);
        firstRow.MessageContent = "committed-first";
        await firstRepository.UpdateAsync(firstRow, CancellationToken.None);
        staleRow.MessageContent = "stale-overwrite";

        var conflict = await Assert.ThrowsAsync<Legacy.Maliev.ContactService.Application.Models.ContactRequestConcurrencyException>(() =>
            staleRepository.UpdateAsync(staleRow, CancellationToken.None));
        Assert.IsType<DbUpdateConcurrencyException>(conflict.InnerException);
        await using var read = postgres.CreateContext();
        Assert.Equal("committed-first", (await read.Messages.AsNoTracking().SingleAsync()).MessageContent);
    }

    /// <summary>Checks that simultaneous HTTP updates produce one committed winner and one conflict response.</summary>
    /// <returns>A task that completes after verifying that simultaneous HTTP updates produce one committed winner and one conflict response.</returns>
    [Fact]
    public async Task HttpConcurrentUpdates_ReturnOneConflict_NotUnknownServerFailure()
    {
        await postgres.ResetAsync();
        var gate = new ConcurrentMutationGate();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString, mutationGate: gate);
        using var first = factory.AuthenticatedClient();
        using var second = factory.AuthenticatedClient();
        var id = await CreateAsync(first, "/Messages", "original");

        var responses = await Task.WhenAll(
            first.PutAsJsonAsync("/Messages/" + id, new { MessageContent = "first-request" }),
            second.PutAsJsonAsync("/Messages/" + id, new { MessageContent = "second-request" }));
        using var response1 = responses[0];
        using var response2 = responses[1];
        await using var read = postgres.CreateContext();
        Assert.Contains((await read.Messages.AsNoTracking().SingleAsync()).MessageContent,
            new[] { "first-request", "second-request" });
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        // Proposed PostgreSQL concurrency classification, not an old-source409 claim.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
    }

    /// <summary>Checks that cache invalidation failure after a database commit preserves the successful mutation response.</summary>
    /// <param name="operation">The committed message mutation exercised before cache failure: create, update, or delete.</param>
    /// <param name="providerCancellation">Whether cache invalidation throws provider cancellation rather than a provider error while the caller remains uncancelled.</param>
    /// <returns>A task that completes after verifying that cache invalidation failure after a database commit preserves the successful mutation response.</returns>
    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    public async Task Mutation_PostCommitCacheFailure_DoesNotMisreportPersistedEffect(string operation, bool providerCancellation)
    {
        await postgres.ResetAsync();
        int? id = null;
        if (operation != "create")
        {
            await using var seedFactory = new ContactRuntimeFactory(postgres.ConnectionString);
            using var seedClient = seedFactory.AuthenticatedClient();
            id = await CreateAsync(seedClient, "/Messages", "before-mutation");
        }

        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString,
            invalidationFailure: providerCancellation ? "provider-cancellation" : "provider-error");
        using var client = factory.AuthenticatedClient();
        using var response = operation switch
        {
            "update" => await client.PutAsJsonAsync("/Messages/" + id, new { MessageContent = "committed-before-cache-failure" }),
            "delete" => await client.DeleteAsync("/Messages/" + id),
            _ => await client.PostAsJsonAsync("/Messages", new { MessageContent = "committed-before-cache-failure" }),
        };
        await using var db = postgres.CreateContext();
        var rows = await db.Messages.AsNoTracking().ToArrayAsync();
        if (operation == "delete")
        {
            Assert.Empty(rows);
        }
        else
        {
            Assert.Equal("committed-before-cache-failure", Assert.Single(rows).MessageContent);
        }

        // Caller token is not cancelled. This is controlled adapter failure after real PG commit,
        // not evidence of Redis success or authorization to implement an idempotency contract.
        Assert.Equal(operation == "create" ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
        if (operation == "create")
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(Assert.Single(rows).Id, body.RootElement.GetProperty("id").GetInt32());
        }
    }

    /// <summary>Checks that concurrent HTTP deletes produce one success and one conflict, followed by not found.</summary>
    /// <returns>A task that completes after verifying that concurrent HTTP deletes produce one success and one conflict, followed by not found.</returns>
    [Fact]
    public async Task HttpConcurrentDeletes_ReturnOneConflict_ThenMissing()
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString, mutationGate: new ConcurrentMutationGate());
        using var first = factory.AuthenticatedClient();
        using var second = factory.AuthenticatedClient();
        var id = await CreateAsync(first, "/Messages", "delete-race");
        var responses = await Task.WhenAll(first.DeleteAsync("/Messages/" + id), second.DeleteAsync("/Messages/" + id));
        using var response1 = responses[0];
        using var response2 = responses[1];
        await using var read = postgres.CreateContext();
        Assert.Empty(await read.Messages.AsNoTracking().ToArrayAsync());
        using var missing = await first.DeleteAsync("/Messages/" + id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
    }

    /// <summary>Checks that an unclassified update or deletion persistence failure remains a server error without committing.</summary>
    /// <param name="operation">The message mutation subjected to an injected unknown save failure: update or delete.</param>
    /// <returns>A task that completes after verifying that an unclassified update or deletion persistence failure remains a server error without committing.</returns>
    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task UnknownPersistenceFailure_RemainsServerFailure_AndDoesNotCommit(string operation)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString, mutationGate: new UnknownMutationFailure());
        using var client = factory.AuthenticatedClient();
        var id = await CreateAsync(client, "/Messages", "unchanged");
        using var response = operation == "update"
            ? await client.PutAsJsonAsync("/Messages/" + id, new { MessageContent = "must-not-commit" })
            : await client.DeleteAsync("/Messages/" + id);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var read = postgres.CreateContext();
        Assert.Equal("unchanged", (await read.Messages.AsNoTracking().SingleAsync()).MessageContent);
    }

    /// <summary>Checks that a genuinely cancelled caller still receives cancellation during cache invalidation.</summary>
    /// <returns>A task that completes after verifying that a genuinely cancelled caller still receives cancellation during cache invalidation.</returns>
    [Fact]
    public async Task Invalidation_ActuallyCancelledCaller_StillPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cache = new DistributedContactRequestCache(new FailingInvalidationCache("provider-cancellation"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.InvalidateAsync(cancellation.Token));
    }
}

/// <summary>Owns a resource-limited disposable PostgreSQL container, migration lifecycle, and sanitized cleanup evidence for runtime tests.</summary>
public sealed class ContactRuntimePostgresFixture : IAsyncLifetime
{
    private readonly string lease = Guid.NewGuid().ToString("N");
    private readonly PostgreSqlContainer container;
    private readonly ContactFixtureLifecycle lifecycle;
    private readonly ContactFixtureReceiptIdentity receiptIdentity = new();

    /// <summary>Configures the labelled PostgreSQL container, resource limits, and joined startup, migration, and disposal lifecycle.</summary>
    public ContactRuntimePostgresFixture()
    {
        container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithLabel("codex.contact.run", Environment.GetEnvironmentVariable("CONTACT_HOSTED_RUN_LEASE") ?? lease)
            .WithLabel("codex.contact.fixture", lease)
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig.Memory = 536870912;
                parameters.HostConfig.NanoCPUs = 1000000000;
                parameters.HostConfig.Tmpfs = new Dictionary<string, string>
                {
                    ["/var/lib/postgresql"] = "rw,size=134217728",
                };
            }).Build();
        lifecycle = new ContactFixtureLifecycle(
            async token =>
            {
                ObserveAdmission("container-start");
                await container.StartAsync(token);
                receiptIdentity.Capture(container.Id);
                Observe("container-start-settled", null);
            },
            async token =>
            {
                ObserveAdmission("migration");
                await using var db = CreateContext();
                await db.Database.MigrateAsync(token);
                Observe("migration-settled", null);
            },
            async () =>
            {
                await container.DisposeAsync();
                Observe("container-dispose-settled", null);
            }, Observe);
    }

    /// <summary>Gets the connection string for the fixture-owned disposable PostgreSQL container.</summary>
    public string ConnectionString => container.GetConnectionString();
    /// <summary>Creates a context connected to the fixture-owned PostgreSQL database.</summary>
    /// <returns>A new caller-owned Contact database context configured with the fixture connection string.</returns>
    public ContactRequestDbContext CreateContext() => new(new DbContextOptionsBuilder<ContactRequestDbContext>()
        .UseNpgsql(ConnectionString).Options);

    /// <summary>Starts PostgreSQL and applies migrations through the joined lifecycle under a bounded initialization deadline.</summary>
    /// <returns>A task that completes after container startup and database migration settle.</returns>
    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await lifecycle.InitializeAsync(deadline.Token);
    }

    /// <summary>Clears all synthetic messages after resource admission under a bounded reset deadline.</summary>
    /// <returns>A task that completes after all rows in the fixture message table are deleted.</returns>
    public async Task ResetAsync()
    {
        ObserveAdmission("reset");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var db = CreateContext();
        await db.Messages.ExecuteDeleteAsync(deadline.Token);
    }

    /// <summary>Disposes the fixture through its retained lifecycle cleanup task.</summary>
    /// <returns>The shared task that completes when fixture-owned PostgreSQL cleanup settles.</returns>
    public Task DisposeAsync() => lifecycle.DisposeAsync();

    private void ObserveAdmission(string phase)
    {
        var memory = File.ReadLines("/proc/meminfo").Single(line => line.StartsWith("MemAvailable:", StringComparison.Ordinal));
        var availableKiB = long.Parse(memory.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture);
        ObserveRecord(phase + "-admission", null, availableKiB);
        if (availableKiB < 4194304)
        {
            throw new InvalidOperationException("Contact container phase memory admission failed; setup failure, not behavioral RED.");
        }
    }

    private void Observe(string phase, Exception? failure) => ObserveRecord(phase, failure, null);

    private void ObserveRecord(string phase, Exception? failure, long? availableKiB)
    {
        var directory = Environment.GetEnvironmentVariable("CONTACT_FIXTURE_CUSTODY_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, lease + ".jsonl"), JsonSerializer.Serialize(new
        {
            lease,
            runLease = Environment.GetEnvironmentVariable("CONTACT_HOSTED_RUN_LEASE"),
            observedUtc = DateTime.UtcNow,
            phase,
            availableKiB,
            testedCommit = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            containerId = phase == "container-start-settled" || phase == "container-dispose-settled" ? receiptIdentity.ContainerId : null,
            exceptionType = failure?.GetType().FullName,
        }) + Environment.NewLine);
    }
}

/// <summary>Retains the actual started resource identity without querying it after disposal.</summary>
internal sealed class ContactFixtureReceiptIdentity
{
    private string? containerId;

    public string? ContainerId => Volatile.Read(ref containerId);

    public void Capture(string actualContainerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actualContainerId);
        var previous = Interlocked.CompareExchange(ref containerId, actualContainerId, null);
        if (previous is not null && !string.Equals(previous, actualContainerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Contact fixture receipt identity changed; preserve uncertainty.");
        }
    }
}

internal sealed class ContactRuntimeFactory(
    string connectionString,
    SaveChangesInterceptor? mutationGate = null,
    string? invalidationFailure = null) : WebApplicationFactory<Program>
{
    private const string Issuer = "https://contact-fixture.example.invalid";
    private readonly RSA rsa = RSA.Create(2048);
    public static DateTimeOffset InitialTime { get; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public FakeTimeProvider Clock { get; } = new(InitialTime);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:ContactRequestDbContext", connectionString);
        builder.UseSetting("Cache:RedisEnabled", "false");
        builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())));
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Issuer);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (mutationGate != null)
            {
                services.AddDbContext<ContactRequestDbContext>(options => options.AddInterceptors(mutationGate));
            }

            if (invalidationFailure != null)
            {
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton<IDistributedCache>(new FailingInvalidationCache(invalidationFailure));
            }
        });
    }

    public HttpClient AuthenticatedClient(string scenario = "valid")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        if (scenario == "anonymous")
        {
            return client;
        }

        using var wrong = RSA.Create(2048);
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "synthetic-contact-caller") };
        if (scenario != "no-permission")
        {
            claims.AddRange(new[]
            {
                ContactRequestPermissions.ContactRequestsRead,
                ContactRequestPermissions.ContactRequestsCreate,
                ContactRequestPermissions.ContactRequestsUpdate,
                ContactRequestPermissions.ContactRequestsDelete,
            }.Select(permission => new Claim("permission", permission)));
        }

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(scenario == "wrong-issuer" ? "https://wrong.example.invalid" : Issuer,
            scenario == "wrong-audience" ? "https://wrong-audience.example.invalid" : Issuer,
            claims, now.AddHours(-1), scenario == "expired" ? now.AddMinutes(-20) : now.AddMinutes(10),
            new SigningCredentials(new RsaSecurityKey(scenario == "wrong-key" ? wrong : rsa), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            rsa.Dispose();
        }
    }
}

internal sealed class ConcurrentMutationGate : SaveChangesInterceptor
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrivals;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<ContactRequest>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                ready.SetResult();
            }

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        return result;
    }
}

internal sealed class UnknownMutationFailure : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<ContactRequest>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new DbUpdateException("Synthetic unknown persistence failure, not an xmin conflict.");
        }

        return ValueTask.FromResult(result);
    }
}

/// <summary>Controlled cache fault only; no fabricated database/auth/provider success.</summary>
internal sealed class FailingInvalidationCache(string failure) : IDistributedCache
{
    public byte[]? Get(string key) => null;
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        throw new NotSupportedException();
    public void Refresh(string key) => throw new NotSupportedException();
    public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
    public void Remove(string key) => throw new NotSupportedException();
    public Task RemoveAsync(string key, CancellationToken token = default) => failure == "provider-cancellation"
        ? Task.FromException(new OperationCanceledException("Synthetic provider cancellation with an uncancelled caller.", token))
        : Task.FromException(new IOException("Synthetic provider invalidation failure."));
}
