using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using Legacy.Maliev.ContactService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

internal static class ContactScaffoldRuntimeProof
{
    public static async Task RunAsync(ContactRuntimePostgresFixture fixture, int messageId)
    {
        Assert.True(OperatingSystem.IsLinux(), "Actual EF tooling proof requires its admitted Linux hosted lane; setup failure, not behavioral RED.");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.ContactService.slnx")))
            directory = directory.Parent;
        var repository = directory?.FullName ?? throw new DirectoryNotFoundException("Contact repository was not found.");
        var lease = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Path.GetTempPath(), "contact-actual-scaffold-" + lease);
        var output = Path.Combine(temporary, "preview");
        var tools = Path.Combine(temporary, "tools");
        var phases = new List<PhaseEvidence>();
        var proofPassed = false;
        var runtimeSourcesUnchanged = false;
        var generatedFiles = new List<object>();
        await using var host = new ContactRuntimeFactory(fixture.ConnectionString);
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        await using var context = fixture.CreateContext();
        var connection = context.Database.GetConnectionString()!;
        var before = await SnapshotAsync(context);
        var protectedPaths = new[] { "Legacy.Maliev.ContactService.Api/Program.cs", "Legacy.Maliev.ContactService.Data/ContactRequestDbContext.cs", "Legacy.Maliev.ContactService.Data/ContactRequestDbContextFactory.cs" };
        var protectedHashes = protectedPaths.Select(path => SHA256.HashData(File.ReadAllBytes(Path.Combine(repository, path)))).ToArray();
        var environment = new Dictionary<string, string?>
        {
            ["ConnectionStrings__ContactRequestDbContext"] = connection,
            ["ConnectionStrings__redis"] = configuration["ConnectionStrings:redis"],
            ["Cache__RedisEnabled"] = configuration["Cache:RedisEnabled"],
            ["Jwt__PublicKey"] = configuration["Jwt:PublicKey"],
            ["Jwt__Issuer"] = configuration["Jwt:Issuer"],
            ["Jwt__Audience"] = configuration["Jwt:Audience"],
            ["Features__ResourceScopedAuthEnabled"] = configuration["Features:ResourceScopedAuthEnabled"],
            ["IAM__LivePermissionChecks__Credential"] = configuration["IAM:LivePermissionChecks:Credential"],
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Logging__LogLevel__Default"] = "Warning",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["CODEX_CONTACT_SCAFFOLD_LEASE"] = lease,
            ["CODEX_CONTACT_PROOF_MESSAGE_ID"] = messageId.ToString(CultureInfo.InvariantCulture),
            ["EnableContactScaffoldDesignTime"] = "true",
            ["UseLocalMalievDependencies"] = "true",
            ["ArtifactsPath"] = Path.Combine(temporary, "artifacts"),
            ["VSTestResultsDirectory"] = "",
            ["CORS__AllowedOrigins__0"] = configuration["CORS:AllowedOrigins:0"],
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        };
        Directory.CreateDirectory(temporary);
        try
        {
            await ExecuteAsync("dotnet", ["tool", "install", "dotnet-ef", "--version", "10.0.12", "--tool-path", tools], "exact-tool-install", repository, environment, phases);
            environment["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            var build = await ExecuteAsync("dotnet", ["build", Path.Combine(repository, "Legacy.Maliev.ContactService.Api/Legacy.Maliev.ContactService.Api.csproj"), "-c", "Release", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false"], "isolated-design-startup-build", repository, environment, phases);
            RequireStrictBuild(build);
            await ExecuteAsync("pwsh", ["-NoProfile", "-File", Path.Combine(repository, "Legacy.Maliev.ContactService.Data/ScaffoldContext.ps1"), "-OutputDirectory", output], "actual-named-connection-scaffold", repository, environment, phases);
            var generated = Directory.GetFiles(output, "*.cs");
            Assert.Equal(2, generated.Length);
            foreach (var path in generated)
            {
                var text = await File.ReadAllTextAsync(path);
                Assert.False(text.Contains(connection, StringComparison.Ordinal), "Generated preview contains a connection value; private details withheld.");
                Assert.False(text.Contains("OnConfiguring", StringComparison.Ordinal), "Generated preview contains prohibited connection configuration; private details withheld.");
                Assert.False(text.Contains("Properties.Resources", StringComparison.Ordinal), "Generated preview contains prohibited resource configuration; private details withheld.");
                generatedFiles.Add(new { name = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
            }
            await File.WriteAllTextAsync(Path.Combine(output, "Preview.csproj"), PreviewProject);
            await File.WriteAllTextAsync(Path.Combine(output, "Program.cs"), PreviewProgram);
            var previewEnvironment = new Dictionary<string, string?>(environment);
            previewEnvironment.Remove("ArtifactsPath");
            previewEnvironment.Remove("EnableContactScaffoldDesignTime");
            var previewBuild = await ExecuteAsync("dotnet", ["build", Path.Combine(output, "Preview.csproj"), "-c", "Release", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false"], "generated-preview-build", repository, previewEnvironment, phases);
            RequireStrictBuild(previewBuild);
            var query = await ExecuteAsync("dotnet", [Path.Combine(output, "bin/Release/net10.0/Preview.dll")], "generated-model-and-query-proof", repository, previewEnvironment, phases);
            Assert.Contains("GENERATED_CONTACT_PROOF_OK", query, StringComparison.Ordinal);
            Assert.Equal(before, await SnapshotAsync(context));
            for (var index = 0; index < protectedPaths.Length; index++)
                Assert.Equal(protectedHashes[index], SHA256.HashData(File.ReadAllBytes(Path.Combine(repository, protectedPaths[index]))));
            runtimeSourcesUnchanged = true;
            proofPassed = true;
        }
        finally
        {
            var full = Path.GetFullPath(temporary);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "contact-actual-scaffold-", full, StringComparison.Ordinal);
            Assert.Equal("contact-actual-scaffold-" + lease, Path.GetFileName(full));
            var cleanupVerified = false;
            try
            {
                if (phases.All(phase => phase.CleanupVerified))
                {
                    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                    Assert.False(Directory.Exists(full));
                    cleanupVerified = true;
                }
            }
            finally
            {
                var evidence = Environment.GetEnvironmentVariable("VSTestResultsDirectory");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "contact-actual-scaffold.json"), JsonSerializer.Serialize(new
                    {
                        lease,
                        phases,
                        generatedFiles,
                        runtimeSourcesUnchanged,
                        cleanupVerified,
                        accepted = proofPassed && cleanupVerified,
                        remainingTemporaryDirectory = cleanupVerified ? null : full,
                        leaseExpiryUtc = DateTime.UtcNow.AddMinutes(20),
                        scope = "Actual EF named startup configuration, generated preview build/model/queries; disposable fixture only.",
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
    }

    private static async Task<string> ExecuteAsync(string executable, string[] arguments, string phase, string workingDirectory,
        Dictionary<string, string?> environment, List<PhaseEvidence> phases)
    {
        var memory = File.ReadLines("/proc/meminfo").Single(line => line.StartsWith("MemAvailable:", StringComparison.Ordinal));
        var availableKiB = long.Parse(memory.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        Assert.True(availableKiB >= 4194304, "Actual EF phase memory admission failed; setup failure, not behavioral RED.");
        // A new Linux session and unique inherited lease contain these task-owned
        // children. Build servers are disabled; no shared process is selected by name.
        using var process = new Process { StartInfo = new ProcessStartInfo("setsid") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        process.StartInfo.ArgumentList.Add(executable);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        foreach (var pair in environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        var processLease = environment["CODEX_CONTACT_SCAFFOLD_LEASE"] + "-" + phase;
        process.StartInfo.Environment["CODEX_CONTACT_SCAFFOLD_LEASE"] = processLease;
        Assert.True(process.Start(), "Actual EF child could not start; setup failure, not behavioral RED.");
        var start = process.StartTime.ToUniversalTime();
        var pid = process.Id;
        var phaseIndex = phases.Count;
        phases.Add(new PhaseEvidence(phase, pid, start, executable, availableKiB, false, -1,
            false, "setup-phase-incomplete", processLease, [], [], false));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var owned = new Dictionary<int, OwnedProcess>();
        var timedOut = false;
        var cleanupFailed = false;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        try
        {
            while (!process.HasExited)
            {
                foreach (var child in ObserveLease(processLease)) owned[child.Pid] = child;
                if (DateTime.UtcNow >= deadline) { timedOut = true; break; }
                await Task.Delay(200);
            }
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                // Re-observe each exact PID/start/executable/session/lease immediately
                // before signaling it. Never use a process-name or recursive-tree kill.
                for (var sweep = 0; sweep < 3; sweep++)
                {
                    cleanupDeadline.Token.ThrowIfCancellationRequested();
                    var active = ObserveLease(processLease);
                    foreach (var child in active) owned[child.Pid] = child;
                    if (active.Count == 0) break;
                    foreach (var child in active)
                    {
                        if (StillOwned(child, processLease)) _ = SendSignal(child.Pid, 15);
                    }
                    await Task.Delay(250);
                    foreach (var child in active)
                    {
                        cleanupDeadline.Token.ThrowIfCancellationRequested();
                        if (!StillOwned(child, processLease)) continue;
                        using var exact = Process.GetProcessById(child.Pid);
                        exact.Kill();
                        await exact.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3), cleanupDeadline.Token);
                    }
                }
                if (!process.HasExited && process.StartTime.ToUniversalTime() == start)
                {
                    // The direct child retains its original creation identity even if
                    // setsid exec changed the executable during an observation race.
                    _ = SendSignal(pid, 15);
                    await Task.Delay(250);
                    if (!process.HasExited && process.StartTime.ToUniversalTime() == start) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3), cleanupDeadline.Token);
                }
            }
            catch
            {
                cleanupFailed = true;
            }
            finally
            {
                var remainingOwned = ObserveLease(processLease).ToArray();
                var terminal = process.HasExited;
                var exitCode = terminal ? process.ExitCode : -1;
                phases[phaseIndex] = phases[phaseIndex] with
                {
                    TimedOut = timedOut,
                    ExitCode = exitCode,
                    Terminal = terminal,
                    FailureCategory = cleanupFailed || !terminal || remainingOwned.Length != 0 ? "setup-cleanup-incomplete"
                        : timedOut ? "setup-timeout" : exitCode == 0 ? null
                        : phase == "generated-model-and-query-proof" && exitCode == 2 ? "runtime-contract-mismatch" : "setup-tool-or-compiler",
                    OwnedProcesses = owned.Values.ToArray(),
                    RemainingOwnedProcesses = remainingOwned,
                    CleanupVerified = !cleanupFailed && terminal && remainingOwned.Length == 0,
                };
            }
        }
        Assert.True(phases[phaseIndex].CleanupVerified, "Actual EF process cleanup was not confirmed; sanitized ownership evidence retained.");
        var output = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
        _ = await stderr.WaitAsync(TimeSpan.FromSeconds(5)); // Private tool/provider details never enter public evidence.
        phases[phaseIndex] = phases[phaseIndex] with
        {
            WarningCounts = Regex.Matches(output, @"\b(\d+) Warning\(s\)").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray(),
            ErrorCounts = Regex.Matches(output, @"\b(\d+) Error\(s\)").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray(),
        };
        Assert.False(timedOut, "Actual EF tool timed out; setup failure, not behavioral RED.");
        Assert.True(process.ExitCode == 0, phase == "generated-model-and-query-proof" && process.ExitCode == 2
            ? "Actual generated model/query contract mismatch after successful generation/build; private details withheld."
            : $"Actual EF phase {phase} failed in tooling/configuration/compiler setup; private details withheld.");
        return output;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);

    private sealed record OwnedProcess(int Pid, DateTime ActualStartUtc, string ExecutableIdentity, int SessionId);

    private sealed record PhaseEvidence(string Phase, int Pid, DateTime ActualStartUtc, string Executable,
        long AvailableKiB, bool TimedOut, int ExitCode, bool Terminal, string? FailureCategory,
        string ProcessLease, OwnedProcess[] OwnedProcesses, OwnedProcess[] RemainingOwnedProcesses, bool CleanupVerified)
    {
        public int[] WarningCounts { get; init; } = [];
        public int[] ErrorCounts { get; init; } = [];
    }

    private static IReadOnlyList<OwnedProcess> ObserveLease(string lease)
    {
        var owned = new List<OwnedProcess>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var environment = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "environ")));
                if (!environment.Split('\0').Contains("CODEX_CONTACT_SCAFFOLD_LEASE=" + lease, StringComparer.Ordinal)) continue;
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) continue;
                var executable = process.MainModule?.FileName;
                if (executable is not null) owned.Add(new OwnedProcess(pid, process.StartTime.ToUniversalTime(), executable, int.Parse(fields[3], CultureInfo.InvariantCulture)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return owned;
    }

    private static bool StillOwned(OwnedProcess expected, string lease) =>
        ObserveLease(lease).Any(actual => actual == expected);

    private static void RequireStrictBuild(string output)
    {
        Assert.Contains("Build succeeded.", output, StringComparison.Ordinal);
        Assert.NotEmpty(Regex.Matches(output, @"\b(\d+) Warning\(s\)"));
        Assert.NotEmpty(Regex.Matches(output, @"\b(\d+) Error\(s\)"));
        Assert.All(Regex.Matches(output, @"\b(\d+) (Warning|Error)\(s\)"), match => Assert.Equal("0", match.Groups[1].Value));
    }

    private static async Task<string> SnapshotAsync(ContactRequestDbContext context) =>
        JsonSerializer.Serialize(await context.Messages.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());

    private const string PreviewProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
            <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.12" />
            <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
            <PackageReference Include="Microsoft.EntityFrameworkCore.Abstractions" Version="10.0.12" />
          </ItemGroup>
        </Project>
        """;

    private const string PreviewProgram = """
        using Legacy.Maliev.ContactService.ScaffoldPreview;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Metadata;
        using System.Globalization;

        try
        {
            var connection = Environment.GetEnvironmentVariable("ConnectionStrings__ContactRequestDbContext") ?? throw new InvalidOperationException();
            var messageId = int.Parse(Environment.GetEnvironmentVariable("CODEX_CONTACT_PROOF_MESSAGE_ID")!, CultureInfo.InvariantCulture);
            await using var context = new ContactScaffoldContext(new DbContextOptionsBuilder<ContactScaffoldContext>().UseNpgsql(connection).Options);
            Require(context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).SequenceEqual(new[] { "Message" }));
            var message = context.Model.FindEntityType(typeof(Message))!;
            var table = StoreObjectIdentifier.Table("Message", null);
            Require(message.FindProperty("Id")!.GetColumnName(table) == "ID");
            Require(message.FindPrimaryKey()!.Properties.Count == 1 && !message.GetForeignKeys().Any());
            foreach (var name in new[] { "FirstName", "LastName", "Company", "Email", "Telephone", "Country" })
                Require(message.FindProperty(name) is { IsNullable: true } property && property.GetMaxLength() == 50);
            Require(message.FindProperty("MessageContent")!.IsNullable && message.FindProperty("MessageContent")!.GetColumnType() == "text");
            foreach (var name in new[] { "CreatedDate", "ModifiedDate" })
                Require(message.FindProperty(name)!.IsNullable && message.FindProperty(name)!.GetColumnType() == "timestamp without time zone" && message.FindProperty(name)!.GetDefaultValueSql()!.Contains("UTC", StringComparison.Ordinal));
            var row = await context.Set<Message>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == messageId && EF.Functions.ILike(item.FirstName!, "Scaffold%")) ?? throw new GeneratedContractMismatch();
            Require(row.FirstName == "Scaffold" && row.LastName == "Contact" && row.Company == "Fixture company" && row.Email == "scaffold@example.test");
            Require(row.Telephone == "000" && row.Country == "Thailand" && row.MessageContent == "synthetic scaffold message");
            Console.WriteLine("GENERATED_CONTACT_PROOF_OK");
            return 0;
        }
        catch (GeneratedContractMismatch)
        {
            Console.Error.WriteLine("Generated Contact contract mismatch; private details withheld.");
            return 2;
        }
        catch
        {
            Console.Error.WriteLine("Generated Contact model/query proof failed; private details withheld.");
            return 1;
        }
        static void Require(bool valid) { if (!valid) throw new GeneratedContractMismatch(); }
        sealed class GeneratedContractMismatch : Exception;
        """;
}
