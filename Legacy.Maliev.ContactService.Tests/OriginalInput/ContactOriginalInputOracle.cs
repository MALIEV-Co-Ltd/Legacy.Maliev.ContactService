using System.Buffers;
using System.Text;
using Legacy.Maliev.ContactService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

namespace Legacy.Maliev.ContactService.Tests.OriginalInput;

// An independent original-shape model and actual stock MVC reader. This does
// not use JsonConvert convenience defaults or the candidate's private model.
internal static class ContactOriginalInputOracle
{
    internal static Type SourceType => typeof(SourceMessage);
    internal static Type DtoType => typeof(UpsertContactRequestRequest);

    internal static MvcNewtonsoftJsonOptions OriginalOptions()
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        return options;
    }

    internal static NewtonsoftJsonInputFormatter Formatter()
    {
        var json = OriginalOptions();
        return new NewtonsoftJsonInputFormatter(NullLogger<NewtonsoftJsonInputFormatter>.Instance,
            json.SerializerSettings, ArrayPool<char>.Shared, new DefaultObjectPoolProvider(), new MvcOptions(), json);
    }

    internal static Task<ContactBodyRead> ReadAsync(string json, string contentType = "application/json", string prefix = "") =>
        ReadBytesAsync(Formatter(), SourceType, new EmptyModelMetadataProvider(),
            Encoding.UTF8.GetBytes(json), contentType, prefix);

    internal static async Task<ContactBodyRead> ReadBytesAsync(IInputFormatter formatter, Type type,
        IModelMetadataProvider metadata, byte[] bytes, string contentType, string prefix = "", int maxErrors = 200)
    {
        var http = new DefaultHttpContext();
        using var body = new MemoryStream(bytes, writable: false);
        http.Request.Body = body;
        http.Request.ContentLength = bytes.LongLength;
        http.Request.ContentType = contentType;
        var state = new ModelStateDictionary { MaxAllowedErrors = maxErrors };
        var context = new InputFormatterContext(http, prefix, state, metadata.GetMetadataForType(type),
            static (stream, encoding) => new HttpRequestStreamReader(stream, encoding), false);
        try
        {
            if (!formatter.CanRead(context)) return Result("unsupported", null, state);
            var result = await formatter.ReadAsync(context);
            Assert.Same(body, http.Request.Body);
            return Result(result.HasError ? "failure" : result.IsModelSet ? "success" : "no-value", result.Model, state);
        }
        catch (Exception failure)
        {
            return Result("throw:" + failure.GetType().FullName, null, state);
        }
    }

    private static ContactBodyRead Result(string kind, object? model, ModelStateDictionary state) => new(kind, model,
        state.Keys.Order(StringComparer.Ordinal).ToArray(), state.ErrorCount,
        state.Values.SelectMany(value => value.Errors).Select(value => value.Exception?.GetType().FullName ?? "").ToArray(),
        state.Values.SelectMany(value => value.Errors).Select(value => value.ErrorMessage.Length == 0
            ? value.Exception?.Message ?? "" : value.ErrorMessage).ToArray());

    internal static string Projection(object? model) => model switch
    {
        null => "null",
        SourceMessage source => System.Text.Json.JsonSerializer.Serialize(new object?[]
        {
            source.FirstName, source.LastName, source.Company, source.Email,
            source.Telephone, source.Country, source.MessageContent
        }),
        UpsertContactRequestRequest request => System.Text.Json.JsonSerializer.Serialize(new object?[]
        {
            request.FirstName, request.LastName, request.Company, request.Email,
            request.Telephone, request.Country, request.MessageContent
        }),
        _ => throw new InvalidOperationException("Unexpected Contact fixture type.")
    };
}

internal sealed record ContactBodyRead(string Kind, object? Model, string[] Keys, int ErrorCount,
    string[] ExceptionTypes, string[] Messages);

#nullable disable
internal sealed class SourceMessage
{
    public int Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Company { get; set; }
    public string Email { get; set; }
    public string Telephone { get; set; }
    public string Country { get; set; }
    public string MessageContent { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
#nullable restore
