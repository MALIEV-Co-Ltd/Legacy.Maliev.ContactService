using Legacy.Maliev.ContactService.Api.Controllers;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Moq;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

/// <summary>Checks original controller null-body guards and versioned identifier-validation ordering without service calls.</summary>
public sealed class ContactOriginalNullControllerTests
{
    /// <summary>Checks that null creation returns the original required-message response before calling the service.</summary>
    /// <returns>A task that completes after verifying that null creation returns the original required-message response before calling the service.</returns>
    [Fact]
    public async Task NullCreate_PreservesOriginalBadRequestMessageWithoutServiceCalls()
    {
        var service = new Mock<IContactService>(MockBehavior.Strict);
        var controller = new ContactRequestsController(service.Object);

        var result = await controller.CreateContactRequestAsync(null!, CancellationToken.None);

        AssertOriginalNullResult(result);
        service.VerifyNoOtherCalls();
    }

    /// <summary>Checks that a legacy null update body returns the original required-message response before looking up the identifier.</summary>
    /// <param name="messageId">The message identifier supplied with a null body to verify validation before lookup.</param>
    /// <returns>A task that completes after verifying that a legacy null update body returns the original required-message response before looking up the identifier.</returns>
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

    /// <summary>Checks that a versioned nonpositive identifier returns its existing rejection before the null-body guard.</summary>
    /// <returns>A task that completes after verifying that a versioned nonpositive identifier returns its existing rejection before the null-body guard.</returns>
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
