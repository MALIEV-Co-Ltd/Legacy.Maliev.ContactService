using Legacy.Maliev.ContactService.Application.Models;

namespace Legacy.Maliev.ContactService.Application.Interfaces;

/// <summary>Caches read-heavy ContactRequest responses.</summary>
public interface IContactRequestCache
{
    /// <summary>Gets the complete ContactRequest list.</summary>
    Task<IReadOnlyList<ContactRequestResponse>?> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Stores the complete ContactRequest list.</summary>
    Task SetAllAsync(IReadOnlyList<ContactRequestResponse> ContactRequests, CancellationToken cancellationToken);

    /// <summary>Invalidates all ContactRequest entries.</summary>
    Task InvalidateAsync(CancellationToken cancellationToken);
}
