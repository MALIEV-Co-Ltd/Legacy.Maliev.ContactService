using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.ContactService.Application.Models;

/// <summary>Legacy-compatible ContactRequest create and update payload.</summary>
public sealed record UpsertContactRequestRequest(
    [StringLength(50)] string? FirstName,
    [StringLength(50)] string? LastName,
    [StringLength(50)] string? Company,
    [StringLength(50)] string? Email,
    [StringLength(50)] string? Telephone,
    [StringLength(50)] string? Country,
    string? MessageContent);
