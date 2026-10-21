using System.Text;
using Legacy.Maliev.ContactService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Legacy.Maliev.ContactService.Tests.OriginalInput;

[Collection("Contact original body")]
public sealed class ContactOriginalBodyReaderTests(ContactOriginalBodyReaderFixture fixture)
    : IClassFixture<ContactOriginalBodyReaderFixture>
{
    private IServiceProvider Services => fixture.Factory.Services;
    private static readonly string[] Fields =
        ["FirstName", "LastName", "Company", "Email", "Telephone", "Country", "MessageContent"];

    private static string Projection(string? field = null, string? value = null)
    {
        var values = new string?[7];
        if (field is not null) values[Array.IndexOf(Fields, field)] = value;
        return System.Text.Json.JsonSerializer.Serialize(values);
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return Row("empty object", "{}", "success", "success", Projection(), Projection());
        yield return Row("standard", """{"FirstName":"ไทย","MessageContent":" literal "}""", "success", "success",
            """["\u0E44\u0E17\u0E22",null,null,null,null,null," literal "]""");
        foreach (var field in Fields)
        {
            foreach (var item in new[]
            {
                (Label: "string", Token: "\" literal \"", Value: " literal ", Native: "success"),
                (Label: "integer", Token: "42", Value: "42", Native: "failure"),
                (Label: "decimal lexeme", Token: "12.50", Value: "12.50", Native: "failure"),
                (Label: "exponent lexeme", Token: "1e2", Value: "1e2", Native: "failure"),
                (Label: "true", Token: "true", Value: "true", Native: "failure"),
                (Label: "false", Token: "false", Value: "false", Native: "failure"),
                (Label: "null", Token: "null", Value: (string?)null, Native: "success"),
                (Label: "empty", Token: "\"\"", Value: "", Native: "success"),
                (Label: "lone high", Token: "\"\\ud800\"", Value: "\uFFFD", Native: "failure"),
                (Label: "lone low", Token: "\"\\udc00\"", Value: "\uFFFD", Native: "failure"),
                (Label: "valid pair", Token: "\"\\ud83d\\ude00\"", Value: "\U0001f600", Native: "success"),
                (Label: "replacement literal", Token: "\"\uFFFD\"", Value: "\uFFFD", Native: "success")
            })
            {
                var expected = Projection(field, item.Value);
                yield return Row(field + " " + item.Label, "{\"" + field + "\":" + item.Token + "}",
                    "success", item.Native, expected, item.Native == "success" ? expected : null);
            }
            foreach (var token in new[] { "{}", "[]", "\"\\ud80X\"" })
                yield return Row(field + " invalid " + token, "{\"" + field + "\":" + token + "}",
                    "failure", "failure", "null");
            yield return Row(field + " ordered null", "{\"" + field + "\":\"first\",\"" + field + "\":null}",
                "success", "success", Projection(field, "first"), Projection());
            yield return Row(field + " ordered empty", "{\"" + field + "\":\"first\",\"" + field + "\":\"\"}",
                "success", "success", Projection(field, ""), Projection(field, ""));
            yield return Row(field + " invalid before valid", "{\"" + field + "\":{},\"" + field + "\":\"last\"}",
                "failure", "failure", "null");
        }

        const string standard = """{"MessageContent":"body"}""";
        var expectedBody = Projection("MessageContent", "body");
        foreach (var grammar in new[]
        {
            (Label: "single quotes", Json: standard.Replace('"', '\'')),
            (Label: "unquoted", Json: "{MessageContent:\"body\"}"),
            (Label: "leading comment", Json: "/* comment */" + standard),
            (Label: "member comment", Json: standard.Insert(1, "/* comment */")),
            (Label: "trailing comma", Json: standard.Insert(standard.Length - 1, ",")),
            (Label: "additional object", Json: standard + "{\"Id\":\"private-marker\"}"),
            (Label: "trailing text", Json: standard + " private-marker")
        })
            yield return Row(grammar.Label, grammar.Json, "success", "failure", expectedBody);
        foreach (var item in new[]
        {
            (Label: "null", Json: "null", Source: "no-value", Native: "no-value"),
            (Label: "missing", Json: "", Source: "no-value", Native: "no-value"),
            (Label: "whitespace", Json: " \r\n ", Source: "no-value", Native: "failure"),
            (Label: "empty string", Json: "\"\"", Source: "no-value", Native: "failure"),
            (Label: "string root", Json: "\"wrong\"", Source: "failure", Native: "failure"),
            (Label: "array root", Json: "[]", Source: "failure", Native: "failure"),
            (Label: "boolean root", Json: "true", Source: "failure", Native: "failure"),
            (Label: "number root", Json: "42", Source: "failure", Native: "failure"),
            (Label: "unresolved reference", Json: "{\"$ref\":\"missing\"}", Source: "no-value", Native: "success"),
            (Label: "invalid reference type", Json: "{\"$ref\":42}", Source: "failure", Native: "success"),
            (Label: "reference with extra fields", Json: "{\"$ref\":\"missing\",\"Id\":1}", Source: "failure", Native: "success"),
            (Label: "wrong values wrapper", Json: "{\"$values\":[]}", Source: "failure", Native: "success"),
            (Label: "unexpected type null", Json: "{\"$type\":null}", Source: "throw:System.NullReferenceException", Native: "success")
        })
            yield return Row(item.Label, item.Json, item.Source, item.Native, "null");
        yield return Row("type activation disabled", standard.Insert(1, "\"$type\":\"System.IO.FileInfo, System.Private.CoreLib\","),
            "success", "success", expectedBody, expectedBody);
        yield return Row("unknown graph skipped", standard.Insert(1, "\"Employee\":[{\"RoleId\":{}}],"),
            "success", "success", expectedBody, expectedBody);
        yield return Row("null reference metadata", standard.Insert(1, "\"$ref\":null,"),
            "success", "success", expectedBody, expectedBody);

        foreach (var field in new[] { "Id", "CreatedDate", "ModifiedDate" })
        {
            foreach (var token in new[] { "null", "\"\"", "\"42\"" })
            {
                var accepted = field == "Id" || token != "\"42\"";
                yield return Row(field + " scalar " + token, standard.Insert(1, "\"" + field + "\":" + token + ","),
                    accepted ? "success" : "failure", "success", accepted ? expectedBody : "null", expectedBody);
            }
            foreach (var token in new[] { "{}", "[]", "true", "\"private-marker\"" })
                yield return Row(field + " discarded invalid " + token, standard.Insert(1, "\"" + field + "\":" + token + ","),
                    "failure", "success", "null", expectedBody);
            yield return Row(field + " invalid before valid", standard.Insert(1,
                "\"" + field + "\":{},\"" + field + "\":null,"), "failure", "success", "null", expectedBody);
        }
        foreach (var token in new[] { "0", "2147483647", "\" +7 \"" })
            yield return Row("discarded Id accepted " + token, standard.Insert(1, "\"Id\":" + token + ","),
                "success", "success", expectedBody, expectedBody);
        foreach (var token in new[] { "2147483648", "7.0", "\" \"" })
            yield return Row("discarded Id refused " + token, standard.Insert(1, "\"Id\":" + token + ","),
                "failure", "success", "null", expectedBody);
        foreach (var field in new[] { "CreatedDate", "ModifiedDate" })
            yield return Row(field + " valid source date", standard.Insert(1,
                "\"" + field + "\":\"2001-02-03T04:05:06Z\","), "success", "success", expectedBody, expectedBody);
    }

    private static object[] Row(string label, string json, string source, string native,
        string expected, string? nativeProjection = null) => [label, json, source, native, expected, nativeProjection!];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualStockSourceNativeAndCandidate_AgreeOnPinnedInputBoundary(
        string label, string json, string sourceKind, string nativeKind, string projection, string? nativeProjection)
    {
        var source = await ContactOriginalInputOracle.ReadAsync(json);
        Assert.Equal(sourceKind, source.Kind);
        if (source.Kind == "success") Assert.Equal(projection, ContactOriginalInputOracle.Projection(source.Model));
        var bytes = Encoding.UTF8.GetBytes(json);
        var native = await ContactOriginalBodyNative.ReadAsync(Services, ContactOriginalBodyNative.Create(Services), bytes);
        Assert.Equal(nativeKind, native.Kind);
        if (native.Kind == "success" && nativeProjection is not null)
            Assert.Equal(nativeProjection, ContactOriginalInputOracle.Projection(native.Model));
        var candidate = await ContactOriginalBodyNative.ReadAsync(Services,
            Services.GetRequiredService<IOptions<MvcOptions>>().Value, bytes);
        Assert.Equal(sourceKind, candidate.Kind);
        if (candidate.Kind == "success") Assert.Equal(projection, ContactOriginalInputOracle.Projection(candidate.Model));
        if (candidate.Kind == "failure")
        {
            Assert.True(candidate.ErrorCount > 0);
            Assert.DoesNotContain(candidate.Messages, message => message.Contains("private-marker", StringComparison.Ordinal));
        }
        _ = label;
    }

    [Theory]
    [InlineData(31, "success")]
    [InlineData(32, "success")]
    [InlineData(33, "failure")]
    public async Task Depth32_IsTheActualMvcDefaultForAllThreeReaders(int depth, string expected)
    {
        var json = "{\"unknown\":" + new string('[', depth - 1) + "0" + new string(']', depth - 1) + "}";
        Assert.Equal(expected, (await ContactOriginalInputOracle.ReadAsync(json)).Kind);
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.Equal(expected, (await ContactOriginalBodyNative.ReadAsync(Services,
            ContactOriginalBodyNative.Create(Services), bytes)).Kind);
        Assert.Equal(expected, (await ContactOriginalBodyNative.ReadAsync(Services,
            Services.GetRequiredService<IOptions<MvcOptions>>().Value, bytes)).Kind);
    }

    [Theory]
    [InlineData("application/json", false, "success")]
    [InlineData("text/json", false, "success")]
    [InlineData("application/vnd.contact+json", false, "success")]
    [InlineData("application/json; charset=utf-16", true, "success")]
    [InlineData("application/json; charset=utf-16BE", false, "failure")]
    [InlineData("application/json; charset=private-charset", false, "failure")]
    [InlineData("application/xml", false, "unsupported")]
    public async Task StockCharsetSelectionAndSafe415_PrecedeOwnedBodyDrain(string contentType, bool unicode, string expected)
    {
        var bytes = (unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes("{\"MessageContent\":\"body\"}");
        var source = await ContactOriginalInputOracle.ReadBytesAsync(ContactOriginalInputOracle.Formatter(),
            ContactOriginalInputOracle.SourceType, new EmptyModelMetadataProvider(), bytes, contentType);
        Assert.Equal(expected, source.Kind);
        var candidate = await ContactOriginalBodyNative.ReadAsync(Services,
            Services.GetRequiredService<IOptions<MvcOptions>>().Value, bytes, contentType);
        Assert.Equal(expected, candidate.Kind);
        if (expected == "failure")
        {
            Assert.Contains(typeof(UnsupportedContentTypeException).FullName!, candidate.ExceptionTypes);
            Assert.DoesNotContain(candidate.Messages, value => value.Contains("private-charset", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ReferenceMapsArePerRead_AndMalformedPathsNeverEchoInputs()
    {
        var options = Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        Assert.Equal("success", (await ContactOriginalBodyNative.ReadAsync(Services, options,
            Encoding.UTF8.GetBytes("{\"$id\":\"previous\",\"MessageContent\":\"body\"}"))).Kind);
        Assert.Equal("no-value", (await ContactOriginalBodyNative.ReadAsync(Services, options,
            Encoding.UTF8.GetBytes("{\"$ref\":\"previous\"}"))).Kind);
        var bad = await ContactOriginalBodyNative.ReadAsync(Services, options,
            Encoding.UTF8.GetBytes("{\"private-path-marker\":[}"), prefix: "private-prefix-marker", maxErrors: 2);
        Assert.Equal("failure", bad.Kind);
        Assert.InRange(bad.ErrorCount, 1, 2);
        Assert.All(bad.Keys, key => Assert.Equal("$", key));
        foreach (var marker in new[] { "private-path-marker", "private-prefix-marker" })
            Assert.DoesNotContain(string.Join("\n", bad.Messages.Concat(bad.Keys)), marker, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginalMvcDefaults_KeepTypeActivationOffAndAdditionalContentUnchecked()
    {
        var json = ContactOriginalInputOracle.OriginalOptions();
        Assert.Equal(32, json.SerializerSettings.MaxDepth);
        Assert.Equal(TypeNameHandling.None, json.SerializerSettings.TypeNameHandling);
        Assert.False(json.SerializerSettings.CheckAdditionalContent);
        Assert.Equal(NullValueHandling.Ignore, json.SerializerSettings.NullValueHandling);
        Assert.Equal(ReferenceLoopHandling.Ignore, json.SerializerSettings.ReferenceLoopHandling);
        Assert.Equal(30 * 1024, json.InputFormatterMemoryBufferThreshold);
    }
}
