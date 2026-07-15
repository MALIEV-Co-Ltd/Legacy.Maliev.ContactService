using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Data;

/// <summary>EF Core implementation of legacy ContactRequest persistence.</summary>
public sealed class ContactRequestRepository(ContactRequestDbContext dbContext) : IContactRequestRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ContactRequest>> GetAllAsync(CancellationToken cancellationToken) =>
        await dbContext.Messages.AsNoTracking().OrderBy(contactRequest => contactRequest.Id)
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContactRequest?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        dbContext.Messages.SingleOrDefaultAsync(contactRequest => contactRequest.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task AddAsync(ContactRequest ContactRequest, CancellationToken cancellationToken)
    {
        await dbContext.Messages.AddAsync(ContactRequest, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(ContactRequest ContactRequest, CancellationToken cancellationToken)
    {
        dbContext.Messages.Update(ContactRequest);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ContactRequest ContactRequest, CancellationToken cancellationToken)
    {
        dbContext.Messages.Remove(ContactRequest);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
