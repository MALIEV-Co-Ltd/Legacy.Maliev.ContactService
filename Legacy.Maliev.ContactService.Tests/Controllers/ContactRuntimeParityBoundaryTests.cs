using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.ContactService.Api.Authorization;
using Legacy.Maliev.ContactService.Data;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

/// <summary>Real production RS256/HTTP/PG contracts using disposable synthetic data only.</summary>
public sealed class ContactRuntimeParityBoundaryTests(ContactRuntimePostgresFixture postgres)
    : IClassFixture<ContactRuntimePostgresFixture>
{
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task Pagination_EmitsSourceTotalRecords_NotRenamedTotalItems(string route)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        await CreateAsync(client, route, "first");
        await CreateAsync(client, route, "second");

        using var response = await client.GetAsync(route + "?index=1&size=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("totalRecords", out var count),
            "Latest committed source pagination exposes totalRecords.");
        Assert.Equal(2, count.GetInt32());
        Assert.False(body.RootElement.TryGetProperty("totalItems", out _));
        Assert.Equal(1, body.RootElement.GetProperty("pageIndex").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("totalPages").GetInt32());
        Assert.False(body.RootElement.GetProperty("hasPreviousPage").GetBoolean());
        Assert.True(body.RootElement.GetProperty("hasNextPage").GetBoolean());
        Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task Search_WhitespaceQueryUsesMvcNullDefault(string route)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var spaced = await CreateAsync(client, route, "literal space");
        var unspaced = await CreateAsync(client, route, "nospace");

        using var response = await client.GetAsync(route + "?search=%20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { spaced, unspaced }, body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32()).ToArray());
    }

    [Theory]
    [InlineData("/Messages", "")]
    [InlineData("/messages/v1/contact-requests", "")]
    [InlineData("/Messages", "?search=")]
    [InlineData("/messages/v1/contact-requests", "?search=")]
    public async Task Search_AbsentAndEmptyQueriesUseUnfilteredDefault(string route, string query)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var first = await CreateAsync(client, route, "first");
        var second = await CreateAsync(client, route, "second");
        using var response = await client.GetAsync(route + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { first, second }, body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32()).ToArray());
    }

    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task Crud_PersistsServerIdentityAndDates_AndPreservesNullOmission(string route)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var created = await client.PostAsJsonAsync(route, new
        {
            Id = 999999,
            FirstName = "Synthetic",
            MessageContent = "fixture",
            CreatedDate = "1900-01-01T00:00:00",
            ModifiedDate = "1900-01-01T00:00:00",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.NotEqual(999999, id);
        Assert.Equal($"/Messages/{id}", created.Headers.Location?.AbsolutePath);
        Assert.False(body.RootElement.TryGetProperty("lastName", out _));
        Assert.False(body.RootElement.TryGetProperty("email", out _));
        Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime,
            body.RootElement.GetProperty("createdDate").GetDateTime());

        await using (var db = postgres.CreateContext())
        {
            var stored = await db.Messages.AsNoTracking().SingleAsync();
            Assert.Equal(id, stored.Id);
            Assert.Equal("Synthetic", stored.FirstName);
            Assert.Null(stored.LastName);
            Assert.Null(stored.Email);
            Assert.Equal(DateTimeKind.Unspecified, stored.CreatedDate!.Value.Kind);
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var updated = await client.PutAsJsonAsync(route + "/" + id,
            new { FirstName = (string?)null, MessageContent = "updated", Country = "TH" });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var read = await client.GetAsync(route + "/" + id);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.False(readBody.RootElement.TryGetProperty("firstName", out _));
        Assert.Equal("TH", readBody.RootElement.GetProperty("country").GetString());
        Assert.Equal(ContactRuntimeFactory.InitialTime.UtcDateTime,
            readBody.RootElement.GetProperty("createdDate").GetDateTime());
        Assert.Equal(ContactRuntimeFactory.InitialTime.AddMinutes(1).UtcDateTime,
            readBody.RootElement.GetProperty("modifiedDate").GetDateTime());

        using var deleted = await client.DeleteAsync(route + "/" + id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await client.GetAsync(route + "/" + id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var emptyDb = postgres.CreateContext();
        Assert.Empty(await emptyDb.Messages.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-issuer", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-audience", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("no-permission", HttpStatusCode.Forbidden)]
    public async Task ProductionAuthentication_DeniesInvalidCallerBeforePersistence(string scenario, HttpStatusCode expected)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient(scenario);
        using var response = await client.PostAsJsonAsync("/Messages", new { MessageContent = "synthetic-denied" });

        Assert.Equal(expected, response.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.Messages.CountAsync());
    }

    [Theory]
    [InlineData("%", "literal%")]
    [InlineData("_", "literal_")]
    [InlineData("\\", "literal\\")]
    public async Task Search_WildcardsAreLiteralUnderPostgres(string search, string matched)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var expected = await CreateAsync(client, "/Messages", matched);
        await CreateAsync(client, "/Messages", "unmatched");
        using var response = await client.GetAsync("/Messages?search=" + Uri.EscapeDataString(search));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, Assert.Single(body.RootElement.GetProperty("items").EnumerateArray())
            .GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Pagination_EmptyAndOutOfRangeKeepLegacyNotFound()
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        using var empty = await client.GetAsync("/Messages");
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        await CreateAsync(client, "/Messages", "one");
        using var beyond = await client.GetAsync("/Messages?index=2&size=1");
        Assert.Equal(HttpStatusCode.NotFound, beyond.StatusCode);
    }

    [Theory]
    [InlineData("/Messages", 0)]
    [InlineData("/Messages", -1)]
    [InlineData("/messages", 0)]
    [InlineData("/messages", -1)]
    public async Task LegacyUpdate_MissingNonpositiveIdentifierPreservesSourceNotFound(string route, int missingId)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var id = await CreateAsync(client, route, "source-must-remain");
        await using var beforeDb = postgres.CreateContext();
        var before = await beforeDb.Messages.AsNoTracking().SingleAsync();

        using var response = await client.PutAsJsonAsync($"{route}/{missingId}", new { MessageContent = "must-not-persist" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var afterDb = postgres.CreateContext();
        var after = await afterDb.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(id, after.Id);
        Assert.Equal(before.MessageContent, after.MessageContent);
        Assert.Equal(before.CreatedDate, after.CreatedDate);
        Assert.Equal(before.ModifiedDate, after.ModifiedDate);
    }

    [Theory]
    [InlineData("/Messages", 0, HttpStatusCode.NotFound)]
    [InlineData("/Messages", -1, HttpStatusCode.NotFound)]
    [InlineData("/messages/v1/contact-requests", 0, HttpStatusCode.BadRequest)]
    [InlineData("/messages/v1/contact-requests", -1, HttpStatusCode.BadRequest)]
    public async Task Update_NonpositiveIdentifierPreservesRouteStatusWithoutChangingStoredMessages(string route, int invalidId, HttpStatusCode expectedStatus)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var id = await CreateAsync(client, route, "original");
        await using var beforeDb = postgres.CreateContext();
        var before = await beforeDb.Messages.AsNoTracking().SingleAsync();

        using var response = await client.PutAsJsonAsync($"{route}/{invalidId}", new { MessageContent = "must-not-persist" });

        Assert.Equal(expectedStatus, response.StatusCode);
        await using var afterDb = postgres.CreateContext();
        var after = await afterDb.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(id, after.Id);
        Assert.Equal(before.MessageContent, after.MessageContent);
        Assert.Equal(before.CreatedDate, after.CreatedDate);
        Assert.Equal(before.ModifiedDate, after.ModifiedDate);
    }

    private static async Task<int> CreateAsync(HttpClient client, string route, string content)
    {
        using var response = await client.PostAsJsonAsync(route, new { MessageContent = content });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Persistence_StaleTrackedUpdateCannotOverwriteCommittedXminVersion()
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString);
        using var client = factory.AuthenticatedClient();
        var id = await CreateAsync(client, "/Messages", "original");
        await using var first = postgres.CreateContext();
        await using var stale = postgres.CreateContext();
        var firstRepository = new ContactRequestRepository(first);
        var staleRepository = new ContactRequestRepository(stale);
        var firstRow = await firstRepository.GetByIdForUpdateAsync(id, CancellationToken.None);
        var staleRow = await staleRepository.GetByIdForUpdateAsync(id, CancellationToken.None);
        Assert.NotNull(firstRow);
        Assert.NotNull(staleRow);
        firstRow.MessageContent = "committed-first";
        await firstRepository.UpdateAsync(firstRow, CancellationToken.None);
        staleRow.MessageContent = "stale-overwrite";

        var conflict = await Assert.ThrowsAsync<Legacy.Maliev.ContactService.Application.Models.ContactRequestConcurrencyException>(() =>
            staleRepository.UpdateAsync(staleRow, CancellationToken.None));
        Assert.IsType<DbUpdateConcurrencyException>(conflict.InnerException);
        await using var read = postgres.CreateContext();
        Assert.Equal("committed-first", (await read.Messages.AsNoTracking().SingleAsync()).MessageContent);
    }

    [Fact]
    public async Task HttpConcurrentUpdates_ReturnOneConflict_NotUnknownServerFailure()
    {
        await postgres.ResetAsync();
        var gate = new ConcurrentMutationGate();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString, mutationGate: gate);
        using var first = factory.AuthenticatedClient();
        using var second = factory.AuthenticatedClient();
        var id = await CreateAsync(first, "/Messages", "original");

        var responses = await Task.WhenAll(
            first.PutAsJsonAsync("/Messages/" + id, new { MessageContent = "first-request" }),
            second.PutAsJsonAsync("/Messages/" + id, new { MessageContent = "second-request" }));
        using var response1 = responses[0];
        using var response2 = responses[1];
        await using var read = postgres.CreateContext();
        Assert.Contains((await read.Messages.AsNoTracking().SingleAsync()).MessageContent,
            new[] { "first-request", "second-request" });
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        // Proposed PostgreSQL concurrency classification, not an old-source409 claim.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    public async Task Mutation_PostCommitCacheFailure_DoesNotMisreportPersistedEffect(string operation, bool providerCancellation)
    {
        await postgres.ResetAsync();
        int? id = null;
        if (operation != "create")
        {
            await using var seedFactory = new ContactRuntimeFactory(postgres.ConnectionString);
            using var seedClient = seedFactory.AuthenticatedClient();
            id = await CreateAsync(seedClient, "/Messages", "before-mutation");
        }

        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString,
            invalidationFailure: providerCancellation ? "provider-cancellation" : "provider-error");
        using var client = factory.AuthenticatedClient();
        using var response = operation switch
        {
            "update" => await client.PutAsJsonAsync("/Messages/" + id, new { MessageContent = "committed-before-cache-failure" }),
            "delete" => await client.DeleteAsync("/Messages/" + id),
            _ => await client.PostAsJsonAsync("/Messages", new { MessageContent = "committed-before-cache-failure" }),
        };
        await using var db = postgres.CreateContext();
        var rows = await db.Messages.AsNoTracking().ToArrayAsync();
        if (operation == "delete")
        {
            Assert.Empty(rows);
        }
        else
        {
            Assert.Equal("committed-before-cache-failure", Assert.Single(rows).MessageContent);
        }

        // Caller token is not cancelled. This is controlled adapter failure after real PG commit,
        // not evidence of Redis success or authorization to implement an idempotency contract.
        Assert.Equal(operation == "create" ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
        if (operation == "create")
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(Assert.Single(rows).Id, body.RootElement.GetProperty("id").GetInt32());
        }
    }

    [Fact]
    public async Task HttpConcurrentDeletes_ReturnOneConflict_ThenMissing()
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString, mutationGate: new ConcurrentMutationGate());
        using var first = factory.AuthenticatedClient();
        using var second = factory.AuthenticatedClient();
        var id = await CreateAsync(first, "/Messages", "delete-race");
        var responses = await Task.WhenAll(first.DeleteAsync("/Messages/" + id), second.DeleteAsync("/Messages/" + id));
        using var response1 = responses[0];
        using var response2 = responses[1];
        await using var read = postgres.CreateContext();
        Assert.Empty(await read.Messages.AsNoTracking().ToArrayAsync());
        using var missing = await first.DeleteAsync("/Messages/" + id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task UnknownPersistenceFailure_RemainsServerFailure_AndDoesNotCommit(string operation)
    {
        await postgres.ResetAsync();
        await using var factory = new ContactRuntimeFactory(postgres.ConnectionString, mutationGate: new UnknownMutationFailure());
        using var client = factory.AuthenticatedClient();
        var id = await CreateAsync(client, "/Messages", "unchanged");
        using var response = operation == "update"
            ? await client.PutAsJsonAsync("/Messages/" + id, new { MessageContent = "must-not-commit" })
            : await client.DeleteAsync("/Messages/" + id);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var read = postgres.CreateContext();
        Assert.Equal("unchanged", (await read.Messages.AsNoTracking().SingleAsync()).MessageContent);
    }

    [Fact]
    public async Task Invalidation_ActuallyCancelledCaller_StillPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cache = new DistributedContactRequestCache(new FailingInvalidationCache("provider-cancellation"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.InvalidateAsync(cancellation.Token));
    }
}

public sealed class ContactRuntimePostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public string ConnectionString => container.GetConnectionString();
    public ContactRequestDbContext CreateContext() => new(new DbContextOptionsBuilder<ContactRequestDbContext>()
        .UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Messages.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

internal sealed class ContactRuntimeFactory(
    string connectionString,
    SaveChangesInterceptor? mutationGate = null,
    string? invalidationFailure = null) : WebApplicationFactory<Program>
{
    private const string Issuer = "https://contact-fixture.example.invalid";
    private readonly RSA rsa = RSA.Create(2048);
    public static DateTimeOffset InitialTime { get; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public FakeTimeProvider Clock { get; } = new(InitialTime);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:ContactRequestDbContext", connectionString);
        builder.UseSetting("Cache:RedisEnabled", "false");
        builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())));
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Issuer);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (mutationGate != null)
            {
                services.AddDbContext<ContactRequestDbContext>(options => options.AddInterceptors(mutationGate));
            }

            if (invalidationFailure != null)
            {
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton<IDistributedCache>(new FailingInvalidationCache(invalidationFailure));
            }
        });
    }

    public HttpClient AuthenticatedClient(string scenario = "valid")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        if (scenario == "anonymous")
        {
            return client;
        }

        using var wrong = RSA.Create(2048);
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "synthetic-contact-caller") };
        if (scenario != "no-permission")
        {
            claims.AddRange(new[]
            {
                ContactRequestPermissions.ContactRequestsRead,
                ContactRequestPermissions.ContactRequestsCreate,
                ContactRequestPermissions.ContactRequestsUpdate,
                ContactRequestPermissions.ContactRequestsDelete,
            }.Select(permission => new Claim("permission", permission)));
        }

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(scenario == "wrong-issuer" ? "https://wrong.example.invalid" : Issuer,
            scenario == "wrong-audience" ? "https://wrong-audience.example.invalid" : Issuer,
            claims, now.AddHours(-1), scenario == "expired" ? now.AddMinutes(-20) : now.AddMinutes(10),
            new SigningCredentials(new RsaSecurityKey(scenario == "wrong-key" ? wrong : rsa), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            rsa.Dispose();
        }
    }
}

internal sealed class ConcurrentMutationGate : SaveChangesInterceptor
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrivals;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<ContactRequest>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                ready.SetResult();
            }

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        return result;
    }
}

internal sealed class UnknownMutationFailure : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<ContactRequest>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new DbUpdateException("Synthetic unknown persistence failure, not an xmin conflict.");
        }

        return ValueTask.FromResult(result);
    }
}

/// <summary>Controlled cache fault only; no fabricated database/auth/provider success.</summary>
internal sealed class FailingInvalidationCache(string failure) : IDistributedCache
{
    public byte[]? Get(string key) => null;
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        throw new NotSupportedException();
    public void Refresh(string key) => throw new NotSupportedException();
    public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
    public void Remove(string key) => throw new NotSupportedException();
    public Task RemoveAsync(string key, CancellationToken token = default) => failure == "provider-cancellation"
        ? Task.FromException(new OperationCanceledException("Synthetic provider cancellation with an uncancelled caller.", token))
        : Task.FromException(new IOException("Synthetic provider invalidation failure."));
}
