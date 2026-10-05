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
