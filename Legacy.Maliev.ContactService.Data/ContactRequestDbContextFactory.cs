using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Legacy.Maliev.ContactService.Data;

/// <summary>Creates the context for explicit design-time migration commands.</summary>
public sealed class ContactRequestDbContextFactory : IDesignTimeDbContextFactory<ContactRequestDbContext>
{
    /// <inheritdoc />
    public ContactRequestDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ContactRequestDbContext");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings__ContactRequestDbContext is required for design-time migration commands.");
        }

        var options = new DbContextOptionsBuilder<ContactRequestDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ContactRequestDbContext(options);
    }
}
