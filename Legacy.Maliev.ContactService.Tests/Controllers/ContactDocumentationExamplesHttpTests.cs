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
        var responseProperties = schemas.GetProperty("ContactRequestResponse").GetProperty("properties");
        Assert.Contains("service-assigned identifier", responseProperties.GetProperty("id").GetProperty("description").GetString());
        foreach (var field in PayloadFields)
            Assert.Contains("omitted when null", responseProperties.GetProperty(field).GetProperty("description").GetString());
        Assert.Contains("one-based", pageProperties.GetProperty("pageIndex").GetProperty("description").GetString());
        Assert.Contains("requested page size", pageProperties.GetProperty("totalPages").GetProperty("description").GetString());
        Assert.Contains("totalRecords", pageProperties.GetProperty("totalRecords").GetProperty("description").GetString());
        Assert.Contains("preceding page", pageProperties.GetProperty("hasPreviousPage").GetProperty("description").GetString());
        Assert.Contains("following page", pageProperties.GetProperty("hasNextPage").GetProperty("description").GetString());
        var createExample = BodyExample(paths.GetProperty(route).GetProperty("post"));
        AssertPayloadFields(createExample);
        using var created = await client.PostAsJsonAsync(route, createExample);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        AssertLiteralFields(createExample, createdJson.RootElement);
        var updateOperation = paths.GetProperty(route + "/{messageId}").GetProperty("put");
        Assert.Contains("omitted or null fields are cleared", updateOperation.GetProperty("description").GetString());
        var updateExample = BodyExample(updateOperation);
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
        var originalCreatedDate = stored.CreatedDate;
        parent.Clock.Advance(TimeSpan.FromMinutes(1));
        using var cleared = await client.PutAsJsonAsync($"{route}/{id}", new { messageContent = "Only this field remains", company = (string?)null });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        using var page = await client.GetAsync(route + "?index=1&size=10");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        using var pageJson = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        var selected = Assert.Single(pageJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(id, selected.GetProperty("id").GetInt32());
        Assert.Equal("Only this field remains", selected.GetProperty("messageContent").GetString());
        foreach (var field in PayloadFields.Where(field => field != "messageContent"))
            Assert.False(selected.TryGetProperty(field, out _));
        Assert.Equal(1, pageJson.RootElement.GetProperty("pageIndex").GetInt32());
        Assert.Equal(1, pageJson.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, pageJson.RootElement.GetProperty("totalRecords").GetInt32());
        Assert.False(pageJson.RootElement.GetProperty("hasPreviousPage").GetBoolean());
        Assert.False(pageJson.RootElement.GetProperty("hasNextPage").GetBoolean());
        var clearedRow = await db.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(originalCreatedDate, clearedRow.CreatedDate);
        Assert.NotEqual(stored.ModifiedDate, clearedRow.ModifiedDate);
        Assert.Equal("Only this field remains", clearedRow.MessageContent);
        Assert.Null(clearedRow.FirstName);
        Assert.Null(clearedRow.LastName);
        Assert.Null(clearedRow.Company);
        Assert.Null(clearedRow.Email);
        Assert.Null(clearedRow.Telephone);
        Assert.Null(clearedRow.Country);
    }

    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task DocumentedMalformedBody_ReturnsValidationProblemWithoutPersisting(string route)
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
        var badRequest = document.RootElement.GetProperty("paths").GetProperty(route).GetProperty("post")
            .GetProperty("responses").GetProperty("400");
        Assert.Contains("without creating a message", badRequest.GetProperty("description").GetString());
        var schema = badRequest.GetProperty("content").GetProperty("application/problem+json").GetProperty("schema");
        Assert.EndsWith("/ValidationProblemDetails", schema.GetProperty("$ref").GetString());
        var errors = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("ValidationProblemDetails").GetProperty("properties").GetProperty("errors");
        Assert.Contains("JSON path", errors.GetProperty("description").GetString());
        using var body = new StringContent("{", System.Text.Encoding.UTF8, "application/json");
        using var rejected = await client.PostAsync(route, body);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        var actualErrors = problem.RootElement.GetProperty("errors");
        Assert.NotEmpty(actualErrors.EnumerateObject());
        Assert.All(actualErrors.EnumerateObject(), error => Assert.NotEmpty(error.Value.EnumerateArray()));
        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.Messages.CountAsync());
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
