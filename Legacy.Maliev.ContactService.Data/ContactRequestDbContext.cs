using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Data;

/// <summary>PostgreSQL context preserving the legacy Message schema contract.</summary>
public sealed class ContactRequestDbContext(DbContextOptions<ContactRequestDbContext> options) : DbContext(options)
{
    /// <summary>Gets the contact message records.</summary>
    public DbSet<ContactRequest> Messages => Set<ContactRequest>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<ContactRequest>();
        message.ToTable("Message");
        message.HasKey(entity => entity.Id);
        message.Property(entity => entity.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        message.Property(entity => entity.FirstName).HasMaxLength(50);
        message.Property(entity => entity.LastName).HasMaxLength(50);
        message.Property(entity => entity.Company).HasMaxLength(50);
        message.Property(entity => entity.Email).HasMaxLength(50);
        message.Property(entity => entity.Telephone).HasMaxLength(50);
        message.Property(entity => entity.Country).HasMaxLength(50);
        message.Property(entity => entity.MessageContent);
        message.Property(entity => entity.CreatedDate).HasColumnType("timestamp with time zone");
        message.Property(entity => entity.ModifiedDate).HasColumnType("timestamp with time zone");
        message.Property<uint>("xmin").HasColumnType("xid").IsRowVersion();
    }
}
