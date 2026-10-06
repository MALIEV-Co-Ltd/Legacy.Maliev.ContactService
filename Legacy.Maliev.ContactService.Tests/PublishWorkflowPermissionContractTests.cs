using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.ContactService.Tests;

public sealed class PublishWorkflowPermissionContractTests
{
    [Fact]
    public void PublishWorkflow_UsesReviewedExactMainValidationGuard()
    {
        var workflow = ReadWorkflow();
        var jobs = (YamlMappingNode)workflow.Children[new YamlScalarNode("jobs")];
        var publish = (YamlMappingNode)jobs.Children[new YamlScalarNode("publish")];

        Assert.Equal("MALIEV-Co-Ltd/Legacy.Maliev.Workflows/.github/workflows/publish-image.yml@503e8846390a597c267d2889b33a9c26863389b3",
            ((YamlScalarNode)publish.Children[new YamlScalarNode("uses")]).Value);
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED == 'true'",
            ((YamlScalarNode)publish.Children[new YamlScalarNode("if")]).Value);
        var inputs = (YamlMappingNode)publish.Children[new YamlScalarNode("with")];
        Assert.Equal(8, inputs.Children.Count);
        Assert.Equal("c40a7f82cea347b949444dcd7fb730f2b8dc3c0e",
            ((YamlScalarNode)inputs.Children[new YamlScalarNode("legacy-service-defaults-ref")]).Value);
        Assert.Equal("78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7",
            ((YamlScalarNode)inputs.Children[new YamlScalarNode("compatibility-contracts-ref")]).Value);
    }

    [Fact]
    public void PublishWorkflow_GrantsOnlyRequiredJobLocalPermissions()
    {
        var workflow = ReadWorkflow();
        var permissions = (YamlMappingNode)workflow.Children[new YamlScalarNode("permissions")];
        Assert.Single(permissions.Children);
        Assert.Equal("read", ((YamlScalarNode)permissions.Children[new YamlScalarNode("contents")]).Value);
        var jobs = (YamlMappingNode)workflow.Children[new YamlScalarNode("jobs")];
        var publish = (YamlMappingNode)jobs.Children[new YamlScalarNode("publish")];
        var jobPermissions = (YamlMappingNode)publish.Children[new YamlScalarNode("permissions")];
        Assert.Equal(3, jobPermissions.Children.Count);
        Assert.Equal("read", ((YamlScalarNode)jobPermissions.Children[new YamlScalarNode("contents")]).Value);
        Assert.Equal("read", ((YamlScalarNode)jobPermissions.Children[new YamlScalarNode("actions")]).Value);
        Assert.Equal("write", ((YamlScalarNode)jobPermissions.Children[new YamlScalarNode("id-token")]).Value);
    }

    private static YamlMappingNode ReadWorkflow()
    {
        var stream = new YamlStream();
        using var reader = File.OpenText(Path.Combine(FindRoot(), ".github", "workflows", "publish-image.yml"));
        stream.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
    }

    [Fact]
    public void PublishWorkflow_ScopesOidcToPublishJobs()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRoot(),
            ".github",
            "workflows",
            "publish-image.yml"));

        var jobsIndex = source.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobsIndex > 0, "The publish workflow must define a jobs section.");
        var workflowHeader = source[..jobsIndex];
        Assert.Contains("permissions:", workflowHeader, StringComparison.Ordinal);
        Assert.Contains("  contents: read", workflowHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token: write", workflowHeader, StringComparison.OrdinalIgnoreCase);

        var publishJobs = Regex.Matches(
            source,
            @"(?ms)^  publish(?:-[^:\r\n]+)?:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*\r?$|\z)");
        Assert.NotEmpty(publishJobs);
        foreach (Match publishJob in publishJobs)
        {
            var job = publishJob.Groups["body"].Value;
            Assert.Contains("permissions:", job, StringComparison.Ordinal);
            Assert.Contains("contents: read", job, StringComparison.Ordinal);
            Assert.Contains("id-token: write", job, StringComparison.Ordinal);
        }

        var deploymentGate = Regex.Match(
            source,
            @"(?ms)^  deployment-gate:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*\r?$|\z)");
        if (deploymentGate.Success)
        {
            Assert.DoesNotContain("id-token: write", deploymentGate.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var workflow = Path.Combine(directory.FullName, ".github", "workflows", "publish-image.yml");
            if (File.Exists(workflow))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The publish workflow root was not found.");
    }
}
