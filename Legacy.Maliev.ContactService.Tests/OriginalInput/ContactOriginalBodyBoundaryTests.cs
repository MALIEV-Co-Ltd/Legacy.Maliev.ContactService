using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Domain;
using Legacy.Maliev.ContactService.Tests.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ContactService.Tests.OriginalInput;

/// <summary>Checks that the original contact-body formatter is confined to its request DTO while retaining native serializer, schema, validation, and diagnostic boundaries.</summary>
/// <param name="postgres">The shared PostgreSQL fixture supplying host connection settings, database reset, and fresh contexts for contact-body persistence assertions.</param>
[Collection("Contact original body")]
public sealed class ContactOriginalBodyBoundaryTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    private const string FormatterCategory = "Microsoft.AspNetCore.Mvc.Formatters.NewtonsoftJsonInputFormatter";

    /// <summary>Checks that only the contact upsert DTO selects the owned formatter and that remaining input formatters and all output formatters retain native order.</summary>
    /// <returns>A task that completes after asserting formatter selection, media compatibility, native formatter ordering, and exclusion of unrelated types.</returns>
    [Fact]
    public async Task Selection_IsOnlyOwnedDtoAndPreservesNativeFormatterAndWriterOrder()
    {
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        var services = factory.Services;
        var options = services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var native = ContactOriginalBodyNative.Create(services);
        Assert.NotSame(options, native);
        var owned = Assert.Single(options.InputFormatters, value => value.GetType().FullName == ContactOriginalBodyNative.FormatterName);
        Assert.Same(owned, options.InputFormatters[0]);
        Assert.Equal(native.InputFormatters.Select(value => value.GetType()).ToArray(),
            options.InputFormatters.Where(value => !ReferenceEquals(value, owned)).Select(value => value.GetType()).ToArray());
        Assert.Equal(native.OutputFormatters.Select(value => value.GetType()).ToArray(),
            options.OutputFormatters.Select(value => value.GetType()).ToArray());
        Assert.Single(options.InputFormatters, value => value is SystemTextJsonInputFormatter);
        Assert.DoesNotContain(options.OutputFormatters, value => value.GetType().FullName?.Contains("Newtonsoft", StringComparison.Ordinal) == true);
        var metadata = services.GetRequiredService<IModelMetadataProvider>();
        Assert.True(owned.CanRead(Context(typeof(UpsertContactRequestRequest), metadata)));
        Assert.True(owned.CanRead(Context(typeof(UpsertContactRequestRequest), metadata, "application/vnd.contact+json")));
        foreach (var type in new[] { typeof(ContactRequest), typeof(ContactRequestResponse),
            typeof(PaginatedContactRequestResponse), typeof(string), typeof(object) })
            Assert.False(owned.CanRead(Context(type, metadata)));
    }

    /// <summary>Checks that reading an original-style scalar leaves MVC and minimal-API System.Text.Json options, metadata callbacks, and serialized output unchanged.</summary>
    /// <returns>A task that completes after asserting source-compatible input conversion and preservation of native option instances, resolver chains, converters, metadata, and output.</returns>
    [Fact]
    public async Task FreshNativeOptionsAndRead_KeepExistingStjOptionsCallbacksAndOutput()
    {
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        var services = factory.Services;
        var json = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        var minimal = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        JsonSerializer.Serialize<object?>(null, json);
        var resolver = json.TypeInfoResolver;
        var chain = json.TypeInfoResolverChain.ToArray();
        var converters = json.Converters.ToArray();
        var setups = services.GetServices<IConfigureOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().ToArray();
        var metadata = json.GetTypeInfo(typeof(UpsertContactRequestRequest));
        var constructor = metadata.CreateObject;
        var readers = metadata.Properties.Select(value => value.CustomConverter).ToArray();
        var request = new UpsertContactRequestRequest(" literal ไทย ", null, " company ", null, null, null, " line\nสอง ");
        var before = JsonSerializer.Serialize(request, json);
        var native = ContactOriginalBodyNative.Create(services);
        var result = await ContactOriginalBodyNative.ReadAsync(services,
            services.GetRequiredService<IOptions<MvcOptions>>().Value, Encoding.UTF8.GetBytes("{\"FirstName\":123}"));
        Assert.Equal("success", result.Kind);
        Assert.Equal("123", Assert.IsType<UpsertContactRequestRequest>(result.Model).FirstName);
        Assert.Single(native.InputFormatters, value => value is SystemTextJsonInputFormatter);
        Assert.Same(json, services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions);
        Assert.Same(minimal, services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions);
        Assert.Same(resolver, json.TypeInfoResolver);
        Assert.Equal(chain.Length, json.TypeInfoResolverChain.Count);
        for (var index = 0; index < chain.Length; index++) Assert.Same(chain[index], json.TypeInfoResolverChain[index]);
        Assert.Equal(converters.Length, json.Converters.Count);
        for (var index = 0; index < converters.Length; index++) Assert.Same(converters[index], json.Converters[index]);
        var afterSetups = services.GetServices<IConfigureOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().ToArray();
        Assert.Equal(setups.Length, afterSetups.Length);
        for (var index = 0; index < setups.Length; index++) Assert.Same(setups[index], afterSetups[index]);
        var afterMetadata = json.GetTypeInfo(typeof(UpsertContactRequestRequest));
        Assert.Same(metadata, afterMetadata);
        Assert.Same(constructor, afterMetadata.CreateObject);
        for (var index = 0; index < readers.Length; index++) Assert.Same(readers[index], afterMetadata.Properties[index].CustomConverter);
        Assert.Equal(before, JsonSerializer.Serialize(request, json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertContactRequestRequest>("{\"FirstName\":123}", json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertContactRequestRequest>("{\"FirstName\":123}", minimal));
    }

    /// <summary>Checks that served OpenAPI retains the native contact request schema, all seven property names, and every request media entry referencing that schema.</summary>
    /// <returns>A task that completes after comparing the adapted and native request schemas and their referencing request media entries.</returns>
    [Fact]
    public async Task ServedOpenApi_KeepsNativeRequestSchemaAndAllRequestMedia()
    {
        await using var parent = new ContactRuntimeFactory(postgres.ConnectionString);
        await using var candidate = parent.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        await using var native = parent.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(ContactOriginalBodyNative.RemoveOwned);
        });
        using var actualClient = candidate.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var nativeClient = native.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var actual = await DocumentAsync(actualClient);
        var expected = await DocumentAsync(nativeClient);
        const string name = "UpsertContactRequestRequest";
        var actualSchema = actual.GetProperty("components").GetProperty("schemas").GetProperty(name);
        Assert.Equal(Canonical(expected.GetProperty("components").GetProperty("schemas").GetProperty(name)), Canonical(actualSchema));
        Assert.Equal(new[] { "company", "country", "email", "firstName", "lastName", "messageContent", "telephone" },
            actualSchema.GetProperty("properties").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal).ToArray());
        var media = RequestMedia(expected, name);
        Assert.NotEmpty(media);
        Assert.Equal(media, RequestMedia(actual, name));
    }

    /// <summary>Checks that the six bounded contact fields retain 50-character attributes on record constructor parameters while message content has no string-length attribute.</summary>
    [Fact]
    public void ExistingSixLengthAttributes_RemainOnRecordParametersWithNoMessageLimit()
    {
        var parameters = typeof(UpsertContactRequestRequest).GetConstructors().Single().GetParameters();
        Assert.Equal(7, parameters.Length);
        foreach (var parameter in parameters)
        {
            var limits = parameter.GetCustomAttributes<StringLengthAttribute>().ToArray();
            if (parameter.Name == "MessageContent") Assert.Empty(limits);
            else Assert.Equal(50, Assert.Single(limits).MaximumLength);
        }
    }

    /// <summary>Checks that MVC rejects a 51-character value in each bounded contact field before any request is persisted.</summary>
    /// <param name="field">The bounded request property, FirstName, LastName, Company, Email, Telephone, or Country, replaced with 51 characters before submission.</param>
    /// <returns>A task that completes after asserting a bad-request response and an empty message table for the selected oversized field.</returns>
    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Company")]
    [InlineData("Email")]
    [InlineData("Telephone")]
    [InlineData("Country")]
    public async Task ActualMvc_RejectsEach51CharacterBoundedFieldBeforePersistence(string field)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var body = Payload();
        body[field] = new string('x', 51);
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/Messages", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.Messages.CountAsync());
    }

    /// <summary>Checks that MVC accepts and persists 501 Thai characters in message content despite the separate 50-character contact-field limits.</summary>
    /// <returns>A task that completes after asserting a created response and exact persistence of the long message content.</returns>
    [Fact]
    public async Task ActualMvc_MessageContentOver50RemainsUnboundedAndPersisted()
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var body = Payload();
        body["MessageContent"] = new string('ม', 501);
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/Messages", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(body["MessageContent"], (await db.Messages.SingleAsync()).MessageContent);
    }

    /// <summary>Checks that owned formatter failures produce debug diagnostics without exposing input markers through errors, log state, scopes, or exception objects.</summary>
    /// <returns>A task that completes after asserting nonempty formatter debug logging and sanitized errors, structured state, scopes, and exception fields for malformed bodies and unsupported charset input.</returns>
    [Fact]
    public async Task ActualFormatterDebug_IsNonemptyAndContainsNoInputPathsValuesCharsetOrException()
    {
        var capture = new FormatterCapture();
        using var loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));
        var setupType = typeof(Program).Assembly.GetType(ContactOriginalBodyNative.SetupName, true)!;
        var setup = Assert.IsAssignableFrom<IConfigureOptions<MvcOptions>>(
            Activator.CreateInstance(setupType, loggers, new DefaultObjectPoolProvider()));
        var options = new MvcOptions();
        setup.Configure(options);
        var formatter = Assert.Single(options.InputFormatters, value => value.GetType().FullName == ContactOriginalBodyNative.FormatterName);
        string[] markers = ["private-path-71", "private-value-42", "private-charset-89", "private-prefix-54"];
        foreach (var (body, contentType) in new[]
        {
            ("{\"private-path-71\":{\"deep\":[0,", "application/json"),
            ("{\"FirstName\":{\"private-path-71\":\"private-value-42\"}}", "application/json"),
            ("{}", "application/json; charset=private-charset-89")
        })
        {
            var result = await ContactOriginalInputOracle.ReadBytesAsync(formatter, ContactOriginalInputOracle.DtoType,
                new EmptyModelMetadataProvider(), Encoding.UTF8.GetBytes(body), contentType, "private-prefix-54", 200);
            Assert.Equal("failure", result.Kind);
            Assert.True(result.ErrorCount > 0);
            AssertPrivate(string.Join("\n", result.Keys.Concat(result.Messages)), markers);
        }
        Assert.NotEmpty(capture.Records);
        Assert.Contains(capture.Records, value => value.Level == LogLevel.Debug);
        Assert.All(capture.Records, value =>
        {
            Assert.Equal(FormatterCategory, value.Category);
            Assert.Null(value.Exception);
            AssertPrivate(value.Rendered + "\n" + string.Join("\n", value.State), markers);
        });
        Assert.All(capture.Scopes, value => AssertPrivate(value, markers));
        // Retained BodyModelBinder Debug logging is deliberately outside this claim.
    }

    private static Dictionary<string, string?> Payload() => new()
    {
        ["FirstName"] = "Name",
        ["LastName"] = "Family",
        ["Company"] = "Company",
        ["Email"] = "contact@example.invalid",
        ["Telephone"] = "123",
        ["Country"] = "TH",
        ["MessageContent"] = "message"
    };

    private static InputFormatterContext Context(Type type, IModelMetadataProvider metadata, string contentType = "application/json")
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = contentType;
        return new(http, "", new ModelStateDictionary(), metadata.GetMetadataForType(type),
            static (stream, encoding) => new HttpRequestStreamReader(stream, encoding), false);
    }

    private static async Task<JsonElement> DocumentAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/messages/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string[] RequestMedia(JsonElement document, string name) =>
        document.GetProperty("paths").EnumerateObject().SelectMany(path => path.Value.EnumerateObject()
            .Where(operation => operation.Value.ValueKind == JsonValueKind.Object && operation.Value.TryGetProperty("requestBody", out _))
            .SelectMany(operation => operation.Value.GetProperty("requestBody").GetProperty("content").EnumerateObject()
                .Where(media => References(media.Value.GetProperty("schema"), "#/components/schemas/" + name))
                .Select(media => path.Name + "|" + operation.Name + "|" + media.Name + "|" + Canonical(media.Value.GetProperty("schema")))))
            .Order(StringComparer.Ordinal).ToArray();

    private static bool References(JsonElement value, string reference) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property =>
            property.Name == "$ref" && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == reference
            || References(property.Value, reference)),
        JsonValueKind.Array => value.EnumerateArray().Any(item => References(item, reference)),
        _ => false
    };

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => JsonSerializer.Serialize(item.Name) + ":" + Canonical(item.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
        _ => value.GetRawText()
    };

    private static void AssertPrivate(string value, string[] markers)
    {
        foreach (var marker in markers) Assert.DoesNotContain(marker, value, StringComparison.Ordinal);
    }

    private sealed record CapturedLog(string Category, LogLevel Level, string Rendered, string[] State, Exception? Exception);

    private sealed class FormatterCapture : ILoggerProvider
    {
        internal ConcurrentQueue<CapturedLog> Records { get; } = new();
        internal ConcurrentQueue<string> Scopes { get; } = new();
        public ILogger CreateLogger(string categoryName) => categoryName == FormatterCategory
            ? new CaptureLogger(this, categoryName) : NullLogger.Instance;
        public void Dispose() { }

        private sealed class CaptureLogger(FormatterCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                owner.Scopes.Enqueue(Convert.ToString(state, CultureInfo.InvariantCulture) ?? "");
                return null;
            }
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> structured
                    ? structured.Select(value => value.Key + "=" + Convert.ToString(value.Value, CultureInfo.InvariantCulture)).ToArray()
                    : [Convert.ToString(state, CultureInfo.InvariantCulture) ?? ""];
                owner.Records.Enqueue(new(category, logLevel, formatter(state, exception), values, exception));
            }
        }
    }
}
