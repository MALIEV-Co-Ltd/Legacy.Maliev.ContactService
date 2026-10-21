using System.Buffers;
using System.Text;
using Legacy.Maliev.ContactService.Application.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Legacy.Maliev.ContactService.Api.Serialization;

/// <summary>Adds original Message body admission for the Contact write request.</summary>
/// <param name="loggers">The application logger factory.</param>
/// <param name="pools">The application object pool provider.</param>
public sealed class ContactOriginalBodyMvcOptionsSetup(ILoggerFactory loggers, ObjectPoolProvider pools) : IConfigureOptions<MvcOptions>
{
    /// <inheritdoc />
    public void Configure(MvcOptions options)
    {
        if (options.InputFormatters.OfType<ContactOriginalBodyInputFormatter>().Any()) return;
        var source = new MvcNewtonsoftJsonOptions();
        source.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        source.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        // Keep stock MVC defaults local to the input reader. The existing STJ
        // formatter, output, DTO validation and documentation remain registered.
        var logger = new SourceBodyLogger(loggers.CreateLogger(
            "Microsoft.AspNetCore.Mvc.Formatters.NewtonsoftJsonInputFormatter"));
        options.InputFormatters.Insert(0, new ContactOriginalBodyInputFormatter(
            new BufferedSourceFormatter(logger, source, pools, options)));
    }

    private sealed class SourceBodyLogger(ILogger target) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            target.BeginScope("Contact source body admission");
        public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (target.IsEnabled(logLevel))
                target.Log(logLevel, eventId, "SourceBodyAdmissionFailure ExceptionType={ExceptionType}",
                    exception?.GetType().Name ?? "InputFormatter");
        }
    }

    private sealed class BufferedSourceFormatter(
        ILogger logger, MvcNewtonsoftJsonOptions json, ObjectPoolProvider pools, MvcOptions mvc)
        : NewtonsoftJsonInputFormatter(logger, json.SerializerSettings, ArrayPool<char>.Shared, pools, mvc, json)
    {
        private const long DefaultRequestLimit = 30_000_000;
        private readonly int memoryThreshold = json.InputFormatterMemoryBufferThreshold;

        public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
        {
            // TextInputFormatter selected the supported encoding before this method.
            // Drain asynchronously before the stock reader performs synchronous I/O.
            var request = context.HttpContext.Request;
            var original = request.Body;
            var originalPosition = original.CanSeek ? original.Position : (long?)null;
            var limit = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize ?? DefaultRequestLimit;
            await using var buffered = new FileBufferingReadStream(original, memoryThreshold, limit,
                static () => Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath());
            try
            {
                request.Body = buffered;
                await buffered.DrainAsync(context.HttpContext.RequestAborted);
                buffered.Position = 0;
                return await base.ReadRequestBodyAsync(context, encoding);
            }
            finally
            {
                request.Body = original;
                if (originalPosition.HasValue) original.Position = originalPosition.Value;
            }
        }
    }
}

internal sealed class ContactOriginalBodyInputFormatter(NewtonsoftJsonInputFormatter source)
    : IInputFormatter, IInputFormatterExceptionPolicy
{
    // Subclassing the inner reader must not widen MVC's selected exception policy.
    public InputFormatterExceptionPolicy ExceptionPolicy => InputFormatterExceptionPolicy.MalformedInputExceptions;

    public bool CanRead(InputFormatterContext context) =>
        context.ModelType == typeof(UpsertContactRequestRequest) && source.CanRead(context);

    public async Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
    {
        if (context.ModelType != typeof(UpsertContactRequestRequest))
            throw new InvalidOperationException("The Contact source body formatter received an unowned contract.");

        // This metadata describes only the inert source object. The projected
        // request still undergoes normal MVC validation with its six length limits.
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(OriginalMessage));
        var state = new ModelStateDictionary(context.ModelState.MaxAllowedErrors);
        var originalContext = new InputFormatterContext(context.HttpContext, context.ModelName, state, metadata,
            context.ReaderFactory, context.TreatEmptyInputAsDefaultValue);
        var result = await source.ReadAsync(originalContext);
        if (result.HasError)
        {
            // Do not reflect an input property, value or caller-controlled prefix
            // into model errors. Preserve unsupported-charset classification.
            var key = context.ModelName is "" or "item" or "request" ? context.ModelName : "$";
            if (state.Values.SelectMany(value => value.Errors).Any(error => error.Exception is UnsupportedContentTypeException))
                context.ModelState.TryAddModelError(key,
                    new UnsupportedContentTypeException("The request content type is unsupported."), context.Metadata);
            else
                context.ModelState.TryAddModelError(key, "The request body contains an invalid value.");
            return InputFormatterResult.Failure();
        }
        if (!result.IsModelSet) return result;
        if (result.Model is null) return InputFormatterResult.Success(null);
        var value = (OriginalMessage)result.Model;
        return InputFormatterResult.Success(new UpsertContactRequestRequest(
            value.FirstName, value.LastName, value.Company, value.Email,
            value.Telephone, value.Country, value.MessageContent));
    }

    // Bind the complete recognized source envelope before projecting. Ignored
    // server-owned fields must still reject their original typed parse failures.
    private sealed class OriginalMessage
    {
        public int Id { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Company { get; set; }
        public string? Email { get; set; }
        public string? Telephone { get; set; }
        public string? Country { get; set; }
        public string? MessageContent { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }
}
