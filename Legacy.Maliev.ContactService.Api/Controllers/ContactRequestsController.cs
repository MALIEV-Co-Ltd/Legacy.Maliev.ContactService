using Legacy.Maliev.ContactService.Api.Authorization;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.ContactService.Api.Controllers;

/// <summary>Preserves the legacy ContactRequest HTTP contract during migration.</summary>
[ApiController]
[Route("Messages")]
[Authorize]
public sealed class ContactRequestsController(IContactService contactService) : ControllerBase
{
    /// <summary>Returns paginated contact messages using the legacy query contract.</summary>
    [HttpGet]
    [AllowAnonymous]
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
    [HttpGet("{messageId:int}", Name = "GetMessage")]
    [RequirePermission(ContactRequestPermissions.ContactRequestsRead)]
    public async Task<ActionResult<ContactRequestResponse>> GetContactRequestAsync(
        int messageId,
        CancellationToken cancellationToken)
    {
        var contactRequest = await contactService.GetByIdAsync(messageId, cancellationToken);
        return contactRequest is null ? NotFound() : contactRequest;
    }

    /// <summary>Creates a ContactRequest.</summary>
    [HttpPost]
    [RequirePermission(ContactRequestPermissions.ContactRequestsCreate)]
    public async Task<ActionResult> CreateContactRequestAsync(
        [FromBody] UpsertContactRequestRequest request,
        CancellationToken cancellationToken)
    {
        var created = await contactService.CreateAsync(request, cancellationToken);
        return CreatedAtRoute("GetMessage", new { messageId = created.Id }, created);
    }

    /// <summary>Updates a ContactRequest.</summary>
    [HttpPut("{messageId:int}")]
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

        return await contactService.UpdateAsync(messageId, request, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    /// <summary>Deletes a ContactRequest.</summary>
    [HttpDelete("{messageId:int}")]
    [RequirePermission(ContactRequestPermissions.ContactRequestsDelete)]
    public async Task<ActionResult> DeleteContactRequestAsync(int messageId, CancellationToken cancellationToken)
    {
        return await contactService.DeleteAsync(messageId, cancellationToken)
            ? NoContent()
            : NotFound();
    }
}
