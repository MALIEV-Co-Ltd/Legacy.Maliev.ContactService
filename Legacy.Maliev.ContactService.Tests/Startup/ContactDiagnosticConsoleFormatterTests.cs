using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Legacy.Maliev.ContactService.Api.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ContactService.Tests.Startup;

[Collection(nameof(ContactConsoleCaptureCollection))]
public sealed class ContactDiagnosticConsoleFormatterTests
{
    [Theory]
    [InlineData(LogLevel.Trace, null)]
    [InlineData(LogLevel.Debug, null)]
    [InlineData(LogLevel.Information, null)]
    [InlineData(LogLevel.None, null)]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void PrivateFormatter_LevelGateNeverInvokesArbitraryMessage(LogLevel level, string? severity)
    {
        var line = Write(level, new Dictionary<string, object?>());
        if (severity is null)
        {
            Assert.Empty(line);
            return;
        }
        using var document = JsonDocument.Parse(line);
        Assert.Equal(severity, document.RootElement.GetProperty("severity").GetString());
        Assert.Equal("Application diagnostic; see event, exception and trace metadata", document.RootElement.GetProperty("message").GetString());
        Assert.Equal(1901, document.RootElement.GetProperty("eventId").GetInt32());
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(document.RootElement.GetProperty("occurredAtUtc").GetString()!, CultureInfo.InvariantCulture).Offset);
    }

    [Fact]
    public void PrivateFormatter_PreservesRuntimeExceptionActivityAndDeploymentProvenance()
    {
        using var activity = new Activity("SourceMetadata").SetIdFormat(ActivityIdFormat.W3C).Start();
        Exception failure;
        try { ThrowSourceException(); throw new UnreachableException(); }
        catch (InvalidOperationException exception) { failure = exception; }
        var line = Write(LogLevel.Error, new Dictionary<string, object?> { ["exceptionType"] = "forged", ["service"] = "forged" }, failure);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("System.InvalidOperationException", root.GetProperty("exceptionType").GetString());
        Assert.Equal("System.ArgumentException", root.GetProperty("innerExceptionType").GetString());
        Assert.EndsWith(".ThrowSourceException", root.GetProperty("sourceLocation").GetString());
        Assert.Equal(activity.TraceId.ToHexString(), root.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), root.GetProperty("spanId").GetString());
        var entryAssembly = Assembly.GetEntryAssembly();
        Assert.NotNull(entryAssembly);
        Assert.Equal(entryAssembly.GetName().Name, root.GetProperty("service").GetString());
        Assert.Equal(entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            root.GetProperty("deploymentVersion").GetString());
        Assert.DoesNotContain("private@example.invalid", line);
    }

    [Fact]
    public async Task PrivateFormatter_PreservesAsyncAndGenericReflectionSourceLocations()
    {
        Exception failure;
        try { await ThrowAsyncSourceException(); throw new UnreachableException(); }
        catch (InvalidOperationException exception) { failure = exception; }
        var expected = failure.TargetSite!.DeclaringType!.FullName + "." + failure.TargetSite.Name;
        Assert.Contains("<", expected);
        using var asyncDocument = JsonDocument.Parse(Write(LogLevel.Error, new object(), failure));
        Assert.Equal(expected, asyncDocument.RootElement.GetProperty("sourceLocation").GetString());
        try { GenericSourceProbe<string>.Throw(); throw new UnreachableException(); }
        catch (InvalidOperationException exception) { failure = exception; }
        expected = failure.TargetSite!.DeclaringType!.FullName + "." + failure.TargetSite.Name;
        Assert.Contains("`", expected);
        using var genericDocument = JsonDocument.Parse(Write(LogLevel.Error, new object(), failure));
        Assert.Equal(expected, genericDocument.RootElement.GetProperty("sourceLocation").GetString());
        Assert.DoesNotContain("private@example.invalid", asyncDocument.RootElement.GetRawText());
        Assert.DoesNotContain("private@example.invalid", genericDocument.RootElement.GetRawText());
    }

    [Fact]
    public void PrivateFormatter_PreservesOriginalSafeFieldsAndModernTypeIncidentAdaptation()
    {
        var nonce = Guid.NewGuid();
        var fields = new Dictionary<string, object?>
        {
            ["EventName"] = "SourceWarning",
            ["Dependency"] = "ContactDb",
            ["Operation"] = "Readiness",
            ["Method"] = "GET",
            ["StatusCode"] = 503,
            ["ElapsedMs"] = 12L,
            ["AttemptCount"] = 2,
            ["Synthetic"] = false,
            ["DiagnosticId"] = nonce.ToString("N"),
            ["CorrelationId"] = nonce.ToString("D"),
            ["TraceId"] = "0123456789abcdef0123456789abcdef",
            ["SpanId"] = "0123456789abcdef",
            ["ExceptionType"] = "PostgresException",
            ["IncidentId"] = "0H_SOURCE:0001"
        };
        using var document = JsonDocument.Parse(Write(LogLevel.Warning, fields));
        var root = document.RootElement;
        Assert.Equal(nonce.ToString("N"), root.GetProperty("CorrelationId").GetString());
        Assert.Equal(nonce.ToString("N"), root.GetProperty("DiagnosticId").GetString());
        Assert.False(root.GetProperty("Synthetic").GetBoolean());
        Assert.Equal(503, root.GetProperty("StatusCode").GetInt32());
        Assert.Equal(12L, root.GetProperty("ElapsedMs").GetInt64());
        foreach (var field in fields.Where(field => field.Key != "CorrelationId"))
            Assert.Equal(JsonSerializer.Serialize(field.Value), root.GetProperty(field.Key).GetRawText());
    }

    [Theory]
    [InlineData("EventName")]
    [InlineData("Path")]
    [InlineData("RequestPath")]
    [InlineData("CustomerEmail")]
    [InlineData("CorrelationId")]
    [InlineData("DiagnosticId")]
    [InlineData("ExceptionType")]
    [InlineData("IncidentId")]
    public void PrivateFormatter_RejectsUnknownOrInvalidScalarFields(string key)
    {
        var line = Write(LogLevel.Warning, new Dictionary<string, object?> { [key] = "private@example.invalid", ["StatusCode"] = "503" });
        using var document = JsonDocument.Parse(line);
        Assert.False(document.RootElement.TryGetProperty(key, out _));
        Assert.False(document.RootElement.TryGetProperty("StatusCode", out _));
        Assert.DoesNotContain("private@example.invalid", line);
    }

    [Fact]
    public void PrivateFormatter_StateWinsWithoutDuplicateKeysAndScopeProcessingIsBounded()
    {
        var scopes = new LoggerExternalScopeProvider();
        var handles = new List<IDisposable>();
        try
        {
            for (int index = 0; index < 17; index++)
                handles.Add(scopes.Push(new Dictionary<string, object?> { ["EventName"] = "ForgedScope", ["Dependency"] = index == 16 ? "BeyondBudget" : null }));
            var fields = new[]
            {
                new KeyValuePair<string, object?>(null!, "private@example.invalid"),
                new KeyValuePair<string, object?>("EventName", "OriginalState"),
                new KeyValuePair<string, object?>("message", "private@example.invalid")
            };
            using var document = JsonDocument.Parse(Write(LogLevel.Warning, fields, scopes: scopes));
            var root = document.RootElement;
            Assert.Equal("OriginalState", root.GetProperty("EventName").GetString());
            Assert.False(root.TryGetProperty("Dependency", out _));
            Assert.Equal(root.EnumerateObject().Count(), root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count());
        }
        finally { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    [Fact]
    public void PrivateFormatter_StateEnumerationAndUntrustedCategoryFailClosed()
    {
        var fields = Enumerable.Range(0, 64).Select(index => new KeyValuePair<string, object?>("Unknown" + index, null))
            .Append(new KeyValuePair<string, object?>("EventName", "BeyondBudget"));
        using var document = JsonDocument.Parse(Write(LogLevel.Warning, fields, category: "private@example.invalid"));
        Assert.False(document.RootElement.TryGetProperty("EventName", out _));
        Assert.Equal("ConfiguredLogger", document.RootElement.GetProperty("logger").GetString());
        using var missingCategory = JsonDocument.Parse(Write(LogLevel.Warning, fields, category: null!));
        Assert.Equal("ConfiguredLogger", missingCategory.RootElement.GetProperty("logger").GetString());
    }

    [Fact]
    public void PrivateFormatter_NullWriterIsRejected()
    {
        var entry = new LogEntry<string>(LogLevel.Warning, "Contact.Source", new EventId(1901), "", null, (_, _) => "");
        Assert.Throws<ArgumentNullException>(() => new ContactDiagnosticConsoleFormatter().Write(entry, null, null!));
    }

    [Fact]
    public void PrivateRegistration_SelectsOwnedWarningFormatterAndPreservesOtherProviders()
    {
        var services = new ServiceCollection();
        var marker = new RetainedProvider();
        services.AddLogging(builder =>
        {
            builder.AddProvider(marker);
            builder.AddContactPrivateDiagnostics();
        });
        using var provider = services.BuildServiceProvider();
        Assert.Equal(ContactDiagnosticConsoleFormatter.FormatterName,
            provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        Assert.Contains(provider.GetServices<ILoggerProvider>(), instance => ReferenceEquals(instance, marker));
        var filters = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        Assert.Single(filters.Rules, rule => rule.ProviderName == typeof(ConsoleLoggerProvider).FullName
            && rule.CategoryName is null && rule.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task ConfiguredContactHost_ConsoleQueueFiltersInformationAndRedactsWarningAndHttpFailure()
    {
        const string messageSentinel = "console-message@example.invalid";
        const string stateSentinel = "console-state@example.invalid";
        const string scopeSentinel = "console-scope@example.invalid";
        const string exceptionSentinel = "console-exception@example.invalid";
        const string literalId = "24681357";
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var synchronizedOutput = TextWriter.Synchronized(output);
        var retained = new HostRetainedProvider();
        string? traceIdentifier = null;
        // This collection disables parallel execution because Console's writers are process-wide.
        // Capture the real ConsoleLoggerProvider queue; do not invoke the formatter directly here.
        Console.SetOut(synchronizedOutput);
        Console.SetError(synchronizedOutput);
        try
        {
            await using var factory = new ConsoleHostFactory(retained, exceptionSentinel);
            using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            });
            Assert.Contains(factory.Services.GetServices<ILoggerProvider>(), provider => ReferenceEquals(provider, retained));
            Assert.Single(factory.Services.GetServices<ILoggerProvider>(), provider => provider is ConsoleLoggerProvider);
            Assert.Equal(ContactDiagnosticConsoleFormatter.FormatterName,
                factory.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);
            var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Contact.ConsoleHostProbe");
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["CustomerEmail"] = scopeSentinel,
                ["Operation"] = "ConsoleHostProbe",
            }))
            {
                foreach (var level in new[] { LogLevel.Information, LogLevel.Warning, LogLevel.Error })
                    logger.Log(level, new EventId(1910 + (int)level, "ConsoleHostProbe"),
                        new Dictionary<string, object?>
                        {
                            ["EventName"] = "ConsoleHostProbe",
                            ["CustomerEmail"] = stateSentinel,
                            ["StatusCode"] = 503,
                        }, new InvalidOperationException(exceptionSentinel), (_, _) => messageSentinel);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/Messages/{literalId}?search={Uri.EscapeDataString(stateSentinel)}");
            request.Headers.Add("X-Correlation-ID", "contact-console-host-incident");
            using var response = await client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(500, body.RootElement.GetProperty("statusCode").GetInt32());
            Assert.Equal("An internal server error occurred", body.RootElement.GetProperty("error").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("details").ValueKind);
            traceIdentifier = body.RootElement.GetProperty("traceId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(traceIdentifier));
            Assert.Equal("contact-console-host-incident", response.Headers.GetValues("X-Correlation-ID").Single());
            foreach (var sentinel in new[] { messageSentinel, stateSentinel, scopeSentinel, exceptionSentinel, literalId })
                Assert.DoesNotContain(sentinel, body.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.Contains(LogLevel.Information, retained.ProbeLevels);
            Assert.Contains(LogLevel.Warning, retained.ProbeLevels);
            Assert.Contains(LogLevel.Error, retained.ProbeLevels);
            // Factory disposal runs before Console restoration, draining the real provider's queue.
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        var text = output.ToString();
        foreach (var sentinel in new[] { messageSentinel, stateSentinel, scopeSentinel, exceptionSentinel, literalId })
            Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        var events = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith('{'))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray();
        var probes = events.Where(value => value.TryGetProperty("EventName", out var name)
            && name.GetString() == "ConsoleHostProbe").ToArray();
        Assert.Equal(2, probes.Length);
        Assert.Equal(new[] { "ERROR", "WARNING" }, probes.Select(value => value.GetProperty("severity").GetString()).OrderBy(value => value));
        foreach (var probe in probes)
        {
            Assert.Equal("ConsoleHostProbe", probe.GetProperty("Operation").GetString());
            Assert.Equal(503, probe.GetProperty("StatusCode").GetInt32());
            Assert.Equal(typeof(InvalidOperationException).FullName, probe.GetProperty("exceptionType").GetString());
            Assert.False(probe.TryGetProperty("CustomerEmail", out _));
        }
        var failure = Assert.Single(events, value => value.TryGetProperty("EventName", out var name)
            && name.GetString() == "UnhandledRequestFailure");
        Assert.Equal("CRITICAL", failure.GetProperty("severity").GetString());
        Assert.Equal(traceIdentifier, failure.GetProperty("IncidentId").GetString());
        Assert.Equal(nameof(Exception), failure.GetProperty("ExceptionType").GetString());
        Assert.False(failure.TryGetProperty("Path", out _));
    }

    private sealed class ConsoleHostFactory(HostRetainedProvider retained, string sensitive)
        : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        private readonly System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048);

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.AddProvider(retained));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Legacy.Maliev.ContactService.Application.Interfaces.IContactService>();
                services.AddSingleton<Legacy.Maliev.ContactService.Application.Interfaces.IContactService>(new ConsoleFailureService(sensitive));
                services.AddAuthentication("ConsoleProbe").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ConsoleProbeAuthentication>("ConsoleProbe", _ => { });
            });
        }

        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            // Ephemeral trust and unreachable synthetic database: no data or dependency-readiness proof.
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ContactRequestDbContext"] = new Npgsql.NpgsqlConnectionStringBuilder
                {
                    Host = System.Net.IPAddress.Loopback.ToString(),
                    Port = 1,
                    Database = "console_probe",
                    Pooling = false,
                }.ConnectionString,
                ["Jwt:PublicKey"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "https://console-probe.example.invalid",
                ["Jwt:Audience"] = "https://console-probe.example.invalid",
                ["Cache:RedisEnabled"] = "false",
                ["Logging:LogLevel:Default"] = "Information",
            }));
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) rsa.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); }
            finally { rsa.Dispose(); }
        }
    }

    private sealed class ConsoleProbeAuthentication(
        IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new System.Security.Claims.Claim("sub", "console-host-probe"),
                new System.Security.Claims.Claim("permission", Legacy.Maliev.ContactService.Api.Authorization.ContactRequestPermissions.ContactRequestsRead),
            };
            var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "ConsoleProbe"));
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(
                new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, "ConsoleProbe")));
        }
    }

    private sealed class ConsoleFailureService(string sensitive) : Legacy.Maliev.ContactService.Application.Interfaces.IContactService
    {
        public Task<Legacy.Maliev.ContactService.Application.Models.PaginatedContactRequestResponse> GetPaginatedAsync(
            Legacy.Maliev.ContactService.Application.Models.ContactRequestSortType? sort, string? search, int? index, int? size, CancellationToken cancellationToken) => throw new Exception(sensitive);
        public Task<Legacy.Maliev.ContactService.Application.Models.ContactRequestResponse?> GetByIdAsync(int id, CancellationToken cancellationToken) => throw new Exception(sensitive);
        public Task<Legacy.Maliev.ContactService.Application.Models.ContactRequestResponse> CreateAsync(Legacy.Maliev.ContactService.Application.Models.UpsertContactRequestRequest request, CancellationToken cancellationToken) => throw new Exception(sensitive);
        public Task<bool> UpdateAsync(int id, Legacy.Maliev.ContactService.Application.Models.UpsertContactRequestRequest request, CancellationToken cancellationToken) => throw new Exception(sensitive);
        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken) => throw new Exception(sensitive);
    }

    private sealed class HostRetainedProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<LogLevel> ProbeLevels { get; } = new();
        public ILogger CreateLogger(string categoryName) => new HostRetainedLogger(this, categoryName);
        public void Dispose() { }

        private sealed class HostRetainedLogger(HostRetainedProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "Contact.ConsoleHostProbe") provider.ProbeLevels.Enqueue(logLevel);
            }
        }
    }


    private sealed class RetainedProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
        public void Dispose() { }
    }

    private static string Write(LogLevel level, object state, Exception? failure = null, IExternalScopeProvider? scopes = null, string category = "Contact.Source")
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var entry = new LogEntry<object>(level, category, new EventId(1901), state, failure,
            (_, _) => throw new InvalidOperationException("Arbitrary formatter must never be invoked"));
        new ContactDiagnosticConsoleFormatter().Write(entry, scopes, writer);
        return writer.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSourceException() => throw new InvalidOperationException("private@example.invalid", new ArgumentException("private@example.invalid"));

    private static async Task ThrowAsyncSourceException()
    {
        await Task.Yield();
        throw new InvalidOperationException("private@example.invalid");
    }

    private static class GenericSourceProbe<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw() => throw new InvalidOperationException("private@example.invalid");
    }
}

[CollectionDefinition(nameof(ContactConsoleCaptureCollection), DisableParallelization = true)]
public sealed class ContactConsoleCaptureCollection { }
