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
        var contactRequests = await repository.GetAllAsync(cancellationToken);
        var query = ApplySearch(contactRequests, search);
        query = ApplySort(query, sort);
        var totalItems = query.Count;
        var pageIndex = Math.Max(index ?? 1, 1);
        var pageSize = Math.Max(size ?? totalItems, 1);
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);
        var items = query
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .Select(ToResponse)
            .ToArray();

        return new PaginatedContactRequestResponse(
            items,
            pageIndex,
            totalPages,
            totalItems,
            pageIndex > 1,
            pageIndex < totalPages);
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
        var ContactRequest = await repository.GetByIdAsync(id, cancellationToken);
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
        var ContactRequest = await repository.GetByIdAsync(id, cancellationToken);
        if (ContactRequest is null)
        {
            return false;
        }

        await repository.DeleteAsync(ContactRequest, cancellationToken);
        await cache.InvalidateAsync(cancellationToken);
        return true;
    }

    private static IReadOnlyList<ContactRequest> ApplySearch(
        IReadOnlyList<ContactRequest> contactRequests,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return contactRequests;
        }

        var normalizedSearch = search.ToLowerInvariant();
        return contactRequests.Where(contactRequest =>
            contactRequest.Id.ToString().Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
            Contains(contactRequest.MessageContent, normalizedSearch) ||
            Contains(contactRequest.LastName, normalizedSearch) ||
            Contains(contactRequest.Company, normalizedSearch) ||
            Contains(contactRequest.Email, normalizedSearch) ||
            Contains(contactRequest.Telephone, normalizedSearch) ||
            Contains(contactRequest.FirstName, normalizedSearch)).ToArray();
    }

    private static IReadOnlyList<ContactRequest> ApplySort(
        IReadOnlyList<ContactRequest> contactRequests,
        ContactRequestSortType? sort) =>
        (sort switch
        {
            ContactRequestSortType.MessageId_Descending => contactRequests.OrderByDescending(contactRequest => contactRequest.Id),
            ContactRequestSortType.MessageCreatedDate_Ascending => contactRequests.OrderBy(contactRequest => contactRequest.CreatedDate),
            ContactRequestSortType.MessageCreatedDate_Descending => contactRequests.OrderByDescending(contactRequest => contactRequest.CreatedDate),
            _ => contactRequests.OrderBy(contactRequest => contactRequest.Id),
        }).ToArray();

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

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
