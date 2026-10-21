using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

[Collection("Contact literal payload")]
public sealed class ContactOrdinaryZeroSizeHttpTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    private const string CacheKey = "legacy:contact:messages:all:v1";
    private const string OtherCacheKey = "legacy:contact:zero-size:unrelated";

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task OrdinaryExplicitZeroSize_PreservesSourceEmptyPageAndNormalNotFoundBody(int index, bool populated)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        await SeedAsync(populated, token);
        var before = await SnapshotAsync(token);

        // Source 5fac706a/72eb9f19: controller f962650b and helper 9684bc3f.
        // The helper clamps index only, then Skip(...).Take(pageSize); zero
        // gives no items and the controller returns NotFound. This is a
        // cardinality oracle, not a historical SQL Server/runtime execution.
        const int sourcePageSize = 0;
        var sourceIds = populated ? new[] { 901, 902 } : Array.Empty<int>();
        Assert.Empty(sourceIds.OrderBy(id => id)
            .Skip((Math.Max(1, index) - 1) * sourcePageSize).Take(sourcePageSize));

        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = factory.Services.GetRequiredService<IDistributedCache>();
        await PrimeCacheAsync(cache, token);
        using var response = await client.GetAsync("/Messages?index=" + index + "&size=0", token);
        var raw = await response.Content.ReadAsStringAsync(token);
        Assert.Equal(before, await SnapshotAsync(token));
        await AssertCacheAsync(cache, token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNotFoundBody(response, raw);

        using var missing = await client.GetAsync("/Messages/2147483647", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(missing.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentType?.MediaType);
        using var expectedBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(token));
        using var actualBody = JsonDocument.Parse(raw);
        foreach (var property in new[] { "type", "title", "status" })
            Assert.Equal(expectedBody.RootElement.GetProperty(property).GetRawText(),
                actualBody.RootElement.GetProperty(property).GetRawText());
        Assert.Equal(before, await SnapshotAsync(token));
        await AssertCacheAsync(cache, token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task VersionedExplicitZeroSize_RetainsAcceptedSingleRowClamp(int index)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        await SeedAsync(true, token);
        var before = await SnapshotAsync(token);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = factory.Services.GetRequiredService<IDistributedCache>();
        await PrimeCacheAsync(cache, token);
        using var response = await client.GetAsync(
            "/messages/v1/contact-requests?index=" + index + "&size=0", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var page = body.RootElement;
        Assert.Equal(1, page.GetProperty("pageIndex").GetInt32());
        Assert.Equal(2, page.GetProperty("totalRecords").GetInt32());
        Assert.Equal(2, page.GetProperty("totalPages").GetInt32());
        Assert.False(page.GetProperty("hasPreviousPage").GetBoolean());
        Assert.True(page.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(901, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(before, await SnapshotAsync(token));
        await AssertCacheAsync(cache, token);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("no-permission", HttpStatusCode.Forbidden)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    public async Task OrdinaryZeroSize_DoesNotBypassNormalAuthenticationAndReadPermission(
        string scenario, HttpStatusCode expected)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        await SeedAsync(true, token);
        var before = await SnapshotAsync(token);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient(scenario);
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = factory.Services.GetRequiredService<IDistributedCache>();
        await PrimeCacheAsync(cache, token);
        using var response = await client.GetAsync("/Messages?size=0", token);
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("zero-size-private-marker", await response.Content.ReadAsStringAsync(token),
            StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(token));
        await AssertCacheAsync(cache, token);
    }

    [Theory]
    [InlineData("?size=0&index=not-an-index")]
    [InlineData("?size=0&sort=not-a-sort")]
    [InlineData("?size=not-a-size")]
    public async Task OrdinaryZeroSize_DoesNotHideExistingQueryBindingErrors(string query)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        await SeedAsync(true, token);
        var before = await SnapshotAsync(token);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = factory.Services.GetRequiredService<IDistributedCache>();
        await PrimeCacheAsync(cache, token);
        using var response = await client.GetAsync("/Messages" + query, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(token));
        await AssertCacheAsync(cache, token);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("1", 1)]
    public async Task OrdinaryOmittedAndPositiveSizes_KeepTheirExistingPageContract(string? size, int expectedCount)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        await SeedAsync(true, token);
        var before = await SnapshotAsync(token);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = factory.Services.GetRequiredService<IDistributedCache>();
        await PrimeCacheAsync(cache, token);
        using var response = await client.GetAsync("/Messages" + (size is null ? "" : "?size=" + size), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var page = body.RootElement;
        Assert.Equal(1, page.GetProperty("pageIndex").GetInt32());
        Assert.Equal(2, page.GetProperty("totalRecords").GetInt32());
        Assert.Equal(size is null ? 1 : 2, page.GetProperty("totalPages").GetInt32());
        Assert.False(page.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(size is not null, page.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(Enumerable.Range(901, expectedCount).ToArray(),
            page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToArray());
        Assert.Equal(before, await SnapshotAsync(token));
        await AssertCacheAsync(cache, token);
    }

    private async Task SeedAsync(bool populated, CancellationToken token)
    {
        await postgres.ResetAsync();
        if (!populated) return;
        await using var db = postgres.CreateContext();
        db.Messages.AddRange(Enumerable.Range(901, 2).Select(id => new ContactRequest
        {
            Id = id,
            FirstName = "zero-size-private-marker " + id,
            LastName = "Synthetic last " + id,
            Company = "Synthetic company " + id,
            Email = "zero" + id + "@example.test",
            Telephone = "Synthetic telephone " + id,
            Country = "Synthetic country " + id,
            MessageContent = "Synthetic retained contact message " + id,
            CreatedDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified),
            ModifiedDate = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Unspecified)
        }));
        await db.SaveChangesAsync(token);
    }

    private async Task<string> SnapshotAsync(CancellationToken token)
    {
        await using var db = postgres.CreateContext();
        var rows = await db.Messages.AsNoTracking().OrderBy(row => row.Id).Select(row => new
        {
            row.Id,
            row.FirstName,
            row.LastName,
            row.Company,
            row.Email,
            row.Telephone,
            row.Country,
            row.MessageContent,
            row.CreatedDate,
            row.ModifiedDate,
            Version = EF.Property<uint>(row, "Version")
        }).ToArrayAsync(token);
        return JsonSerializer.Serialize(rows);
    }

    private static Task PrimeCacheAsync(IDistributedCache cache, CancellationToken token) => Task.WhenAll(
        cache.SetAsync(CacheKey, Encoding.UTF8.GetBytes("retained-contact-cache"), token),
        cache.SetAsync(OtherCacheKey, Encoding.UTF8.GetBytes("retained-unrelated-cache"), token));

    private static async Task AssertCacheAsync(IDistributedCache cache, CancellationToken token)
    {
        Assert.Equal(Encoding.UTF8.GetBytes("retained-contact-cache"), await cache.GetAsync(CacheKey, token));
        Assert.Equal(Encoding.UTF8.GetBytes("retained-unrelated-cache"), await cache.GetAsync(OtherCacheKey, token));
    }

    private static void AssertNotFoundBody(HttpResponseMessage response, string raw)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Not Found", body.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("type").GetString()));
        Assert.DoesNotContain("zero-size-private-marker", raw, StringComparison.Ordinal);
        Assert.False(body.RootElement.TryGetProperty("items", out _));
    }
}
