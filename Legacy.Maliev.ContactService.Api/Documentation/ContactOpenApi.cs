using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Xml.Linq;

namespace Legacy.Maliev.ContactService.Api.Documentation;

internal static class ContactOpenApi
{
    internal static void Configure(OpenApiOptions options)
    {
        // The XML generator consumes record summaries but the served positional
        // record properties still lack their maintained comments. Apply the same
        // XML input at schema generation, without changing the payload records.
        using var payloadComments = typeof(ContactOpenApi).Assembly.GetManifestResourceStream(
            "Legacy.Maliev.ContactService.Api.Documentation.ContactPayload.xml")!;
        var fieldDescriptions = XDocument.Load(payloadComments).Descendants("member")
            .Where(member => member.Attribute("name")!.Value.StartsWith("P:", StringComparison.Ordinal))
            .ToDictionary(member => member.Attribute("name")!.Value.Split('.').Last(),
                member => member.Element("summary")!.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        options.AddSchemaTransformer((schema, context, cancellationToken) =>
        {
            if (context.JsonPropertyInfo is { } property
                && fieldDescriptions.TryGetValue(property.Name, out var description))
                schema.Description = description;
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, cancellationToken) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (!metadata.OfType<IAllowAnonymous>().Any() && metadata.OfType<IAuthorizeData>().Any())
                operation.Security = [new OpenApiSecurityRequirement()];
            return Task.CompletedTask;
        });
        options.AddDocumentTransformer((document, context, cancellationToken) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT bearer authentication and the operation's existing granular permission are required.",
            };
            foreach (var path in document.Paths.Values)
            {
                if (path.Operations is null) continue;
                foreach (var operation in path.Operations.Values)
                {
                    if (operation.Security is { Count: > 0 })
                        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
                    // XML parameter processing can replace the body description with
                    // the CancellationToken comment. All body operations use the
                    // existing contact-request create/update payload.
                    if (operation.RequestBody is not null)
                        operation.RequestBody.Description = "The contact details and message content supplied by the caller; identifiers and timestamps are managed by the service.";
                }
            }
            return Task.CompletedTask;
        });
    }
}
