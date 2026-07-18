using Legacy.Maliev.ContactService.Domain;

namespace Legacy.Maliev.ContactService.Application.Interfaces;

/// <summary>Persists legacy ContactRequest records.</summary>
public interface IContactRequestRepository
{
    /// <summary>Returns all records without tracking.</summary>
    Task<IReadOnlyList<ContactRequest>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Returns one record without tracking for read-only use.</summary>
    Task<ContactRequest?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Returns one tracked record for update or deletion.</summary>
    Task<ContactRequest?> GetByIdForUpdateAsync(int id, CancellationToken cancellationToken) =>
        GetByIdAsync(id, cancellationToken);

    /// <summary>Adds and saves a record.</summary>
    Task AddAsync(ContactRequest ContactRequest, CancellationToken cancellationToken);

    /// <summary>Saves changes to a record.</summary>
    Task UpdateAsync(ContactRequest ContactRequest, CancellationToken cancellationToken);

    /// <summary>Deletes and saves a record.</summary>
    Task DeleteAsync(ContactRequest ContactRequest, CancellationToken cancellationToken);
}
