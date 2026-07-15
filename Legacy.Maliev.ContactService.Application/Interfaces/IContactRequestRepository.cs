using Legacy.Maliev.ContactService.Domain;

namespace Legacy.Maliev.ContactService.Application.Interfaces;

/// <summary>Persists legacy ContactRequest records.</summary>
public interface IContactRequestRepository
{
    /// <summary>Returns all records without tracking.</summary>
    Task<IReadOnlyList<ContactRequest>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Returns one tracked record.</summary>
    Task<ContactRequest?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Adds and saves a record.</summary>
    Task AddAsync(ContactRequest ContactRequest, CancellationToken cancellationToken);

    /// <summary>Saves changes to a record.</summary>
    Task UpdateAsync(ContactRequest ContactRequest, CancellationToken cancellationToken);

    /// <summary>Deletes and saves a record.</summary>
    Task DeleteAsync(ContactRequest ContactRequest, CancellationToken cancellationToken);
}
