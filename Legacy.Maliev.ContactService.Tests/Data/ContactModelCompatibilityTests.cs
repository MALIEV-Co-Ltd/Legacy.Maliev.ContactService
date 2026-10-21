using System.Runtime.CompilerServices;
using Legacy.Maliev.ContactService.Data;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Tests.Data;

/// <summary>Checks UTC wall-clock timestamp mappings and explicit UTC conversion in the timestamp migration.</summary>
public sealed class ContactModelCompatibilityTests
{
    /// <summary>Checks that creation and modification timestamps use timestamp without time zone with a UTC SQL default.</summary>
    [Fact]
    public void Model_UsesUtcWallClockTimestampContract()
    {
        var options = new DbContextOptionsBuilder<ContactRequestDbContext>()
            .UseNpgsql("Host=localhost;Database=model")
            .Options;
        using var context = new ContactRequestDbContext(options);
        var entity = context.Model.FindEntityType(typeof(ContactRequest))!;

        foreach (var propertyName in new[] { nameof(ContactRequest.CreatedDate), nameof(ContactRequest.ModifiedDate) })
        {
            var property = entity.FindProperty(propertyName)!;
            Assert.Equal("timestamp without time zone", property.GetColumnType());
            Assert.Equal("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'", property.GetDefaultValueSql());
        }
    }

    /// <summary>Checks that timestamp migration SQL drops existing defaults and explicitly interprets existing values as UTC.</summary>
    [Fact]
    public void TimestampMigration_ConvertsExistingValuesExplicitlyAsUtc()
    {
        var migration = File.ReadAllText(FindRepositoryFile(
            "Legacy.Maliev.ContactService.Data/Migrations/20260807143100_AlignUtcTimestampColumns.cs"));

        Assert.Contains("DROP DEFAULT", migration, StringComparison.Ordinal);
        Assert.Contains("timestamp without time zone", migration, StringComparison.Ordinal);
        Assert.Contains("USING \"{column}\" AT TIME ZONE 'UTC'", migration, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find migration source '{relativePath}'.");
    }
}
