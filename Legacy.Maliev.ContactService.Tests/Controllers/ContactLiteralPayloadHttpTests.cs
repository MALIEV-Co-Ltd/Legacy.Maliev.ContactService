using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

[CollectionDefinition("Contact literal payload", DisableParallelization = true)]
public sealed class ContactLiteralPayloadCollection;

[Collection("Contact literal payload")]
public sealed class ContactLiteralPayloadHttpTests(ContactRuntimePostgresFixture postgres) : IClassFixture<ContactRuntimePostgresFixture>
{
    [Fact]
    public async Task Scaffold_ActualEfPreviewBuildAndGeneratedQueriesPreserveMigratedOwnedMessage()
    {
        await postgres.ResetAsync();
        await using var context = postgres.CreateContext();
        var row = new Legacy.Maliev.ContactService.Domain.ContactRequest
        {
            FirstName = "Scaffold",
            LastName = "Contact",
            Company = "Fixture company",
            Email = "scaffold@example.test",
            Telephone = "000",
            Country = "Thailand",
            MessageContent = "synthetic scaffold message"
        };
        context.Messages.Add(row);
        await context.SaveChangesAsync();
        await ContactScaffoldRuntimeProof.RunAsync(postgres, row.Id);
    }

    [Theory]
    [InlineData("/Messages", "")]
    [InlineData("/Messages", "  Literal value  ")]
    [InlineData("/Messages", "ข้อความภาษาไทย")]
    [InlineData("/messages/v1/contact-requests", "")]
    [InlineData("/messages/v1/contact-requests", "  Literal value  ")]
    [InlineData("/messages/v1/contact-requests", "ข้อความภาษาไทย")]
    public async Task AllSourcePayloadFields_RoundTripLiterallyThenClearWithoutAcceptingClientIdentityOrDates(string route, string value)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var payload = Fields(value);
        payload["Id"] = 999999;
        payload["CreatedDate"] = "1900-01-01T00:00:00";
        payload["ModifiedDate"] = "1900-01-01T00:00:00";
        using var created = await client.PostAsJsonAsync(route, payload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.NotEqual(999999, id);
        AssertFields(createdJson.RootElement, value);
        await using var db = postgres.CreateContext();
        var original = await db.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime, original.CreatedDate);
        Assert.Equal(original.CreatedDate, original.ModifiedDate);
        Assert.Equal(Enumerable.Repeat(value, 7), new[] { original.FirstName, original.LastName, original.Company, original.Email, original.Telephone, original.Country, original.MessageContent });
        using var read = await client.GetAsync($"{route}/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        AssertFields(readJson.RootElement, value);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var cleared = await client.PutAsJsonAsync($"{route}/{id}", Fields(null));
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        var stored = await db.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal(original.CreatedDate, stored.CreatedDate);
        Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime.AddMinutes(1), stored.ModifiedDate);
        Assert.All(new[] { stored.FirstName, stored.LastName, stored.Company, stored.Email, stored.Telephone, stored.Country, stored.MessageContent }, item => Assert.Null(item));
        using var final = await client.GetAsync($"{route}/{id}");
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        using var finalJson = JsonDocument.Parse(await final.Content.ReadAsStringAsync());
        AssertFields(finalJson.RootElement, null);
        Assert.Equal(id, finalJson.RootElement.GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("MessageCreatedDate_Ascending", false)]
    [InlineData("MessageCreatedDate_Descending", true)]
    public async Task NullableCreatedDateSort_PreservesSourceNullPlacementAcrossPages(string sort, bool descending)
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        var undated = new Legacy.Maliev.ContactService.Domain.ContactRequest { MessageContent = "Undated fixture" };
        var earliest = new Legacy.Maliev.ContactService.Domain.ContactRequest { MessageContent = "Earliest fixture", CreatedDate = new DateTime(2020, 1, 1) };
        var middle = new Legacy.Maliev.ContactService.Domain.ContactRequest { MessageContent = "Middle fixture", CreatedDate = new DateTime(2020, 1, 2) };
        var latest = new Legacy.Maliev.ContactService.Domain.ContactRequest { MessageContent = "Latest fixture", CreatedDate = new DateTime(2020, 1, 3) };
        db.Messages.AddRange(middle, undated, latest, earliest);
        await db.SaveChangesAsync();
        await db.Messages.Where(message => message.Id == undated.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.CreatedDate, (DateTime?)null));
        Assert.Null(await db.Messages.AsNoTracking().Where(message => message.Id == undated.Id)
            .Select(message => message.CreatedDate).SingleAsync());
        var before = await db.Messages.AsNoTracking().OrderBy(message => message.Id).ToArrayAsync();
        var expected = descending
            ? new[] { latest.Id, middle.Id, earliest.Id, undated.Id }
            : new[] { undated.Id, earliest.Id, middle.Id, latest.Id };
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        foreach (var route in new[] { "/Messages", "/messages/v1/contact-requests" })
        {
            using var response = await client.GetAsync($"{route}?sort={sort}&index=1&size=4");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(expected, items.Select(item => item.GetProperty("id").GetInt32()).ToArray());
            Assert.Equal(4, json.RootElement.GetProperty("totalRecords").GetInt32());
            Assert.False(items.Single(item => item.GetProperty("id").GetInt32() == undated.Id).TryGetProperty("createdDate", out _));
            for (var index = 1; index <= expected.Length; index++)
            {
                using var pageResponse = await client.GetAsync($"{route}?sort={sort}&index={index}&size=1");
                Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
                using var pageJson = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
                var page = pageJson.RootElement;
                Assert.Equal(index, page.GetProperty("pageIndex").GetInt32());
                Assert.Equal(4, page.GetProperty("totalRecords").GetInt32());
                Assert.Equal(4, page.GetProperty("totalPages").GetInt32());
                Assert.Equal(index > 1, page.GetProperty("hasPreviousPage").GetBoolean());
                Assert.Equal(index < 4, page.GetProperty("hasNextPage").GetBoolean());
                Assert.Equal(expected[index - 1], Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
            }
            using var beyond = await client.GetAsync($"{route}?sort={sort}&index=5&size=1");
            Assert.Equal(HttpStatusCode.NotFound, beyond.StatusCode);
        }
        var after = await db.Messages.AsNoTracking().OrderBy(message => message.Id).ToArrayAsync();
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    private static Dictionary<string, object?> Fields(string? value) => new()
    {
        ["FirstName"] = value,
        ["LastName"] = value,
        ["Company"] = value,
        ["Email"] = value,
        ["Telephone"] = value,
        ["Country"] = value,
        ["MessageContent"] = value
    };

    private static void AssertFields(JsonElement body, string? value)
    {
        foreach (var name in new[] { "firstName", "lastName", "company", "email", "telephone", "country", "messageContent" })
        {
            if (value is null) Assert.False(body.TryGetProperty(name, out _));
            else Assert.Equal(value, body.GetProperty(name).GetString());
        }
        Assert.False(body.TryGetProperty("FirstName", out _));
    }
}
