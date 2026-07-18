using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Legacy.Maliev.ContactService.Api.Controllers;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

public sealed class VersionedContactRequestRouteTests
{
    private const string LegacyCollection = "Messages";
    private const string VersionedCollection = "messages/v1/contact-requests";

    [Fact]
    public void Controller_preserves_legacy_route_and_declares_v1()
    {
        var controller = typeof(ContactRequestsController);

        Assert.Equal(LegacyCollection, controller.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.Equal("1.0", controller.GetCustomAttribute<ApiVersionAttribute>()?.Versions.Single().ToString());
    }

    [Fact]
    public void Named_get_routes_are_distinct_and_keep_GetMessage_for_legacy_location_headers()
    {
        var routes = typeof(ContactRequestsController)
            .GetMethod(nameof(ContactRequestsController.GetContactRequestAsync))!
            .GetCustomAttributes<HttpGetAttribute>()
            .ToArray();

        Assert.Contains(routes, route => route.Name == "GetMessage" && route.Template == "{messageId:int}");
        Assert.Contains(
            routes,
            route => route.Name == "GetVersionedMessage"
                && route.Template == "/messages/v{version:apiVersion}/contact-requests/{messageId:int}");
        Assert.Equal(2, routes.Select(route => route.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ApiExplorer_exposes_non_ambiguous_legacy_and_versioned_route_parity()
    {
        IReadOnlyList<ApiDescription> descriptions = DiscoverControllerApiDescriptions();
        var expectedRoutes = new (string Method, string LegacyPath, string VersionedPath)[]
        {
            ("GET", LegacyCollection, VersionedCollection),
            ("GET", $"{LegacyCollection}/{{messageId}}", $"{VersionedCollection}/{{messageId}}"),
            ("POST", LegacyCollection, VersionedCollection),
            ("PUT", $"{LegacyCollection}/{{messageId}}", $"{VersionedCollection}/{{messageId}}"),
            ("DELETE", $"{LegacyCollection}/{{messageId}}", $"{VersionedCollection}/{{messageId}}")
        };

        Assert.Equal(
            descriptions.Count,
            descriptions.Select(description => (description.HttpMethod, description.RelativePath)).Distinct().Count());

        foreach ((string method, string legacyPath, string versionedPath) in expectedRoutes)
        {
            ApiDescription legacy = Assert.Single(descriptions, description =>
                description.HttpMethod == method && description.RelativePath == legacyPath);
            ApiDescription versioned = Assert.Single(descriptions, description =>
                description.HttpMethod == method && description.RelativePath == versionedPath);

            var legacyAction = Assert.IsType<ControllerActionDescriptor>(legacy.ActionDescriptor);
            var versionedAction = Assert.IsType<ControllerActionDescriptor>(versioned.ActionDescriptor);
            Assert.Equal(legacyAction.MethodInfo, versionedAction.MethodInfo);
            Assert.Equal(
                legacyAction.MethodInfo.GetCustomAttribute<RequirePermissionAttribute>()?.Permission,
                versionedAction.MethodInfo.GetCustomAttribute<RequirePermissionAttribute>()?.Permission);
        }
    }

    private static IReadOnlyList<ApiDescription> DiscoverControllerApiDescriptions()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        IServiceCollection services = builder.Services;
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(ContactRequestsController).Assembly);
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        }).AddMvc().AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'V";
            options.SubstituteApiVersionInUrl = true;
        });

        using IHost host = builder.Build();
        return host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Where(description => description.ActionDescriptor is ControllerActionDescriptor action
                && action.ControllerTypeInfo.AsType() == typeof(ContactRequestsController))
            .ToArray();
    }
}
