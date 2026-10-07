using System.Net;
using System.Text.Json;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

[Collection("Contact literal payload")]
public sealed class ContactSourcePaginationCardinalityHttpTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task OriginalThousandRecordPagingContract_DefaultAllAndPageEdgesPreservePhysicalRows(string route)
    {
        await postgres.ResetAsync();
        await using (var db = postgres.CreateContext())
        {
            db.Messages.AddRange(Enumerable.Range(1, 1000).Select(number => new ContactRequest
            {
                FirstName = $"Synthetic contact {number}",
                MessageContent = "Synthetic pagination fixture",
                CreatedDate = new DateTime(2020, 1, 1).AddMinutes(number)
            }));
            await db.SaveChangesAsync();
        }
        int[] expectedIds;
        string before;
        await using (var db = postgres.CreateContext())
        {
            var rows = await db.Messages.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
            Assert.Equal(1000, rows.Length);
            expectedIds = rows.Select(row => row.Id).ToArray();
            Assert.Equal(1000, expectedIds.Distinct().Count());
            before = JsonSerializer.Serialize(rows);
        }
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        await AssertPageAsync(client, route, expectedIds, 1, 1, false, false);
        for (var page = 1; page <= 10; page++)
        {
            await AssertPageAsync(client, $"{route}?index={page}&size=100",
                expectedIds.Skip((page - 1) * 100).Take(100).ToArray(), page, 10, page > 1, page < 10);
        }
        await AssertPageAsync(client, route + "?index=28&size=37", [expectedIds[^1]], 28, 28, true, false);
        foreach (var query in new[] { "?index=11&size=100", "?index=29&size=37" })
        {
            using var response = await client.GetAsync(route + query);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        await using var after = postgres.CreateContext();
        Assert.Equal(before, JsonSerializer.Serialize(await after.Messages.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
    }

    private static async Task AssertPageAsync(HttpClient client, string path, int[] ids,
        int index, int totalPages, bool previous, bool next)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = json.RootElement;
        Assert.Equal(1000, page.GetProperty("totalRecords").GetInt32());
        Assert.Equal(index, page.GetProperty("pageIndex").GetInt32());
        Assert.Equal(totalPages, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(previous, page.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(next, page.GetProperty("hasNextPage").GetBoolean());
        Assert.False(page.TryGetProperty("Items", out _));
        Assert.Equal(ids, page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToArray());
    }
}
