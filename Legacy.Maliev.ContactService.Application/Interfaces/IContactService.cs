using Legacy.Maliev.ContactService.Application.Models;

namespace Legacy.Maliev.ContactService.Application.Interfaces;

/// <summary>Provides legacy ContactRequest operations.</summary>
public interface IContactService
{
    /// <summary>Returns paginated contact requests using the legacy search and sort contract.</summary>
    Task<PaginatedContactRequestResponse> GetPaginatedAsync(
        ContactRequestSortType? sort,
        string? search,
        int? index,
        int? size,
        CancellationToken cancellationToken);

    /// <summary>Returns a ContactRequest by legacy identifier.</summary>
    Task<ContactRequestResponse?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Creates a ContactRequest.</summary>
    Task<ContactRequestResponse> CreateAsync(UpsertContactRequestRequest request, CancellationToken cancellationToken);

    /// <summary>Updates a ContactRequest when it exists.</summary>
    Task<bool> UpdateAsync(int id, UpsertContactRequestRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes a ContactRequest when it exists.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
}
