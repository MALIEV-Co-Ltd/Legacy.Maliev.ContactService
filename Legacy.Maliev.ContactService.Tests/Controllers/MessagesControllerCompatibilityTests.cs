using Legacy.Maliev.ContactService.Api.Controllers;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Api.Authorization;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

public sealed class MessagesControllerCompatibilityTests
{
    [Fact]
    public void GetPaginatedAsync_requires_contact_message_read_permission()
    {
        var method = typeof(ContactRequestsController).GetMethod(
            nameof(ContactRequestsController.GetPaginatedAsync),
            BindingFlags.Instance | BindingFlags.Public)!;

        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(
            ContactRequestPermissions.ContactRequestsRead,
            method.GetCustomAttribute<RequirePermissionAttribute>()?.Permission);
    }

    [Fact]
    public async Task GetPaginatedAsync_returns_not_found_for_empty_legacy_result()
    {
        var controller = new ContactRequestsController(new StubContactService(new PaginatedContactRequestResponse([], 1, 0, 0, false, false)));

        var result = await controller.GetPaginatedAsync(null, null, null, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateContactRequestAsync_uses_legacy_GetMessage_route_and_messageId_parameter()
    {
        var response = new ContactRequestResponse(7, "Nat", null, null, null, null, null, null, null, null);
        var controller = new ContactRequestsController(new StubContactService(new PaginatedContactRequestResponse([], 1, 0, 0, false, false), response));

        var result = await controller.CreateContactRequestAsync(
            new UpsertContactRequestRequest("Nat", null, null, null, null, null, null),
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtRouteResult>(result);
        Assert.Equal("GetMessage", created.RouteName);
        Assert.Equal(7, created.RouteValues?["messageId"]);
    }

    private sealed class StubContactService(
        PaginatedContactRequestResponse paginated,
        ContactRequestResponse? created = null) : IContactService
    {
        public Task<PaginatedContactRequestResponse> GetPaginatedAsync(
            ContactRequestSortType? sort,
            string? search,
            int? index,
            int? size,
            CancellationToken cancellationToken) => Task.FromResult(paginated);

        public Task<ContactRequestResponse?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult<ContactRequestResponse?>(created);

        public Task<ContactRequestResponse> CreateAsync(UpsertContactRequestRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(created ?? throw new InvalidOperationException("Created response not configured."));

        public Task<bool> UpdateAsync(int id, UpsertContactRequestRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
