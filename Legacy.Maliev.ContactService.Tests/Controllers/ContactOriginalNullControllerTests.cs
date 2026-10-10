using Legacy.Maliev.ContactService.Api.Controllers;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Moq;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

public sealed class ContactOriginalNullControllerTests
{
    [Fact]
    public async Task NullCreate_PreservesOriginalBadRequestMessageWithoutServiceCalls()
    {
        var service = new Mock<IContactService>(MockBehavior.Strict);
        var controller = new ContactRequestsController(service.Object);

        var result = await controller.CreateContactRequestAsync(null!, CancellationToken.None);

        AssertOriginalNullResult(result);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task LegacyNullUpdate_PreservesOriginalBadRequestBeforeLookup(int messageId)
    {
        var service = new Mock<IContactService>(MockBehavior.Strict);
        var controller = new ContactRequestsController(service.Object);

        var result = await controller.UpdateContactRequestAsync(messageId, null!, CancellationToken.None);

        AssertOriginalNullResult(result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VersionedNonpositiveIdentifier_PreservesExistingRejectionBeforeNullGuard()
    {
        var service = new Mock<IContactService>(MockBehavior.Strict);
        var controller = new ContactRequestsController(service.Object);
        controller.ControllerContext = new ControllerContext { RouteData = new RouteData() };
        controller.ControllerContext.RouteData.Values["version"] = "1.0";

        var result = await controller.UpdateContactRequestAsync(0, null!, CancellationToken.None);

        Assert.IsType<BadRequestResult>(result);
        service.VerifyNoOtherCalls();
    }

    private static void AssertOriginalNullResult(ActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal("Message is required", Assert.IsType<string>(badRequest.Value));
    }
}
