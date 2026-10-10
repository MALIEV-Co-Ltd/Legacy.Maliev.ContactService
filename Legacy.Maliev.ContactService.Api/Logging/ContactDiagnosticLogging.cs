using Microsoft.Extensions.Logging.Console;

namespace Legacy.Maliev.ContactService.Api.Logging;

/// <summary>Registers Contact's private console diagnostics after the shared service defaults.</summary>
public static class ContactDiagnosticLogging
{
    /// <summary>Selects the application-owned formatter and preserves the original console warning filter.</summary>
    /// <param name="builder">The application logging builder.</param>
    /// <returns>The same logging builder.</returns>
    public static ILoggingBuilder AddContactPrivateDiagnostics(this ILoggingBuilder builder)
    {
        builder.AddConsoleFormatter<ContactDiagnosticConsoleFormatter, ConsoleFormatterOptions>();
        builder.AddConsole(options => options.FormatterName = ContactDiagnosticConsoleFormatter.FormatterName);
        builder.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Warning);
        return builder;
    }
}
