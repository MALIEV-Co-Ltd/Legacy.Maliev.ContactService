using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.ContactService.Tests.Workflows;

/// <summary>Checks immutable image-build dependencies and Docker restore inputs while preserving deployment admission.</summary>
public sealed class PublicationDependencyTests
{
    /// <summary>Checks publisher dependency pins, the closed deployment gate, and copying shared build properties before Docker restore.</summary>
    [Fact]
    public void Publisher_ProvidesPinnedDockerDependencies_WithoutOpeningDeploymentGate()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Legacy.Maliev.ContactService.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "publish-image.yml"))));
        var document = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)document.Children[new YamlScalarNode("jobs")];
        var publish = (YamlMappingNode)jobs.Children[new YamlScalarNode("publish")];
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED == 'true'", Value(publish, "if"));
        Assert.Equal("MALIEV-Co-Ltd/Legacy.Maliev.Workflows/.github/workflows/publish-image.yml@503e8846390a597c267d2889b33a9c26863389b3", Value(publish, "uses"));
        var inputs = (YamlMappingNode)publish.Children[new YamlScalarNode("with")];
        Assert.Equal("173d63218d9142b34075bca87f5c6d933bcbdfb6", Value(inputs, "legacy-service-defaults-ref"));
        Assert.Equal("fe17b8cea8ace3eef5434f8b29c9038f5c975613", Value(inputs, "compatibility-contracts-ref"));
        Assert.Equal(".", Value(inputs, "context"));
        Assert.Equal("legacy-production", Value(inputs, "environment"));

        var dockerfile = File.ReadAllText(Path.Combine(root.FullName, "Legacy.Maliev.ContactService.Api", "Dockerfile"));
        Assert.Contains("COPY Directory.Build.props .", dockerfile, StringComparison.Ordinal);
        Assert.Contains("RUN dotnet restore", dockerfile, StringComparison.Ordinal);
        Assert.True(dockerfile.IndexOf("COPY Directory.Build.props .", StringComparison.Ordinal) <
                    dockerfile.IndexOf("RUN dotnet restore", StringComparison.Ordinal));
    }

    private static string? Value(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value : null;
}
