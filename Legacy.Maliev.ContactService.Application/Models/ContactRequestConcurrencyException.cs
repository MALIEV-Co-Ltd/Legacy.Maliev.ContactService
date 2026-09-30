namespace Legacy.Maliev.ContactService.Application.Models;

/// <summary>Indicates that a tracked contact request changed before its mutation could commit.</summary>
public sealed class ContactRequestConcurrencyException(Exception innerException)
    : Exception("The contact request changed before the mutation could commit.", innerException);
