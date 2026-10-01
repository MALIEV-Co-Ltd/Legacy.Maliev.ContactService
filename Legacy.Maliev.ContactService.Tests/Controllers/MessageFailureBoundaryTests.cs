using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Legacy.Maliev.ContactService.Api.Authorization;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

public sealed class MessageFailureBoundaryTests
{
    [Fact]
    public async Task AuthenticatedMessageFailure_UsesRedactedRouteAndIncidentWithoutContactData()
    {
        const string sensitive = "private-contact@example.com";
        const string literalId = "987654321";
        var logs = new CapturingLogProvider();
        await using var factory = new FailureFactory(logs, sensitive);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/Messages/{literalId}?search={Uri.EscapeDataString(sensitive)}");
        request.Headers.Add("X-Correlation-ID", "message-incident-123");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("message-incident-123", response.Headers.GetValues("X-Correlation-ID").Single());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string traceId = body.RootElement.GetProperty("traceId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.Equal(500, body.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal("An internal server error occurred", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("details").ValueKind);
        var incident = Assert.Single(logs.Messages, message => message.Contains("UnhandledRequestFailure", StringComparison.Ordinal));
        Assert.Contains("Path=Messages/{messageId:int}", incident, StringComparison.Ordinal);
        Assert.Contains($"IncidentId={traceId}", incident, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(literalId, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, string.Join('\n', logs.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(literalId, string.Join('\n', logs.Messages), StringComparison.Ordinal);
    }

    private sealed class FailureFactory(CapturingLogProvider logs, string sensitive) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("ConnectionStrings:ContactRequestDbContext",
                "Host=localhost;Database=unused;Username=unused");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IContactService>();
                services.AddSingleton<IContactService>(new ThrowingContactService(sensitive));
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("sub", "message-failure-test"),
                new Claim("permission", ContactRequestPermissions.ContactRequestsRead),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    private sealed class ThrowingContactService(string sensitive) : IContactService
    {
        public Task<PaginatedContactRequestResponse> GetPaginatedAsync(
            ContactRequestSortType? sort, string? search, int? index, int? size, CancellationToken cancellationToken) =>
            throw new Exception(sensitive);

        public Task<ContactRequestResponse?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
            throw new Exception(sensitive);

        public Task<ContactRequestResponse> CreateAsync(UpsertContactRequestRequest request, CancellationToken cancellationToken) =>
            throw new Exception(sensitive);

        public Task<bool> UpdateAsync(int id, UpsertContactRequestRequest request, CancellationToken cancellationToken) =>
            throw new Exception(sensitive);

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken) =>
            throw new Exception(sensitive);
    }

    private sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();
        public IReadOnlyCollection<string> Messages => messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
