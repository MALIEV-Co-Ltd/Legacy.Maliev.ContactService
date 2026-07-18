using System.Collections.Concurrent;
using System.Data.Common;
using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Data;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.ContactService.Tests.Data;

public sealed class ContactRequestMigrationTests
{
    [Fact]
    public async Task Paginated_query_filters_sorts_and_pages_in_postgres()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await postgres.StartAsync();
        var commands = new RecordingCommandInterceptor();
        var options = new DbContextOptionsBuilder<ContactRequestDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .AddInterceptors(commands)
            .Options;
        await using var context = new ContactRequestDbContext(options);
        await context.Database.MigrateAsync();
        context.Messages.AddRange(Enumerable.Range(1, 30).Select(index => new ContactRequest
        {
            FirstName = $"Customer {index:D2}",
            Email = $"customer-{index:D2}@example.invalid",
            MessageContent = "quote request",
            CreatedDate = new DateTime(2026, 1, index, 0, 0, 0, DateTimeKind.Utc),
        }));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        commands.Clear();
        var repository = new ContactRequestRepository(context);

        var page = await repository.GetPaginatedAsync(
            ContactRequestSortType.MessageId_Descending,
            "quote",
            2,
            5,
            CancellationToken.None);

        Assert.Equal(30, page.TotalItems);
        Assert.Equal(6, page.TotalPages);
        Assert.Equal([25, 24, 23, 22, 21], page.Items.Select(item => item.Id));
        Assert.Contains(commands.Commands, command => command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(commands.Commands, command => command.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Single_read_is_untracked_and_detached_update_still_succeeds()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<ContactRequestDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        await using var context = new ContactRequestDbContext(options);
        await context.Database.MigrateAsync();
        var repository = new ContactRequestRepository(context);
        var message = new ContactRequest { Email = "fixture@example.invalid", MessageContent = "Fixture" };
        await repository.AddAsync(message, CancellationToken.None);
        context.ChangeTracker.Clear();

        var loaded = await repository.GetByIdAsync(message.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Empty(context.ChangeTracker.Entries());
        var tracked = await repository.GetByIdForUpdateAsync(message.Id, CancellationToken.None);
        Assert.NotNull(tracked);
        tracked.MessageContent = "Updated fixture";
        await repository.UpdateAsync(tracked, CancellationToken.None);
        context.ChangeTracker.Clear();
        Assert.Equal(
            "Updated fixture",
            await context.Messages.AsNoTracking()
                .Where(value => value.Id == message.Id)
                .Select(value => value.MessageContent)
                .SingleAsync());
    }

    [Fact]
    public async Task Initial_migration_applies_twice_and_preserves_native_xmin()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<ContactRequestDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;

        await using (var context = new ContactRequestDbContext(options))
        {
            await context.Database.MigrateAsync();
            await context.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();

        Assert.Equal(1L, await ExecuteScalarAsync(connection, """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'Message';
            """));
        Assert.Equal(0L, await ExecuteScalarAsync(connection, """
            SELECT COUNT(*)
            FROM pg_attribute attribute
            JOIN pg_class relation ON relation.oid = attribute.attrelid
            WHERE relation.relname = 'Message'
              AND attribute.attname = 'xmin'
              AND attribute.attnum > 0;
            """));
    }

    private static async Task<long> ExecuteScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> commands = new();

        public IReadOnlyCollection<string> Commands => commands.ToArray();

        public void Clear() => commands.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
