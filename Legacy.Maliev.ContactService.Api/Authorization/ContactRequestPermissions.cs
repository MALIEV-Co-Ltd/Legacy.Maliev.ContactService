namespace Legacy.Maliev.ContactService.Api.Authorization;

/// <summary>Permissions for mutating temporary legacy ContactRequest data.</summary>
public static class ContactRequestPermissions
{
    /// <summary>Read one protected ContactRequest record.</summary>
    public const string ContactRequestsRead = "legacy-contact.messages.read";

    /// <summary>Create ContactRequest records.</summary>
    public const string ContactRequestsCreate = "legacy-contact.messages.create";

    /// <summary>Update ContactRequest records.</summary>
    public const string ContactRequestsUpdate = "legacy-contact.messages.update";

    /// <summary>Delete ContactRequest records.</summary>
    public const string ContactRequestsDelete = "legacy-contact.messages.delete";
}
