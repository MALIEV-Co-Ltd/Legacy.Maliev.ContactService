using System.Text.RegularExpressions;
using System.Xml.Linq;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.ContactService.Tests.Workflows;

public sealed class WorkflowContractTests
{
    private static readonly string Workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "_build-and-test.yml"));
    private static readonly string ApiProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.ContactService.Api", "Legacy.Maliev.ContactService.Api.csproj"));

    private static void BuildAndTest_RejectsPreviousUploadArtifactPin()
    {
        var previousPin = Workflow.Replace("actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a", "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02", StringComparison.Ordinal);
        Assert.NotEqual(Workflow, previousPin);
        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(previousPin));
    }

    [Fact]
    public void BuildAndTest_SatisfiesStructuralContract()
    {
        BuildAndTest_RejectsPreviousUploadArtifactPin();
        WorkflowContractValidator.Validate(Workflow);
    }

    [Theory]
    [InlineData("shell: pwsh", "shell: bash")]
    [InlineData("./tooling/Test-ContactScaffoldContract.ps1", "./tooling/UnreviewedScaffold.ps1")]
    [InlineData("./runner-results/contact-scaffold-orchestration.json", "./discarded-scaffold-evidence.json")]
    public void BuildAndTest_RejectsAlteredScaffoldGuardBoundary(string original, string replacement)
    {
        AssertMutationRejected(original, replacement);
    }

    [Fact]
    public void BuildAndTest_RejectsSharedActionMainWithPinnedShaComment()
    {
        AssertMutationRejected(
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@e3a6093324a24968876782153286f52db8b29fd8",
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@main # e3a6093324a24968876782153286f52db8b29fd8");
    }

    [Fact]
    public void BuildAndTest_RejectsCommentedDependencySha()
    {
        AssertMutationRejected(
            "ref: c40a7f82cea347b949444dcd7fb730f2b8dc3c0e",
            "ref: main # c40a7f82cea347b949444dcd7fb730f2b8dc3c0e");
    }

    [Fact]
    public void ApiProject_UsesOnlyLegacyServiceDefaults()
    {
        Assert.Contains("Legacy.Maliev.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Maliev.Aspire\\Maliev.Aspire.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Maliev.Aspire.ServiceDefaults\"", ApiProject, StringComparison.Ordinal);
        var project = XDocument.Parse(ApiProject);
        var enabled = Assert.Single(project.Descendants("EnableContactScaffoldDesignTime"));
        Assert.Equal("false", enabled.Value);
        Assert.Equal("'$(EnableContactScaffoldDesignTime)' == ''", enabled.Attribute("Condition")?.Value);
        var reference = Assert.Single(project.Descendants("PackageReference"),
            element => (string?)element.Attribute("Include") == "Microsoft.EntityFrameworkCore.Design");
        Assert.Equal("'$(EnableContactScaffoldDesignTime)' == 'true'", reference.Attribute("Condition")?.Value);
        Assert.Equal("10.0.12", reference.Attribute("Version")?.Value);
        Assert.Equal("all", reference.Element("PrivateAssets")?.Value);

    }

    [Fact]
    public void BuildAndTest_RejectsJobPermissionEscalation()
    {
        AssertMutationRejected(
            "  validate:\n    name: validate",
            "  validate:\n    permissions:\n      contents: write\n    name: validate");
    }

    [Fact]
    public void BuildAndTest_RejectsSecretReferenceAnywhere()
    {
        var mutated = $"{Workflow}\n# ${{{{ secrets.X }}}}\n";

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    [Theory]
    [InlineData("${{secrets.X}}")]
    [InlineData("${{ secrets['X'] }}")]
    public void BuildAndTest_RejectsSecretExpressionInJobEnvironment(string expression)
    {
        AssertMutationRejected(
            "    env:\n      MalievWorkspaceRoot: ${{ github.workspace }}/.dependencies",
            $"    env:\n      MalievWorkspaceRoot: ${{{{ github.workspace }}}}/.dependencies\n      REVIEW_TOKEN: {expression}");
    }

    [Fact]
    public void BuildAndTest_RejectsWhitespaceObfuscatedRestoreCommand()
    {
        AssertMutationRejected(
            "          solution: Legacy.Maliev.ContactService.slnx",
            "          solution: Legacy.Maliev.ContactService.slnx\n      - run: dotnet  restore Legacy.Maliev.ContactService.slnx");
    }

    [Fact]
    public void BuildAndTest_RejectsMissingLocalDependencyOptIn()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            string.Empty);
    }

    [Fact]
    public void BuildAndTest_RejectsReservedGitHubActionsOverride()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            "          use-local-maliev-dependencies: 'true'\n        env:\n          GITHUB_ACTIONS: 'false'\n");
    }

    private static void AssertMutationRejected(string original, string replacement)
    {
        Assert.Contains(original, Workflow, StringComparison.Ordinal);
        var mutated = Workflow.Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(segments)}'.");
    }
}

internal static partial class WorkflowContractValidator
{
    private const string CheckoutAction = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1";
    private const string SharedValidationAction = "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@e3a6093324a24968876782153286f52db8b29fd8";

    public static void Validate(string workflow)
    {
        if (SecretExpression().IsMatch(workflow))
        {
            throw new InvalidOperationException("Workflow must not reference secrets.");
        }

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(workflow));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Workflow must be valid YAML.", exception);
        }

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("Workflow must contain exactly one mapping document.");
        }

        var workflowPermissions = RequireExactReadOnlyPermissions(RequireMapping(root, "permissions"), "workflow");
        var jobs = RequireMapping(root, "jobs");
        if (jobs.Children.Count != 1)
        {
            throw new InvalidOperationException("Workflow must define only the validate job.");
        }

        var validateJob = RequireMapping(jobs, "validate");
        var jobPermissionsNode = GetOptional(validateJob, "permissions");
        var effectiveJobPermissions = jobPermissionsNode is null
            ? workflowPermissions
            : RequireExactReadOnlyPermissions(RequireMapping(jobPermissionsNode, "jobs.validate.permissions"), "validate job");
        if (!effectiveJobPermissions.SequenceEqual(workflowPermissions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Validate job permissions must not differ from workflow permissions.");
        }

        RequireScalarValue(validateJob, "name", "validate");
        RejectDuplicatedValidationActionsAndCommands(jobs);

        var steps = RequireSequence(validateJob, "steps");
        if (steps.Children.Count != 7)
        {
            throw new InvalidOperationException("Validate job must contain four validation, one scaffold guard, and two evidence steps.");
        }

        var environment = RequireMapping(validateJob, "env");
        if (environment.Children.Count != 4)
        {
            throw new InvalidOperationException("Validate environment must contain only dependency root and evidence properties.");
        }

        RequireScalarValue(environment, "MalievWorkspaceRoot", "${{ github.workspace }}/.dependencies");
        RequireScalarValue(environment, "VSTestCollect", "XPlat Code Coverage");
        RequireScalarValue(environment, "VSTestLogger", "trx");
        RequireScalarValue(environment, "VSTestResultsDirectory", "${{ github.workspace }}/runner-results");

        var scaffold = RequireMapping(steps.Children[4], "scaffold guard");
        if (scaffold.Children.Count != 3)
        {
            throw new InvalidOperationException("Scaffold guard must contain only name, shell, and run.");
        }

        RequireScalarValue(scaffold, "name", "Validate safe scaffold orchestration");
        RequireScalarValue(scaffold, "shell", "pwsh");
        RequireScalarValue(scaffold, "run", "./tooling/Test-ContactScaffoldContract.ps1 -EvidencePath ./runner-results/contact-scaffold-orchestration.json");
        var gate = RequireMapping(steps.Children[5], "coverage gate");
        if (gate.Children.Count != 2)
        {
            throw new InvalidOperationException("Coverage gate must contain only name and run.");
        }

        RequireScalarValue(gate, "name", "Gate owned production coverage");
        RequireScalarValue(gate, "run", "python3 scripts/verify-runner-coverage.py runner-results");
        var evidence = RequireMapping(steps.Children[6], "evidence upload");
        if (evidence.Children.Count != 4)
        {
            throw new InvalidOperationException("Evidence upload must contain exactly name, if, uses and with.");
        }

        RequireScalarValue(evidence, "name", "Preserve validation evidence");
        RequireScalarValue(evidence, "if", "always()");
        RequireScalarValue(evidence, "uses", "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a");
        var evidenceInputs = RequireMapping(evidence, "with");
        if (evidenceInputs.Children.Count != 4)
        {
            throw new InvalidOperationException("Evidence upload must have exactly four bounded inputs.");
        }

        RequireScalarValue(evidenceInputs, "name", "contact-validation-${{ github.sha }}");
        RequireScalarValue(evidenceInputs, "path", "runner-results");
        RequireScalarValue(evidenceInputs, "if-no-files-found", "warn");
        RequireScalarValue(evidenceInputs, "retention-days", "7");

        ValidateStep(
            steps.Children[0],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[1],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults",
                ["ref"] = "c40a7f82cea347b949444dcd7fb730f2b8dc3c0e",
                ["path"] = ".dependencies/Legacy.Maliev.ServiceDefaults",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[2],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts",
                ["ref"] = "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7",
                ["path"] = ".dependencies/Legacy.Maliev.CompatibilityContracts",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[3],
            SharedValidationAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["solution"] = "Legacy.Maliev.ContactService.slnx",
                ["use-local-maliev-dependencies"] = "true",
            });
    }

    private static IReadOnlyList<string> RequireExactReadOnlyPermissions(YamlMappingNode permissions, string scope)
    {
        if (permissions.Children.Count != 1)
        {
            throw new InvalidOperationException($"{scope} permissions must contain only contents: read.");
        }

        RequireScalarValue(permissions, "contents", "read");
        return ["contents:read"];
    }

    private static void ValidateStep(
        YamlNode node,
        string expectedAction,
        IReadOnlyDictionary<string, string> expectedInputs)
    {
        var step = RequireMapping(node, "workflow step");
        var allowedKeys = new HashSet<string>(["name", "uses", "with"], StringComparer.Ordinal);

        var actualKeys = step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal);
        if (!actualKeys.SetEquals(allowedKeys))
        {
            throw new InvalidOperationException("Each workflow step must contain exactly name, uses, and with.");
        }

        var name = RequireScalar(GetRequired(step, "name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Workflow step name must not be empty.");
        }

        RequireScalarValue(step, "uses", expectedAction);
        var inputs = RequireMapping(step, "with");
        if (inputs.Children.Count != expectedInputs.Count)
        {
            throw new InvalidOperationException($"Action {expectedAction} has an unexpected input count.");
        }

        foreach (var expectedInput in expectedInputs)
        {
            RequireScalarValue(inputs, expectedInput.Key, expectedInput.Value);
        }

        var environment = GetOptional(step, "env");
        if (environment is not null)
        {
            throw new InvalidOperationException("Workflow action steps must not override reserved environment variables.");
        }

        if (expectedInputs.ContainsKey("use-local-maliev-dependencies"))
        {
            var useLocalDependencies = GetRequired(inputs, "use-local-maliev-dependencies") as YamlScalarNode;
            if (useLocalDependencies?.Style != ScalarStyle.SingleQuoted)
            {
                throw new InvalidOperationException("use-local-maliev-dependencies must use the single-quoted string value 'true'.");
            }
        }
    }

    private static void RejectDuplicatedValidationActionsAndCommands(YamlMappingNode jobs)
    {
        foreach (var jobNode in jobs.Children.Values.OfType<YamlMappingNode>())
        {
            var stepsNode = GetOptional(jobNode, "steps");
            if (stepsNode is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var stepNode in steps.Children.OfType<YamlMappingNode>())
            {
                if (GetOptional(stepNode, "uses") is YamlScalarNode usesNode)
                {
                    var action = usesNode.Value ?? string.Empty;
                    if (action.StartsWith("actions/setup-dotnet@", StringComparison.OrdinalIgnoreCase)
                        || action.StartsWith("actions/cache@", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Caller duplicates shared action {action}.");
                    }
                }

                if (GetOptional(stepNode, "run") is YamlScalarNode runNode)
                {
                    RejectDuplicatedDotNetCommand(runNode.Value ?? string.Empty);
                }
            }
        }
    }

    private static void RejectDuplicatedDotNetCommand(string command)
    {
        var tokens = command
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('"', '\'', ';', '&', '|').ToLowerInvariant())
            .Where(token => token.Length > 0)
            .ToArray();

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!string.Equals(tokens[index], "dotnet", StringComparison.Ordinal))
            {
                continue;
            }

            var verb = tokens[index + 1];
            if (verb is "restore" or "build" or "test" or "format" or "list"
                || tokens[(index + 1)..].Any(token => token is "audit" or "--vulnerable"))
            {
                throw new InvalidOperationException($"Caller duplicates shared dotnet validation command: {verb}.");
            }
        }
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode parent, string key) =>
        RequireMapping(GetRequired(parent, key), key);

    private static YamlMappingNode RequireMapping(YamlNode node, string description)
    {
        return node as YamlMappingNode
            ?? throw new InvalidOperationException($"{description} must be a mapping.");
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode parent, string key)
    {
        return GetRequired(parent, key) as YamlSequenceNode
            ?? throw new InvalidOperationException($"{key} must be a sequence.");
    }

    private static void RequireScalarValue(YamlMappingNode parent, string key, string expected)
    {
        var actual = RequireScalar(GetRequired(parent, key));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{key} must equal '{expected}', but was '{actual}'.");
        }
    }

    private static string RequireScalar(YamlNode node)
    {
        return (node as YamlScalarNode)?.Value
            ?? throw new InvalidOperationException("Expected a scalar YAML value.");
    }

    private static YamlNode GetRequired(YamlMappingNode parent, string key)
    {
        return GetOptional(parent, key)
            ?? throw new InvalidOperationException($"Missing required YAML key '{key}'.");
    }

    private static YamlNode? GetOptional(YamlMappingNode parent, string key)
    {
        foreach (var child in parent.Children)
        {
            if (child.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return child.Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\$\{\{\s*secrets\s*(?:\.|\[)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretExpression();
}
