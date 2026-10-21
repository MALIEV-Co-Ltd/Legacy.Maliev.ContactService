using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Legacy.Maliev.ContactService.Api.Logging;

/// <summary>Writes Contact's source-bound private diagnostic console metadata.</summary>
public sealed class ContactDiagnosticConsoleFormatter : ConsoleFormatter
{
    /// <summary>Gets the application-owned formatter name.</summary>
    public const string FormatterName = "contact-private-diagnostic-json";

    /// <summary>Initializes the application-owned formatter.</summary>
    public ContactDiagnosticConsoleFormatter() : base(FormatterName) { }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (entry.LogLevel is not (LogLevel.Warning or LogLevel.Error or LogLevel.Critical)) return;
        var assembly = Assembly.GetEntryAssembly();
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["severity"] = entry.LogLevel == LogLevel.Critical ? "CRITICAL" : entry.LogLevel == LogLevel.Error ? "ERROR" : "WARNING",
            ["occurredAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["logger"] = Identifier(entry.Category, 256) ? entry.Category : "ConfiguredLogger",
            ["eventId"] = entry.EventId.Id,
            ["message"] = "Application diagnostic; see event, exception and trace metadata",
            ["service"] = SafeMetadata(assembly?.GetName().Name),
            ["deploymentVersion"] = SafeMetadata(assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        };
        if (entry.Exception is { } exception)
        {
            payload.Add("exceptionType", SafeReflectionMetadata(exception.GetType().FullName));
            payload.Add("innerExceptionType", SafeReflectionMetadata(exception.InnerException?.GetType().FullName));
            payload.Add("sourceLocation", SafeReflectionMetadata(exception.TargetSite is { } method
                ? method.DeclaringType?.FullName + "." + method.Name : null));
        }
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity && activity.TraceId != default)
        {
            payload.Add("traceId", activity.TraceId.ToHexString());
            payload.Add("spanId", activity.SpanId.ToHexString());
        }
        AddFields(payload, entry.State);
        if (scopes is not null)
        {
            var stop = new ScopeLimitException();
            int count = 0;
            try
            {
                scopes.ForEachScope((scope, values) =>
                {
                    AddFields(values, scope);
                    if (++count == 16) throw stop;
                }, payload);
            }
            catch (ScopeLimitException budgetException) when (ReferenceEquals(budgetException, stop)) { }
        }
        writer.WriteLine(JsonSerializer.Serialize(payload));
    }

    private static void AddFields(Dictionary<string, object?> payload, object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
        foreach (var field in fields.Take(64))
        {
            if (field.Key is null || payload.ContainsKey(field.Key)) continue;
            if (field.Key is "StatusCode" or "ElapsedMs" or "AttemptCount" && field.Value is int or long)
                payload.Add(field.Key, field.Value);
            else if (field.Key is "EventName" or "Dependency" or "Operation" or "Method"
                && field.Value is string code && Identifier(code, 96))
                payload.Add(field.Key, code);
            else if (field.Key is "TraceId" or "SpanId" && field.Value is string trace
                && trace.Length is >= 16 and <= 32 && trace.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                payload.Add(field.Key, trace);
            else if (field.Key == "Synthetic" && field.Value is bool synthetic)
                payload.Add(field.Key, synthetic);
            else if (field.Key == "DiagnosticId" && field.Value is string nonce && Guid.TryParseExact(nonce, "N", out var diagnostic))
                payload.Add(field.Key, diagnostic.ToString("N"));
            else if (field.Key == "CorrelationId" && field.Value is string correlation && Guid.TryParse(correlation, out var id))
                payload.Add(field.Key, id.ToString("N"));
            // Frozen modern middleware records type/incident metadata as state, without passing exception objects.
            else if (field.Key == "ExceptionType" && field.Value is string type && Identifier(type, 256))
                payload.Add(field.Key, type);
            else if (field.Key == "IncidentId" && field.Value is string incident && incident.Length is >= 1 and <= 128
                && incident.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ':' or '.'))
                payload.Add(field.Key, incident);
        }
    }

    private static bool Identifier(string? text, int maximum) => text is { Length: >= 1 } && text.Length <= maximum
        && char.IsAsciiLetter(text[0])
        && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    private static string? SafeMetadata(string? text) => text is { Length: >= 1 and <= 256 }
        && char.IsAsciiLetterOrDigit(text[0])
        && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' or '+' or '`') ? text : null;

    // Trusted reflection names include async/compiler-generated and constructed generic punctuation.
    // This validator is never used for caller state, category or formatted message text.
    private static string? SafeReflectionMetadata(string? text) => text is { Length: >= 1 and <= 2048 }
        && text.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '.' or '-' or '+' or '`' or '<' or '>' or '[' or ']' or ',' or '=' or ' ' or '$' or '|' or '&' or '*')
        ? text : null;

    private sealed class ScopeLimitException : Exception;
}
