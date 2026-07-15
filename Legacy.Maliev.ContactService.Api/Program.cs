using System.Text.Json.Serialization;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Services;
using Legacy.Maliev.ContactService.Data;
using Maliev.Aspire.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddPostgresDbContext<ContactRequestDbContext>(connectionName: "ContactRequestDbContext");
builder.AddStandardCache("legacy:contact:");
builder.AddStandardCors();
builder.AddJwtAuthentication();
builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
builder.AddStandardOpenApi(
    title: "Legacy MALIEV ContactRequest Service API",
    description: "Temporary .NET 10 compatibility service preserving the legacy website contact message API contract.");

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

/// <summary>Legacy ContactRequest Service entry point.</summary>
public partial class Program;
