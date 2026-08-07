using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.ContactService.Data.Migrations;

/// <inheritdoc />
public partial class AlignUtcTimestampColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        ConvertUtcTimestampColumns(migrationBuilder, toTimestampWithoutTimeZone: true);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        ConvertUtcTimestampColumns(migrationBuilder, toTimestampWithoutTimeZone: false);

    private static void ConvertUtcTimestampColumns(MigrationBuilder migrationBuilder, bool toTimestampWithoutTimeZone)
    {
        var targetType = toTimestampWithoutTimeZone
            ? "timestamp without time zone"
            : "timestamp with time zone";
        var defaultSql = toTimestampWithoutTimeZone
            ? "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')"
            : "CURRENT_TIMESTAMP";

        foreach (var column in new[] { "ModifiedDate", "CreatedDate" })
        {
            migrationBuilder.Sql($"""
                ALTER TABLE "Message" ALTER COLUMN "{column}" DROP DEFAULT;
                ALTER TABLE "Message" ALTER COLUMN "{column}"
                    TYPE {targetType}
                    USING "{column}" AT TIME ZONE 'UTC';
                ALTER TABLE "Message" ALTER COLUMN "{column}" SET DEFAULT {defaultSql};
                """);
        }
    }
}
