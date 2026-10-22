using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Tests.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ContactService.Tests.OriginalInput;

internal static class ContactOriginalBodyNative
{
    internal const string SetupName = "Legacy.Maliev.ContactService.Api.Serialization.ContactOriginalBodyMvcOptionsSetup";
    internal const string FormatterName = "Legacy.Maliev.ContactService.Api.Serialization.ContactOriginalBodyInputFormatter";

    internal static bool IsOwned(IConfigureOptions<MvcOptions> setup) =>
        setup.GetType().Assembly == typeof(Program).Assembly && setup.GetType().FullName == SetupName;

    internal static MvcOptions Create(IServiceProvider services)
    {
        var setups = services.GetServices<IConfigureOptions<MvcOptions>>().ToArray();
        Assert.Single(setups, IsOwned);
        var options = new OptionsFactory<MvcOptions>(setups.Where(setup => !IsOwned(setup)),
            services.GetServices<IPostConfigureOptions<MvcOptions>>(),
            services.GetServices<IValidateOptions<MvcOptions>>()).Create(Options.DefaultName);
        Assert.DoesNotContain(options.InputFormatters, formatter => formatter.GetType().FullName == FormatterName);
        Assert.Single(options.InputFormatters, formatter => formatter is SystemTextJsonInputFormatter);
        return options;
    }

    internal static void RemoveOwned(IServiceCollection services)
    {
        var original = services.ToArray();
        var descriptor = Assert.Single(original, value => value.ServiceType == typeof(IConfigureOptions<MvcOptions>)
            && value.ImplementationType is { } type && type.Assembly == typeof(Program).Assembly
            && type.FullName == SetupName);
        Assert.True(services.Remove(descriptor));
        var retained = original.Where(value => !ReferenceEquals(value, descriptor)).ToArray();
        Assert.Equal(retained.Length, services.Count);
        for (var index = 0; index < retained.Length; index++) Assert.Same(retained[index], services[index]);
    }

    internal static Task<ContactBodyRead> ReadAsync(IServiceProvider services, MvcOptions options, byte[] bytes,
        string contentType = "application/json", string prefix = "", int maxErrors = 200)
    {
        var formatter = options.InputFormatters.First(value => value.GetType().FullName == FormatterName
            || value is SystemTextJsonInputFormatter);
        return ContactOriginalInputOracle.ReadBytesAsync(formatter, typeof(UpsertContactRequestRequest),
            services.GetRequiredService<IModelMetadataProvider>(), bytes, contentType, prefix, maxErrors);
    }
}

// One normal disposable PG/factory for all reader rows; never one server per
// MemberData case. Initialization failure and normal completion release both.
/// <summary>Owns one disposable PostgreSQL fixture and Contact runtime factory shared across body-reader theory rows.</summary>
public sealed class ContactOriginalBodyReaderFixture : IAsyncLifetime
{
    private readonly ContactRuntimePostgresFixture postgres = new();
    internal ContactRuntimeFactory Factory { get; private set; } = null!;

    /// <summary>Starts the shared reader database fixture and constructs its runtime factory, releasing PostgreSQL if factory creation fails.</summary>
    /// <returns>A task that completes when PostgreSQL and the shared Contact runtime factory are initialized.</returns>
    public async Task InitializeAsync()
    {
        await postgres.InitializeAsync();
        try
        {
            Factory = new ContactRuntimeFactory(postgres.ConnectionString);
        }
        catch
        {
            await postgres.DisposeAsync();
            throw;
        }
    }

    /// <summary>Disposes the shared runtime factory and releases PostgreSQL even if factory disposal fails.</summary>
    /// <returns>A task that completes when the shared runtime factory and PostgreSQL fixture have been disposed.</returns>
    public async Task DisposeAsync()
    {
        try
        {
            if (Factory is not null) await Factory.DisposeAsync();
        }
        finally
        {
            await postgres.DisposeAsync();
        }
    }
}
