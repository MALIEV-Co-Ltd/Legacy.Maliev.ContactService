namespace Legacy.Maliev.ContactService.Domain;

/// <summary>Represents the legacy website contact message record without changing its wire shape.</summary>
public sealed class ContactRequest
{
    /// <summary>Gets or sets the legacy integer identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the first name.</summary>
    public string? FirstName { get; set; }

    /// <summary>Gets or sets the last name.</summary>
    public string? LastName { get; set; }

    /// <summary>Gets or sets the company.</summary>
    public string? Company { get; set; }

    /// <summary>Gets or sets the email address.</summary>
    public string? Email { get; set; }

    /// <summary>Gets or sets the telephone number.</summary>
    public string? Telephone { get; set; }

    /// <summary>Gets or sets the country text submitted by the visitor.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the contact message content.</summary>
    public string? MessageContent { get; set; }

    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTime? CreatedDate { get; set; }

    /// <summary>Gets or sets the last modification timestamp.</summary>
    public DateTime? ModifiedDate { get; set; }
}
