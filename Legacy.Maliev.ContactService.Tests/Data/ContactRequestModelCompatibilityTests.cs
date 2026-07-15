using Legacy.Maliev.ContactService.Data;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Tests.Data;

public sealed class ContactRequestModelCompatibilityTests
{
    [Fact]
    public void Model_maps_to_legacy_Message_table_and_ID_column()
    {
        var options = new DbContextOptionsBuilder<ContactRequestDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .Options;
        using var context = new ContactRequestDbContext(options);

        var entity = context.Model.FindEntityType(typeof(ContactRequest));

        Assert.NotNull(entity);
        Assert.Equal("Message", entity.GetTableName());
        Assert.Equal("ID", entity.FindProperty(nameof(ContactRequest.Id))?.GetColumnName());
        Assert.Equal(50, entity.FindProperty(nameof(ContactRequest.FirstName))?.GetMaxLength());
        Assert.Equal(50, entity.FindProperty(nameof(ContactRequest.Email))?.GetMaxLength());
        Assert.NotNull(entity.FindProperty(nameof(ContactRequest.MessageContent)));
    }
}
