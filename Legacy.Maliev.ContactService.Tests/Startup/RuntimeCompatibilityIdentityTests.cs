using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Xunit.Abstractions;

namespace Legacy.Maliev.ContactService.Tests.Startup;

/// <summary>Verifies the shared frameworks loaded by each qualification test host.</summary>
public sealed class RuntimeCompatibilityIdentityTests(ITestOutputHelper output)
{
    /// <summary>Rejects a misconfigured qualification host before filtered tests execute.</summary>
    [ModuleInitializer]
    internal static void RequireQualifiedRuntimeWhenRequested()
    {
        var required = Environment.GetEnvironmentVariable("CONTACT_REQUIRED_RUNTIME_MAJOR");
        if (required is null) return;
        var assembly = typeof(WebApplication).Assembly;
        if (required != "11" || Environment.Version.Major != 11
            || !RuntimeInformation.FrameworkDescription.StartsWith(".NET 11.", StringComparison.Ordinal)
            || assembly.GetName().Version?.Major != 11
            || !assembly.Location.Replace('\\', '/').Contains("/shared/Microsoft.AspNetCore.App/11.", StringComparison.Ordinal))
            throw new InvalidOperationException("The Contact runtime qualification test host did not load the required shared frameworks.");
    }

    /// <summary>Exercises the identity check in the ordinary full test suite.</summary>
    [Fact]
    public void Loaded_shared_frameworks_match_the_qualification_lane()
    {
        RequireQualifiedRuntimeWhenRequested();
        if (Environment.GetEnvironmentVariable("CONTACT_REQUIRED_RUNTIME_MAJOR") is not null)
        {
            var assembly = typeof(WebApplication).Assembly;
            output.WriteLine($"Framework={RuntimeInformation.FrameworkDescription}; Core={Environment.Version}; ASP.NET={assembly.GetName().Version}; Location={assembly.Location}");
        }
    }
}
