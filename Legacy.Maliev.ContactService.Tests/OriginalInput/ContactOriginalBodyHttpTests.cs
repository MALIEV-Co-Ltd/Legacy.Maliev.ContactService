using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.ContactService.Domain;
using Legacy.Maliev.ContactService.Tests.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using SourceJson = Newtonsoft.Json.JsonConvert;

namespace Legacy.Maliev.ContactService.Tests.OriginalInput;

[CollectionDefinition("Contact original body", DisableParallelization = true)]
public sealed class ContactOriginalBodyHttpCollection;

[Collection("Contact original body")]
public sealed class ContactOriginalBodyHttpTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    private const string AllKey = "legacy:contact:messages:all:v1";
    private const string OtherKey = "contact-original-body:unrelated";
    private static readonly string[] Fields =
        ["FirstName", "LastName", "Company", "Email", "Telephone", "Country", "MessageContent"];
    private static readonly string[] Lanes =
        ["legacy-create", "legacy-update", "versioned-create", "versioned-update"];
    private static readonly byte[] AllSentinel = Encoding.UTF8.GetBytes("synthetic existing message cache");
    private static readonly byte[] OtherSentinel = Encoding.UTF8.GetBytes("synthetic unrelated cache");

    public static IEnumerable<object[]> GrammarCases()
    {
        foreach (var lane in Lanes)
        {
            foreach (var grammar in new[]
            {
                "source-producer", "single-quotes", "unquoted-names", "comments",
                "trailing-comma", "additional-object", "trailing-text", "utf16"
            })
            {
                yield return [lane, grammar];
            }
        }
    }

    [Theory]
    [MemberData(nameof(GrammarCases))]
    public async Task OriginalGrammar_PreservesSevenFieldsAndServerOwnedIdentity(string lane, string grammar)
    {
        // The website producer creates all seven scalar slots plus Id and dates,
        // serializes with Newtonsoft, and consumes the stored Id from POST.
        var expected = Enumerable.Repeat<string?>(" \tข้อความ +%? ", 7).ToArray();
        var json = ProducerBody(expected);
        json = grammar switch
        {
            "single-quotes" => json.Replace('"', '\''),
            "unquoted-names" => System.Text.RegularExpressions.Regex.Replace(json, "\\\"([A-Za-z][A-Za-z0-9]*)\\\":", "$1:"),
            "comments" => "/* synthetic */" + json.Insert(1, "/* member */"),
            "trailing-comma" => json.Insert(json.Length - 1, ","),
            "additional-object" => json + """{"Id":"private-input-marker"}""",
            "trailing-text" => json + " private-input-marker",
            _ => json
        };
        await AssertSuccessfulWrite(lane, json, expected, grammar == "utf16");
    }

    [Theory]
    [InlineData("legacy-create")]
    [InlineData("legacy-update")]
    [InlineData("versioned-create")]
    [InlineData("versioned-update")]
    public async Task AllConsumedStrings_AcceptOriginalPrimitiveTextWithoutTrimming(string lane)
    {
        const string json = """
            {"Id":2147483647,"CreatedDate":"1900-01-01T00:00:00","ModifiedDate":"1900-01-02T00:00:00",
             "FirstName":42,"LastName":false,"Company":true,"Email":-12.50,
             "Telephone":1e+02,"Country":0,"MessageContent":123456789012345678901234567890}
            """;
        await AssertSuccessfulWrite(lane, json,
            ["42", "false", "true", "-12.50", "1e+02", "0", "123456789012345678901234567890"]);
    }

    public static IEnumerable<object[]> DuplicateCases()
    {
        foreach (var lane in Lanes)
        {
            foreach (var mode in new[] { "retain", "empty-clears", "default-null-clears", "explicit-null-clears" })
            {
                yield return [lane, mode];
            }
        }
    }

    [Theory]
    [MemberData(nameof(DuplicateCases))]
    public async Task OrderedNullsRetainValues_EmptyAndAbsentSlotsClearStoredFields(string lane, string mode)
    {
        var json = mode == "default-null-clears" ? "{}"
            : mode == "explicit-null-clears" ? "{" + string.Join(",", Fields.Select(field => "\"" + field + "\":null")) + "}"
            : "{" + string.Join(",", Fields.Select(field =>
                "\"" + field + "\":null,\"" + field + "\":\" keep \",\"" + field + "\":"
                + (mode == "empty-clears" ? "\"\"" : "null"))) + "}";
        var value = mode == "retain" ? " keep " : mode == "empty-clears" ? "" : null;
        await AssertSuccessfulWrite(lane, json, Enumerable.Repeat(value, 7).ToArray());
    }

    public static IEnumerable<object[]> InvalidIgnoredCases()
    {
        foreach (var lane in Lanes)
        {
            foreach (var field in new[] { "Id", "CreatedDate", "ModifiedDate" })
            {
                yield return [lane, field, false];
                if (!IsCreate(lane))
                {
                    yield return [lane, field, true];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidIgnoredCases))]
    public async Task InvalidEarlierRecognizedIdentityOrDate_RejectsBeforeLookupAndMutation(string lane, string field, bool missing)
    {
        var valid = ProducerBody(Enumerable.Repeat<string?>("valid", 7).ToArray());
        // The valid later occurrence cannot erase an earlier typed parse failure.
        var json = "{\"" + field + "\":\"private-input-marker\"," + valid[1..];
        await AssertRejectedWrite(lane, json, HttpStatusCode.BadRequest, missing);
    }

    public static IEnumerable<object[]> CapacityCases()
    {
        foreach (var lane in Lanes)
        {
            yield return [lane, 50];
            yield return [lane, 51];
        }
    }

    [Theory]
    [MemberData(nameof(CapacityCases))]
    public async Task OrdinaryMvcStringLengthValidation_RemainsOnAllSixLimitedFields(string lane, int length)
    {
        var expected = Enumerable.Repeat<string?>(new string('ก', length), 6)
            .Append(new string('M', 101)).ToArray();
        var json = ProducerBody(expected);
        if (length == 50)
        {
            await AssertSuccessfulWrite(lane, json, expected);
        }
        else
        {
            await AssertRejectedWrite(lane, json, HttpStatusCode.BadRequest, validationFields: Fields[..6]);
        }
    }

    public static IEnumerable<object[]> NoValueCases()
    {
        foreach (var lane in Lanes)
        {
            foreach (var body in new[] { "", "null", "  ", "\"\"", """{"$ref":"missing"}""" })
            {
                yield return [lane, body];
            }
        }
    }

    [Theory]
    [MemberData(nameof(NoValueCases))]
    public async Task OriginalNoValue_RemainsRequiredBody400WithoutWrite(string lane, string json)
    {
        await AssertRejectedWrite(lane, json, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("legacy-create")]
    [InlineData("legacy-update")]
    [InlineData("versioned-create")]
    [InlineData("versioned-update")]
    public async Task UnsupportedCharset_Remains415WithoutExposingItsValue(string lane)
    {
        await AssertRejectedWrite(lane, ProducerBody(Enumerable.Repeat<string?>("valid", 7).ToArray()),
            HttpStatusCode.UnsupportedMediaType, contentType: "application/json; charset=x-private-charset-marker");
    }

    public static IEnumerable<object[]> MissingCases()
    {
        foreach (var lane in new[] { "legacy-update", "versioned-update" })
        {
            foreach (var id in new[] { -1, 0, int.MaxValue })
            {
                yield return [lane, id];
            }
        }
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public async Task ValidBinding_PreservesOrdinaryMissingAndVersionedNonpositiveStatus(string lane, int id)
    {
        var expected = lane == "versioned-update" && id <= 0 ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound;
        await AssertRejectedWrite(lane, """{'MessageContent':'valid source grammar'}""", expected, targetId: id);
    }

    public static IEnumerable<object[]> AuthorityCases()
    {
        foreach (var lane in Lanes)
        {
            foreach (var scenario in new[] { "anonymous", "wrong-key", "expired", "no-permission" })
            {
                yield return [lane, scenario];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AuthorityCases))]
    public async Task OriginalBinding_DoesNotBypassProductionRs256OrPermissions(string lane, string scenario)
    {
        await AssertRejectedWrite(lane, """{'MessageContent':'private-input-marker'}""",
            scenario == "no-permission" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized,
            scenario: scenario);
    }

    [Theory]
    [InlineData("legacy-update")]
    [InlineData("versioned-update")]
    public async Task ConcurrentOriginalGrammarUpdates_KeepExistingXminConflictClassification(string lane)
    {
        var seeded = await Seed();
        var sibling = await Snapshot(seeded.Selected.Id);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString, mutationGate: new ConcurrentMutationGate());
        using var first = factory.AuthenticatedClient();
        using var second = factory.AuthenticatedClient();
        await PrimeReads(first, seeded.Selected.Id);
        await SetCache(factory);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var responses = await Task.WhenAll(
            Send(first, lane, seeded.Selected.Id, """{'MessageContent':'winner-one'}"""),
            Send(second, lane, seeded.Selected.Id, """{"MessageContent":"winner-two"} trailing-source-text"""));
        using var response1 = responses[0];
        using var response2 = responses[1];
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var row = await ReadRow(seeded.Selected.Id);
        Assert.Contains(row.MessageContent, new[] { "winner-one", "winner-two" });
        Assert.Null(row.FirstName);
        Assert.Null(row.LastName);
        Assert.Null(row.Company);
        Assert.Null(row.Email);
        Assert.Null(row.Telephone);
        Assert.Null(row.Country);
        Assert.Equal(seeded.Selected.CreatedDate, row.CreatedDate);
        Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime.AddMinutes(1), row.ModifiedDate);
        Assert.Equal(sibling, await Snapshot(seeded.Selected.Id));
        await AssertCache(factory, invalidated: true);
        await AssertPublicReads(first, row, 2);
    }

    private async Task AssertSuccessfulWrite(string lane, string json, string?[] expected, bool unicode = false)
    {
        var seeded = await Seed();
        var survivorBefore = await Snapshot(IsCreate(lane) ? 0 : seeded.Selected.Id);
        var selectedBefore = await SnapshotRow(seeded.Selected.Id);
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        await PrimeReads(client, seeded.Selected.Id);
        await SetCache(factory);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var response = await Send(client, lane, seeded.Selected.Id, json, unicode);
        Assert.Equal(IsCreate(lane) ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
        var id = seeded.Selected.Id;
        if (IsCreate(lane))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            id = body.RootElement.GetProperty("id").GetInt32();
            Assert.True(id > 0);
            Assert.NotEqual(int.MaxValue, id);
            Assert.DoesNotContain(id, new[] { seeded.Selected.Id, seeded.Sibling.Id });
            var location = Assert.IsType<Uri>(response.Headers.Location);
            Assert.Equal("/Messages/" + id, location.AbsolutePath, ignoreCase: true);
            Assert.Equal(lane == "versioned-create" ? "?version=1" : "", location.Query);
            AssertFields(body.RootElement, expected);
            Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime.AddMinutes(1), body.RootElement.GetProperty("createdDate").GetDateTime());
            Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime.AddMinutes(1), body.RootElement.GetProperty("modifiedDate").GetDateTime());
        }
        else
        {
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        var row = await ReadRow(id);
        Assert.Equal(expected, Values(row));
        Assert.Equal(IsCreate(lane) ? ContactRuntimeFactory.InitialTime.UtcDateTime.AddMinutes(1) : seeded.Selected.CreatedDate, row.CreatedDate);
        Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime.AddMinutes(1), row.ModifiedDate);
        if (!IsCreate(lane))
        {
            Assert.NotEqual(selectedBefore, await SnapshotRow(id));
        }
        Assert.Equal(survivorBefore, await Snapshot(id));
        await AssertCache(factory, invalidated: true);
        await AssertPublicReads(client, row, IsCreate(lane) ? 3 : 2);
        await AssertCache(factory, invalidated: true);
    }

    private async Task AssertRejectedWrite(string lane, string json, HttpStatusCode status, bool missing = false,
        string? contentType = null, string[]? validationFields = null, int? targetId = null, string scenario = "valid")
    {
        var seeded = await Seed();
        var before = await Snapshot();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var reader = factory.AuthenticatedClient();
        await PrimeReads(reader, seeded.Selected.Id);
        await SetCache(factory);
        using var client = factory.AuthenticatedClient(scenario);
        using var response = await Send(client, lane, targetId ?? (missing ? int.MaxValue : seeded.Selected.Id),
            json, contentType: contentType);
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        foreach (var marker in new[] { "private-input-marker", "x-private-charset-marker", "Npgsql", "SELECT " })
        {
            Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
        }
        if (validationFields is not null)
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
            var errors = document.RootElement.GetProperty("errors");
            foreach (var field in validationFields)
            {
                Assert.Contains(errors.EnumerateObject(), error => error.Name.EndsWith(field, StringComparison.OrdinalIgnoreCase));
            }
        }
        Assert.Equal(before, await Snapshot());
        await AssertCache(factory, invalidated: false);
        await AssertPublicReads(reader, seeded.Selected, 2);
        Assert.Equal(before, await Snapshot());
        await AssertCache(factory, invalidated: false);
    }

    private static bool IsCreate(string lane) => lane.EndsWith("-create", StringComparison.Ordinal);

    private static string Route(string lane, int id)
    {
        var root = lane.StartsWith("versioned", StringComparison.Ordinal) ? "/messages/v1/contact-requests" : "/Messages";
        return IsCreate(lane) ? root : root + "/" + id;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string lane, int id, string json,
        bool unicode = false, string? contentType = null)
    {
        using var request = new HttpRequestMessage(IsCreate(lane) ? HttpMethod.Post : HttpMethod.Put, Route(lane, id));
        request.Content = new ByteArrayContent((unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes(json));
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
            contentType ?? (unicode ? "application/json; charset=utf-16" : "application/json; charset=utf-8"));
        return await client.SendAsync(request);
    }

    private static string ProducerBody(string?[] values)
    {
        Assert.Equal(7, values.Length);
        var fields = Fields.Select((field, index) => (field, value: (object?)values[index]))
            .ToDictionary(item => item.field, item => item.value);
        fields["Id"] = 0;
        fields["CreatedDate"] = new DateTime(1900, 1, 1);
        fields["ModifiedDate"] = new DateTime(1900, 1, 2);
        return SourceJson.SerializeObject(fields);
    }

    private async Task<(ContactRequest Selected, ContactRequest Sibling)> Seed()
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        var selected = NewSeed("before");
        var sibling = NewSeed("sibling");
        db.Messages.AddRange(selected, sibling);
        await db.SaveChangesAsync();
        Assert.True(selected.Id > 0);
        Assert.True(sibling.Id > 0);
        Assert.NotEqual(selected.Id, sibling.Id);
        return (selected, sibling);
    }

    private static ContactRequest NewSeed(string value) => new()
    {
        FirstName = value,
        LastName = value,
        Company = value,
        Email = value,
        Telephone = value,
        Country = value,
        MessageContent = value,
        CreatedDate = new DateTime(2020, 1, 1),
        ModifiedDate = new DateTime(2020, 1, 2)
    };

    private async Task<ContactRequest> ReadRow(int id)
    {
        await using var db = postgres.CreateContext();
        return await db.Messages.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private async Task<string> Snapshot(int excludeId = 0)
    {
        await using var db = postgres.CreateContext();
        return await db.Database.SqlQuery<string>($"""
            SELECT coalesce(jsonb_agg(to_jsonb(m) || jsonb_build_object('_xmin', m.xmin::text) ORDER BY m."ID"), '[]'::jsonb)::text AS "Value"
            FROM "Message" m WHERE m."ID" <> {excludeId}
            """).SingleAsync();
    }

    private async Task<string> SnapshotRow(int id)
    {
        await using var db = postgres.CreateContext();
        return await db.Database.SqlQuery<string>($"""
            SELECT (to_jsonb(m) || jsonb_build_object('_xmin', m.xmin::text))::text AS "Value"
            FROM "Message" m WHERE m."ID" = {id}
            """).SingleAsync();
    }

    private static async Task SetCache(ContactRuntimeFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        await cache.SetAsync(AllKey, AllSentinel, new DistributedCacheEntryOptions());
        await cache.SetAsync(OtherKey, OtherSentinel, new DistributedCacheEntryOptions());
    }

    private static async Task AssertCache(ContactRuntimeFactory factory, bool invalidated)
    {
        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        if (invalidated)
        {
            Assert.Null(await cache.GetAsync(AllKey));
        }
        else
        {
            Assert.Equal(AllSentinel, await cache.GetAsync(AllKey));
        }
        Assert.Equal(OtherSentinel, await cache.GetAsync(OtherKey));
    }

    private static async Task PrimeReads(HttpClient client, int id)
    {
        foreach (var root in new[] { "/Messages", "/messages/v1/contact-requests" })
        {
            using var detail = await client.GetAsync(root + "/" + id);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var list = await client.GetAsync(root + "?index=1&size=10");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }
    }

    private static string?[] Values(ContactRequest row) =>
        [row.FirstName, row.LastName, row.Company, row.Email, row.Telephone, row.Country, row.MessageContent];

    private static void AssertFields(JsonElement body, string?[] values)
    {
        for (var index = 0; index < Fields.Length; index++)
        {
            var name = char.ToLowerInvariant(Fields[index][0]) + Fields[index][1..];
            if (values[index] is null)
            {
                Assert.False(body.TryGetProperty(name, out _));
            }
            else
            {
                Assert.Equal(values[index], body.GetProperty(name).GetString());
            }
            Assert.False(body.TryGetProperty(Fields[index], out _));
        }
    }

    private static async Task AssertPublicReads(HttpClient client, ContactRequest expected, int count)
    {
        foreach (var root in new[] { "/Messages", "/messages/v1/contact-requests" })
        {
            using var detail = await client.GetAsync(root + "/" + expected.Id);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            var body = document.RootElement;
            Assert.Equal(expected.Id, body.GetProperty("id").GetInt32());
            AssertFields(body, Values(expected));
            Assert.Equal(expected.CreatedDate, body.GetProperty("createdDate").GetDateTime());
            Assert.Equal(expected.ModifiedDate, body.GetProperty("modifiedDate").GetDateTime());
            using var list = await client.GetAsync(root + "?sort=MessageId_Ascending&index=1&size=10");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(count, page.RootElement.GetProperty("totalRecords").GetInt32());
            var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetInt32() == expected.Id);
            AssertFields(item, Values(expected));
        }
    }
}
