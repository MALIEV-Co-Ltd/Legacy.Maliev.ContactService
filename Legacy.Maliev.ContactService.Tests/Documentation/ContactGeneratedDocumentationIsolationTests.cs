using System.Xml.Linq;

namespace Legacy.Maliev.ContactService.Tests.Documentation;

/// <summary>Checks compiler-produced Contact XML documentation and its isolation from project source roots.</summary>
public sealed class ContactGeneratedDocumentationIsolationTests
{
    /// <summary>Checks actual assembly XML output, maintained member summaries, and absence of source-root artifacts.</summary>
    /// <param name="assemblyName">The Contact assembly name identifying its compiler-generated XML output and project source directory.</param>
    /// <param name="documentedMember">The XML member identifier whose maintained summary must appear in the compiled assembly documentation.</param>
    [Theory]
    [InlineData("Legacy.Maliev.ContactService.Api", "T:Program")]
    [InlineData("Legacy.Maliev.ContactService.Application", "T:Legacy.Maliev.ContactService.Application.Interfaces.IContactService")]
    [InlineData("Legacy.Maliev.ContactService.Data", "T:Legacy.Maliev.ContactService.Data.ContactRequestDbContext")]
    [InlineData("Legacy.Maliev.ContactService.Domain", "T:Legacy.Maliev.ContactService.Domain.ContactRequest")]
    [InlineData("Legacy.Maliev.ContactService.Tests", "T:Legacy.Maliev.ContactService.Tests.Documentation.ContactGeneratedDocumentationIsolationTests")]
    [InlineData("Legacy.Maliev.ContactService.Tests", "M:Legacy.Maliev.ContactService.Tests.Documentation.ContactGeneratedDocumentationIsolationTests.CompilerDocumentation_IsAvailableInOutputWithoutSourceRootArtifact(System.String,System.String)")]
    [InlineData("Legacy.Maliev.ContactService.Tests", "P:Legacy.Maliev.ContactService.Tests.Controllers.ContactRuntimePostgresFixture.ConnectionString")]
    public void CompilerDocumentation_IsAvailableInOutputWithoutSourceRootArtifact(string assemblyName, string documentedMember)
    {
        var output = Path.Combine(AppContext.BaseDirectory, assemblyName + ".xml");
        Assert.True(File.Exists(output), "Build the actual Contact solution projects before documentation acceptance.");

        var document = XDocument.Load(output);
        Assert.Equal(assemblyName, document.Root?.Element("assembly")?.Element("name")?.Value);
        var member = Assert.Single(document.Descendants("member"), element =>
            string.Equals((string?)element.Attribute("name"), documentedMember, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(member.Element("summary")?.Value));

        var sourceArtifact = Path.Combine(FindRepositoryRoot(), assemblyName, assemblyName + ".xml");
        Assert.False(File.Exists(sourceArtifact), "Generated assembly documentation must remain outside the project source root.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.ContactService.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Contact repository root.");
    }
}
