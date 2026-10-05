using Asp.Versioning;
using Legacy.Maliev.ContactService.Api.Authorization;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.ContactService.Api.Controllers;

/// <summary>Preserves the legacy ContactRequest HTTP contract during migration.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("Messages")]
[Authorize]
public sealed class ContactRequestsController(IContactService contactService) : ControllerBase
{
    /// <summary>Returns paginated contact messages using the legacy query contract.</summary>
    /// <remarks>Reading contact details requires the existing contact-message read permission.</remarks>
    /// <param name="sort" example="0">The legacy contact-message sort value.</param>
    /// <param name="search" example="enquiry">Text to search in the contact message fields.</param>
    /// <param name="index" example="1">The one-based page index; omitted values use the existing default.</param>
    /// <param name="size" example="10">The page size; omitted values use the existing default.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <response code="200">The selected page of contact messages.</response>
    /// <response code="404">No messages exist on the selected page.</response>
    [HttpGet]
    [HttpGet("/messages/v{version:apiVersion}/contact-requests")]
    [RequirePermission(ContactRequestPermissions.ContactRequestsRead)]
    [ProducesResponseType<PaginatedContactRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaginatedContactRequestResponse>> GetPaginatedAsync(
        [FromQuery] ContactRequestSortType? sort,
        [FromQuery] string? search,
        [FromQuery] int? index,
        [FromQuery] int? size,
        CancellationToken cancellationToken)
    {
        var contactRequests = await contactService.GetPaginatedAsync(sort, search, index, size, cancellationToken);
        if (contactRequests.Items.Count == 0)
        {
            return NotFound();
        }

        return contactRequests;
    }

    /// <summary>Returns one ContactRequest by legacy identifier.</summary>
    /// <param name="messageId" example="42">The identifier of the contact message to retrieve.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("{messageId:int}", Name = "GetMessage")]
    [HttpGet("/messages/v{version:apiVersion}/contact-requests/{messageId:int}", Name = "GetVersionedMessage")]
    [RequirePermission(ContactRequestPermissions.ContactRequestsRead)]
    public async Task<ActionResult<ContactRequestResponse>> GetContactRequestAsync(
        int messageId,
        CancellationToken cancellationToken)
    {
        var contactRequest = await contactService.GetByIdAsync(messageId, cancellationToken);
        return contactRequest is null ? NotFound() : contactRequest;
    }

    /// <summary>Creates a ContactRequest.</summary>
    /// <param name="request">The contact details and message content to store; identifiers and timestamps are assigned by the service.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpPost]
    [HttpPost("/messages/v{version:apiVersion}/contact-requests")]
    [RequirePermission(ContactRequestPermissions.ContactRequestsCreate)]
    public async Task<ActionResult> CreateContactRequestAsync(
        [FromBody] UpsertContactRequestRequest request,
        CancellationToken cancellationToken)
    {
        var created = await contactService.CreateAsync(request, cancellationToken);
        return CreatedAtRoute("GetMessage", new { messageId = created.Id }, created);
    }

    /// <summary>Updates a ContactRequest.</summary>
    /// <param name="messageId" example="42">The identifier of the contact message to update.</param>
    /// <param name="request">The replacement contact details and message content.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpPut("{messageId:int}")]
    [HttpPut("/messages/v{version:apiVersion}/contact-requests/{messageId:int}")]
    [RequirePermission(ContactRequestPermissions.ContactRequestsUpdate)]
    public async Task<ActionResult> UpdateContactRequestAsync(
        int messageId,
        [FromBody] UpsertContactRequestRequest request,
        CancellationToken cancellationToken)
    {
        if (messageId <= 0)
        {
            return BadRequest();
        }

        try
        {
            return await contactService.UpdateAsync(messageId, request, cancellationToken)
                ? NoContent()
                : NotFound();
        }
        catch (ContactRequestConcurrencyException)
        {
            return Conflict();
        }
    }

    /// <summary>Deletes a ContactRequest.</summary>
    /// <param name="messageId" example="42">The identifier of the contact message to delete.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpDelete("{messageId:int}")]
    [HttpDelete("/messages/v{version:apiVersion}/contact-requests/{messageId:int}")]
    [RequirePermission(ContactRequestPermissions.ContactRequestsDelete)]
    public async Task<ActionResult> DeleteContactRequestAsync(int messageId, CancellationToken cancellationToken)
    {
        try
        {
            return await contactService.DeleteAsync(messageId, cancellationToken)
                ? NoContent()
                : NotFound();
        }
        catch (ContactRequestConcurrencyException)
        {
            return Conflict();
        }
    }
}
