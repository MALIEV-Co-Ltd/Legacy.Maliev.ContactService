using Legacy.Maliev.ContactService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

public sealed class RequestValidationCompatibilityTests
{
    [Fact]
    public void Upsert_request_is_accepted_by_the_Mvc_record_validation_pipeline()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddMvcCore()
            .AddDataAnnotations()
            .Services
            .BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var actionContext = new ActionContext(
            new DefaultHttpContext { RequestServices = services },
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var request = new UpsertContactRequestRequest(
            "Nat",
            "Maliev",
            "MALIEV",
            "contact@example.com",
            "+66000000000",
            "Thailand",
            "Test message");

        var exception = Record.Exception(() => validator.Validate(actionContext, null, string.Empty, request));

        Assert.Null(exception);
    }
}
