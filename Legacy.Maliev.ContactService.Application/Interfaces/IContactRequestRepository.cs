using Legacy.Maliev.ContactService.Domain;
using Legacy.Maliev.ContactService.Application.Models;

namespace Legacy.Maliev.ContactService.Application.Interfaces;

/// <summary>Persists legacy ContactRequest records.</summary>
public interface IContactRequestRepository
{
    /// <summary>Returns one filtered, sorted page without tracking.</summary>
    Task<PaginatedContactRequestResponse> GetPaginatedAsync(
        ContactRequestSortType? sort,
        string? search,
        int? index,
        int? size,
        CancellationToken cancellationToken);

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
