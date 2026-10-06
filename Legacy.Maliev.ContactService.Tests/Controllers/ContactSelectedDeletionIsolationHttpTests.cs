using System.Net;
using System.Text.Json;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

[Collection("Contact literal payload")]
public sealed class ContactSelectedDeletionIsolationHttpTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task DeleteSelectedMessage_PreservesUnrelatedRowsAndCrossAliasReads(string deleteRoute)
    {
        await postgres.ResetAsync();
        var createdDate = new DateTime(2020, 1, 1);
        ContactRequest[] messages =
        [
            new ContactRequest
            {
                FirstName = "ผู้ติดต่อก่อนหน้า",
                LastName = "  Literal survivor  ",
                Email = "first-survivor@example.test",
                Country = "Thailand",
                MessageContent = "ข้อความเดิม +%?",
                CreatedDate = createdDate,
                ModifiedDate = createdDate.AddDays(1)
            },
            new ContactRequest
            {
                FirstName = "Selected",
                Company = "Selected fixture company",
                Email = "selected-delete@example.test",
                Telephone = "0800",
                MessageContent = "Selected private fixture message",
                CreatedDate = createdDate.AddDays(2),
                ModifiedDate = createdDate.AddDays(3)
            },
            new ContactRequest
            {
                LastName = "ผู้ติดต่อถัดไป",
                Company = "  บริษัทตัวอย่าง  ",
                Telephone = "0900",
                MessageContent = "ข้อความที่ต้องเก็บไว้",
                CreatedDate = createdDate.AddDays(4),
                ModifiedDate = createdDate.AddDays(5)
            }
        ];
        await using (var db = postgres.CreateContext())
        {
            db.Messages.AddRange(messages);
            await db.SaveChangesAsync();
        }
        Assert.All(messages, message => Assert.True(message.Id > 0));
        Assert.Equal(3, messages.Select(message => message.Id).Distinct().Count());
        await using (var readback = postgres.CreateContext())
        {
            var stored = await readback.Messages.AsNoTracking().OrderBy(message => message.Id).ToArrayAsync();
            Assert.Equal(JsonSerializer.Serialize(messages.OrderBy(message => message.Id)), JsonSerializer.Serialize(stored));
        }
        var selected = messages[1];
        var survivors = messages.Where(message => message.Id != selected.Id).OrderBy(message => message.Id).ToArray();
        var expectedAfter = JsonSerializer.Serialize(survivors);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var routes = new[] { "/Messages", "/messages/v1/contact-requests" };
        foreach (var route in routes) await AssertListAsync(client, route, messages.OrderBy(message => message.Id).ToArray());
        using (var deleted = await client.DeleteAsync($"{deleteRoute}/{selected.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(string.Empty, await deleted.Content.ReadAsStringAsync());
        }
        foreach (var route in routes)
        {
            using var missing = await client.GetAsync($"{route}/{selected.Id}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var missingBody = await missing.Content.ReadAsStringAsync();
            Assert.DoesNotContain("selected-delete@example.test", missingBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Selected private fixture message", missingBody, StringComparison.Ordinal);
            Assert.DoesNotContain("messageContent", missingBody, StringComparison.Ordinal);
            using var repeated = await client.DeleteAsync($"{route}/{selected.Id}");
            Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
            await AssertListAsync(client, route, survivors);
            foreach (var survivor in survivors)
            {
                using var detail = await client.GetAsync($"{route}/{survivor.Id}");
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
                AssertMessage(json.RootElement, survivor);
            }
        }
        await using var final = postgres.CreateContext();
        var after = await final.Messages.AsNoTracking().OrderBy(message => message.Id).ToArrayAsync();
        Assert.Equal(expectedAfter, JsonSerializer.Serialize(after));
    }

    private static async Task AssertListAsync(HttpClient client, string route, ContactRequest[] expected)
    {
        using var response = await client.GetAsync(route + "?sort=MessageId_Ascending&index=1&size=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = document.RootElement;
        Assert.Equal(expected.Length, page.GetProperty("totalRecords").GetInt32());
        Assert.Equal(1, page.GetProperty("pageIndex").GetInt32());
        Assert.Equal(1, page.GetProperty("totalPages").GetInt32());
        Assert.False(page.GetProperty("hasPreviousPage").GetBoolean());
        Assert.False(page.GetProperty("hasNextPage").GetBoolean());
        Assert.False(page.TryGetProperty("Items", out _));
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(expected.Select(message => message.Id).ToArray(), items.Select(item => item.GetProperty("id").GetInt32()).ToArray());
        for (var index = 0; index < expected.Length; index++) AssertMessage(items[index], expected[index]);
    }

    private static void AssertMessage(JsonElement body, ContactRequest expected)
    {
        Assert.Equal(expected.Id, body.GetProperty("id").GetInt32());
        Assert.Equal(expected.CreatedDate, body.GetProperty("createdDate").GetDateTime());
        Assert.Equal(expected.ModifiedDate, body.GetProperty("modifiedDate").GetDateTime());
        var fields = new Dictionary<string, string?>
        {
            ["firstName"] = expected.FirstName,
            ["lastName"] = expected.LastName,
            ["company"] = expected.Company,
            ["email"] = expected.Email,
            ["telephone"] = expected.Telephone,
            ["country"] = expected.Country,
            ["messageContent"] = expected.MessageContent
        };
        foreach (var field in fields)
        {
            if (field.Value is null) Assert.False(body.TryGetProperty(field.Key, out _));
            else Assert.Equal(field.Value, body.GetProperty(field.Key).GetString());
        }
        var names = fields.Where(field => field.Value != null).Select(field => field.Key)
            .Concat(new[] { "id", "createdDate", "modifiedDate" }).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(names, body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }
}
