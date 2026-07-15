using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.ContactService.Application.Models;

/// <summary>Legacy-compatible ContactRequest create and update payload.</summary>
public sealed record UpsertContactRequestRequest(
    [property: StringLength(50)] string? FirstName,
    [property: StringLength(50)] string? LastName,
    [property: StringLength(50)] string? Company,
    [property: StringLength(50)] string? Email,
    [property: StringLength(50)] string? Telephone,
    [property: StringLength(50)] string? Country,
    string? MessageContent);
