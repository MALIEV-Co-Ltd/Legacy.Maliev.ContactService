using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

[Collection("Contact literal payload")]
public sealed class ContactDocumentationExamplesHttpTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task GeneratedExamples_AreRealCreateAndReplacementPayloads(string route)
    {
        await postgres.ResetAsync();
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var factory = parent.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var signed = parent.AuthenticatedClient();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = signed.DefaultRequestHeaders.Authorization;
        using var documentResponse = await client.GetAsync("/messages/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, documentResponse.StatusCode);
        using var document = JsonDocument.Parse(await documentResponse.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var requestSchema = schemas.GetProperty("UpsertContactRequestRequest");
        var schemaExample = Assert.Single(requestSchema.GetProperty("examples").EnumerateArray());
        AssertPayloadFields(schemaExample);
        var properties = requestSchema.GetProperty("properties");
        Assert.Equal("Example", Assert.Single(properties.GetProperty("firstName").GetProperty("examples").EnumerateArray()).GetString());
        Assert.Equal("Thailand", Assert.Single(properties.GetProperty("country").GetProperty("examples").EnumerateArray()).GetString());
        Assert.Contains("not a numeric country identifier", properties.GetProperty("country").GetProperty("description").GetString());
        Assert.Contains("does not create or link a company record", properties.GetProperty("company").GetProperty("description").GetString());
        var pageProperties = schemas.GetProperty("PaginatedContactRequestResponse").GetProperty("properties");
        var pageExample = Assert.Single(pageProperties.GetProperty("items").GetProperty("examples").EnumerateArray());
        var itemExample = Assert.Single(pageExample.EnumerateArray());
        Assert.Equal(42, itemExample.GetProperty("id").GetInt32());
        AssertLiteralFields(schemaExample, itemExample);
        var paths = document.RootElement.GetProperty("paths");
        var createExample = BodyExample(paths.GetProperty(route).GetProperty("post"));
        AssertPayloadFields(createExample);
        using var created = await client.PostAsJsonAsync(route, createExample);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        AssertLiteralFields(createExample, createdJson.RootElement);
        var updateExample = BodyExample(paths.GetProperty(route + "/{messageId}").GetProperty("put"));
        AssertPayloadFields(updateExample);
        Assert.NotEqual(createExample.GetProperty("messageContent").GetString(), updateExample.GetProperty("messageContent").GetString());
        using var updated = await client.PutAsJsonAsync($"{route}/{id}", updateExample);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var read = await client.GetAsync($"{route}/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(id, readJson.RootElement.GetProperty("id").GetInt32());
        AssertLiteralFields(updateExample, readJson.RootElement);
        await using var db = postgres.CreateContext();
        var stored = await db.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(id, stored.Id);
        Assert.Equal(updateExample.GetProperty("messageContent").GetString(), stored.MessageContent);
        Assert.Equal(updateExample.GetProperty("company").GetString(), stored.Company);
        Assert.Equal(updateExample.GetProperty("country").GetString(), stored.Country);
    }

    private static JsonElement BodyExample(JsonElement operation) =>
        operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example");

    private static readonly string[] PayloadFields = ["firstName", "lastName", "company", "email", "telephone", "country", "messageContent"];

    private static void AssertPayloadFields(JsonElement example)
    {
        Assert.Equal(PayloadFields.OrderBy(name => name), example.EnumerateObject().Select(value => value.Name).OrderBy(name => name));
        Assert.All(example.EnumerateObject(), field => Assert.Equal(JsonValueKind.String, field.Value.ValueKind));
    }

    private static void AssertLiteralFields(JsonElement expected, JsonElement actual)
    {
        foreach (var field in PayloadFields)
            Assert.Equal(expected.GetProperty(field).GetString(), actual.GetProperty(field).GetString());
    }
}
