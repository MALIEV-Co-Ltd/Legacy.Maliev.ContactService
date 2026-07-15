namespace Legacy.Maliev.ContactService.Application.Models;

/// <summary>Legacy-compatible ContactRequest response.</summary>
public sealed record ContactRequestResponse(
    int Id,
    string? FirstName,
    string? LastName,
    string? Company,
    string? Email,
    string? Telephone,
    string? Country,
    string? MessageContent,
    DateTime? CreatedDate,
    DateTime? ModifiedDate);

/// <summary>Legacy-compatible paginated Web API response shape.</summary>
public sealed record PaginatedContactRequestResponse(
    IReadOnlyList<ContactRequestResponse> Items,
    int PageIndex,
    int TotalPages,
    int TotalItems,
    bool HasPreviousPage,
    bool HasNextPage);
