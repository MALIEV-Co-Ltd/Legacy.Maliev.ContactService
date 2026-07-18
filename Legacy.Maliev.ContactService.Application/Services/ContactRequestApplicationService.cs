using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Domain;

namespace Legacy.Maliev.ContactService.Application.Services;

/// <summary>Implements legacy-compatible ContactRequest behavior.</summary>
public sealed class ContactRequestApplicationService(
    IContactRequestRepository repository,
    IContactRequestCache cache,
    TimeProvider timeProvider) : IContactService
{
    /// <inheritdoc />
    public async Task<PaginatedContactRequestResponse> GetPaginatedAsync(
        ContactRequestSortType? sort,
        string? search,
        int? index,
        int? size,
        CancellationToken cancellationToken)
    {
        return await repository.GetPaginatedAsync(sort, search, index, size, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ContactRequestResponse?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var ContactRequest = await repository.GetByIdAsync(id, cancellationToken);
        return ContactRequest is null ? null : ToResponse(ContactRequest);
    }

    /// <inheritdoc />
    public async Task<ContactRequestResponse> CreateAsync(
        UpsertContactRequestRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ContactRequest = new ContactRequest
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Company = request.Company,
            Email = request.Email,
            Telephone = request.Telephone,
            Country = request.Country,
            MessageContent = request.MessageContent,
            CreatedDate = now,
            ModifiedDate = now,
        };
        await repository.AddAsync(ContactRequest, cancellationToken);
        await cache.InvalidateAsync(cancellationToken);
        return ToResponse(ContactRequest);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        int id,
        UpsertContactRequestRequest request,
        CancellationToken cancellationToken)
    {
        var ContactRequest = await repository.GetByIdForUpdateAsync(id, cancellationToken);
        if (ContactRequest is null)
        {
            return false;
        }

        ContactRequest.FirstName = request.FirstName;
        ContactRequest.LastName = request.LastName;
        ContactRequest.Company = request.Company;
        ContactRequest.Email = request.Email;
        ContactRequest.Telephone = request.Telephone;
        ContactRequest.Country = request.Country;
        ContactRequest.MessageContent = request.MessageContent;
        ContactRequest.ModifiedDate = timeProvider.GetUtcNow().UtcDateTime;
        await repository.UpdateAsync(ContactRequest, cancellationToken);
        await cache.InvalidateAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var ContactRequest = await repository.GetByIdForUpdateAsync(id, cancellationToken);
        if (ContactRequest is null)
        {
            return false;
        }

        await repository.DeleteAsync(ContactRequest, cancellationToken);
        await cache.InvalidateAsync(cancellationToken);
        return true;
    }

    private static ContactRequestResponse ToResponse(ContactRequest contactRequest) => new(
        contactRequest.Id,
        contactRequest.FirstName,
        contactRequest.LastName,
        contactRequest.Company,
        contactRequest.Email,
        contactRequest.Telephone,
        contactRequest.Country,
        contactRequest.MessageContent,
        contactRequest.CreatedDate,
        contactRequest.ModifiedDate);
}
