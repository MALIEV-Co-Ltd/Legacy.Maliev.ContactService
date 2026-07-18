using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.ContactService.Data;

/// <summary>EF Core implementation of legacy ContactRequest persistence.</summary>
public sealed class ContactRequestRepository(ContactRequestDbContext dbContext) : IContactRequestRepository
{
    /// <inheritdoc />
    public async Task<PaginatedContactRequestResponse> GetPaginatedAsync(
        ContactRequestSortType? sort,
        string? search,
        int? index,
        int? size,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Messages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLikePattern(search)}%";
            query = query.Where(contactRequest =>
                EF.Functions.ILike(contactRequest.Id.ToString(), pattern, "\\") ||
                (contactRequest.MessageContent != null && EF.Functions.ILike(contactRequest.MessageContent, pattern, "\\")) ||
                (contactRequest.LastName != null && EF.Functions.ILike(contactRequest.LastName, pattern, "\\")) ||
                (contactRequest.Company != null && EF.Functions.ILike(contactRequest.Company, pattern, "\\")) ||
                (contactRequest.Email != null && EF.Functions.ILike(contactRequest.Email, pattern, "\\")) ||
                (contactRequest.Telephone != null && EF.Functions.ILike(contactRequest.Telephone, pattern, "\\")) ||
                (contactRequest.FirstName != null && EF.Functions.ILike(contactRequest.FirstName, pattern, "\\")));
        }

        var totalItems = await query.CountAsync(cancellationToken);
        var pageIndex = Math.Max(index ?? 1, 1);
        var pageSize = Math.Max(size ?? totalItems, 1);
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);
        query = sort switch
        {
            ContactRequestSortType.MessageId_Descending => query.OrderByDescending(contactRequest => contactRequest.Id),
            ContactRequestSortType.MessageCreatedDate_Ascending => query.OrderBy(contactRequest => contactRequest.CreatedDate),
            ContactRequestSortType.MessageCreatedDate_Descending => query.OrderByDescending(contactRequest => contactRequest.CreatedDate),
            _ => query.OrderBy(contactRequest => contactRequest.Id),
        };
        var items = await query
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .Select(contactRequest => new ContactRequestResponse(
                contactRequest.Id,
                contactRequest.FirstName,
                contactRequest.LastName,
                contactRequest.Company,
                contactRequest.Email,
                contactRequest.Telephone,
                contactRequest.Country,
                contactRequest.MessageContent,
                contactRequest.CreatedDate,
                contactRequest.ModifiedDate))
            .ToArrayAsync(cancellationToken);
        return new PaginatedContactRequestResponse(
            items,
            pageIndex,
            totalPages,
            totalItems,
            pageIndex > 1,
            pageIndex < totalPages);
    }

    /// <inheritdoc />
    public Task<ContactRequest?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        dbContext.Messages.AsNoTracking()
            .SingleOrDefaultAsync(contactRequest => contactRequest.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<ContactRequest?> GetByIdForUpdateAsync(int id, CancellationToken cancellationToken) =>
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

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
