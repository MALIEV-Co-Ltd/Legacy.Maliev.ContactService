using System.Text.Json.Serialization;
using Legacy.Maliev.ContactService.Api.Logging;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Services;
using Legacy.Maliev.ContactService.Data;
using Legacy.Maliev.ContactService.Api.Documentation;
using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.Diagnostics;

try
{
    await RunHostAsync(args);
}
catch (Microsoft.Extensions.Hosting.HostAbortedException)
{
    throw;
}
catch (Exception exception)
{
    PrivateStartupBoundary.ReportFailure(exception);
}

static async Task RunHostAsync(string[] startupArgs)
{
    var builder = WebApplication.CreateBuilder(startupArgs);

    builder.AddServiceDefaults();
    builder.Logging.AddContactPrivateDiagnostics();
    builder.AddDefaultApiVersioning();
    builder.AddPostgresDbContext<ContactRequestDbContext>(connectionName: "ContactRequestDbContext");
    builder.AddStandardCache("legacy:contact:");
    builder.AddStandardCors();
    builder.AddJwtAuthentication();
    builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
    builder.AddStandardOpenApi(
        title: "Legacy MALIEV ContactRequest Service API",
        description: "Temporary .NET 10 compatibility service preserving the legacy website contact message API contract.");
    // A local literal registration lets .NET consume this assembly's maintained XML comments.
    builder.Services.AddOpenApi("v1", ContactOpenApi.Configure);

    builder.Services.AddControllers().AddJsonOptions(options =>
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<IContactRequestRepository, ContactRequestRepository>();
    builder.Services.AddScoped<IContactRequestCache, DistributedContactRequestCache>();
    builder.Services.AddScoped<IContactService, ContactRequestApplicationService>();

    var app = builder.Build();

    app.UseStandardMiddleware();
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapDefaultEndpoints("messages");
    app.MapControllers();
    app.MapApiDocumentation(servicePrefix: "messages");

    await app.RunAsync();
}

/// <summary>Legacy ContactRequest Service entry point.</summary>
public partial class Program;
