namespace Legacy.Maliev.ContactService.Application.Models;

/// <summary>Legacy contact-message sort values preserved from MessageService.</summary>
public enum ContactRequestSortType
{
    /// <summary>Sort by identifier ascending.</summary>
    MessageId_Ascending,

    /// <summary>Sort by identifier descending.</summary>
    MessageId_Descending,

    /// <summary>Sort by created date ascending.</summary>
    MessageCreatedDate_Ascending,

    /// <summary>Sort by created date descending.</summary>
    MessageCreatedDate_Descending,
}
