using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Legacy.Maliev.ContactService.Tests.Startup;

/// <summary>Serializes tests that intercept the Contact entrypoint and mutate process-wide startup state.</summary>
[CollectionDefinition("Contact startup state", DisableParallelization = true)]
public sealed class StartupCollection;

/// <summary>Verifies private startup failure reporting, normal host interception, and unchanged host-abort behavior.</summary>
[Collection("Contact startup state")]
public sealed class StartupFailureAcceptanceTests(ITestOutputHelper output)
{
    private const string Sentinel = "private-startup-test-sentinel";

    /// <summary>Checks that actual API startup failures emit redacted private diagnostics and exit with code one.</summary>
    /// <param name="phase">The malformed configuration, missing-root, or host-start failure scenario applied to the actual API process.</param>
    /// <returns>A task that completes when the actual startup process exits with code one and its private diagnostic assertions finish.</returns>
    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-root")]
    [InlineData("host-start")]
    public async Task Actual_entrypoint_reports_private_failure_and_exits_one(string phase)
    {
        var root = Path.Combine(Path.GetTempPath(), Sentinel + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var child = new Process();
        var custody = new StartupCustody(child, root);
        try
        {
            File.WriteAllText(Path.Combine(root, "appsettings.json"), phase == "host-start"
                ? JsonSerializer.Serialize(Settings()) : "{ invalid-" + Sentinel);
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Legacy.Maliev.ContactService.slnx")))
                repository = repository.Parent;
            Assert.NotNull(repository);
            var api = Path.Combine(repository.FullName, "Legacy.Maliev.ContactService.Api", "bin", "Release", "net10.0",
                "Legacy.Maliev.ContactService.Api.dll");
            Assert.True(File.Exists(api) && File.Exists(Path.ChangeExtension(api, ".runtimeconfig.json")),
                "Build the actual API Release artifacts before running subprocess acceptance.");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(api)!
            };
            var benign = new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT", "HOME", "USERPROFILE" }
                .Select(key => (Key: key, Value: Environment.GetEnvironmentVariable(key))).ToArray();
            start.Environment.Clear();
            foreach (var (key, value) in benign)
                if (value is not null) start.Environment[key] = value;
            var requireRuntime11 = Environment.GetEnvironmentVariable("CONTACT_REQUIRED_RUNTIME_MAJOR") == "11";
            var runtimeReceipt = Path.Combine(root, "runtime-identity.json");
            var testAssembly = typeof(StartupFailureAcceptanceTests).Assembly.Location;
            if (requireRuntime11)
            {
                var run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
                var attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
                Assert.Matches("^[1-9][0-9]*$", run ?? "");
                Assert.Matches("^[1-9][0-9]*$", attempt ?? "");
                start.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
                start.Environment["DOTNET_STARTUP_HOOKS"] = testAssembly;
                start.Environment["CONTACT_RUNTIME_RECEIPT"] = runtimeReceipt;
                start.Environment["CONTACT_RUNTIME_RUN"] = run + ":" + attempt;
            }
            foreach (var argument in new[] { api, "--environment=Production", "--contentRoot=" +
                (phase == "missing-root" ? Path.Combine(root, Sentinel) : root) }) start.ArgumentList.Add(argument);
            if (phase == "host-start") start.ArgumentList.Add("--urls=http://127.0.0.1:0/" + Sentinel);
            child.StartInfo = start;
            custody.DeclaredExecutable = start.FileName;
            custody.StartAttempted = true;
            custody.StartResult = child.Start();
            Assert.True(custody.StartResult.Value);
            custody.StdoutReader = child.StandardOutput;
            custody.Stdout = ReadBoundedAsync(custody.StdoutReader, custody.Readers.Token);
            custody.StderrReader = child.StandardError;
            custody.Stderr = ReadBoundedAsync(custody.StderrReader, custody.Readers.Token);
            custody.Pid = child.Id;
            custody.Birth = child.StartTime.ToUniversalTime();
            ObserveOwnedExecutable(custody);
            if (requireRuntime11)
            {
                await ContactRuntimeProcessIdentity.AwaitReceiptAsync(
                    () => File.Exists(runtimeReceipt), () => child.HasExited, custody.Readers.Token);
                var ticks = ContactRuntimeProcessIdentity.ParseStartTicks(
                    await File.ReadAllTextAsync($"/proc/{child.Id}/stat", custody.Readers.Token));
                using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(runtimeReceipt, custody.Readers.Token));
                var observed = identity.RootElement;
                Assert.Equal(child.Id, observed.GetProperty("pid").GetInt32());
                Assert.Equal(ticks, observed.GetProperty("startTicks").GetUInt64());
                Assert.Equal(start.Environment["CONTACT_RUNTIME_RUN"], observed.GetProperty("run").GetString());
                Assert.Equal(testAssembly, observed.GetProperty("hookPath").GetString());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(testAssembly, custody.Readers.Token))),
                    observed.GetProperty("hookSha256").GetString());
                Assert.Equal(11, observed.GetProperty("coreMajor").GetInt32());
                Assert.Equal(11, observed.GetProperty("aspNetMajor").GetInt32());
                Assert.Equal(typeof(object).Assembly.Location, observed.GetProperty("corePath").GetString());
                Assert.Equal(typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location,
                    observed.GetProperty("aspNetPath").GetString());
                Assert.False(child.HasExited, "Runtime identity child exited during inspection.");
                var pending = runtimeReceipt + ".ack.pending";
                await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var writer = new StreamWriter(file))
                    await writer.WriteAsync($"{start.Environment["CONTACT_RUNTIME_RUN"]}|{child.Id}|{ticks}");
                File.Move(pending, runtimeReceipt + ".ack");
            }
            try { await child.WaitForExitAsync(custody.Readers.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Owned child startup timed out; this is not behavioral failure evidence.");
            }
            var stdout = custody.Stdout!;
            var stderr = custody.Stderr!;
            Assert.Equal(1, child.ExitCode);
            var capturedStdout = await stdout.WaitAsync(custody.Readers.Token);
            if (phase != "host-start") Assert.Equal(string.Empty, capturedStdout);
            var raw = capturedStdout + await stderr.WaitAsync(custody.Readers.Token);
            Assert.DoesNotContain(Sentinel, raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("StackTrace", raw, StringComparison.OrdinalIgnoreCase);
            var entries = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                using var parsed = JsonDocument.Parse(entry);
                Assert.False(parsed.RootElement.TryGetProperty("StackTrace", out _));
            }
            var line = Assert.Single(entries, entry =>
            {
                using var parsed = JsonDocument.Parse(entry);
                return parsed.RootElement.TryGetProperty("eventId", out var eventId) && eventId.GetInt32() == 5102;
            });
            using var json = JsonDocument.Parse(line);
            Assert.Equal(5102, json.RootElement.GetProperty("eventId").GetInt32());
            Assert.Equal("StartupFailure", json.RootElement.GetProperty("EventName").GetString());
            Assert.Equal("HostInitialization", json.RootElement.GetProperty("Operation").GetString());
            Assert.Equal("CRITICAL", json.RootElement.GetProperty("severity").GetString());
            Assert.Matches("^[A-Za-z][A-Za-z0-9_.+`]{0,191}$", json.RootElement.GetProperty("exceptionType").GetString()!);
            Assert.False(json.RootElement.TryGetProperty("State", out _));
            Assert.False(json.RootElement.TryGetProperty("Scopes", out _));
        }
        catch (Exception error) { custody.Primary = error; }
        finally
        {
            await SettleStartupCustodyAsync(custody);
            try
            {
                output.WriteLine("CONTACT_STARTUP_RESOURCE=" + JsonSerializer.Serialize(new
                {
                    owner = "contact-startup-boundary",
                    custody.Id,
                    custody.Pid,
                    custody.Birth,
                    custody.Executable,
                    custody.DeclaredExecutable,
                    custody.ExecutableObservation,
                    timeoutSeconds = 20,
                    stopTimeoutSeconds = 5,
                    readerSettlementSeconds = 5,
                    streamCharacterBound = OutputCharacterBound,
                    custody.ExitVerified,
                    stdoutSettled = custody.Stdout?.IsCompleted ?? true,
                    stderrSettled = custody.Stderr?.IsCompleted ?? true,
                    custody.ProcessDisposed,
                    custody.ReadersDisposed,
                    custody.DirectoryRemoved,
                    recoveryRequired = !custody.Released,
                    custody.LeaseExpiresUtc,
                    primaryFailureType = custody.Primary?.GetType().Name,
                    cleanupFailureTypes = custody.CleanupFailures.Select(exception => exception.GetType().Name).ToArray(),
                    persistentData = false,
                }));
            }
            catch (Exception error) { custody.CleanupFailures.Add(error); }
        }
        if (custody.FailureForCaller() is { } failureForCaller)
            ExceptionDispatchInfo.Capture(failureForCaller).Throw();
    }

    /// <summary>Checks that in-process configuration failures are contained and reported privately while setting exit code one.</summary>
    /// <param name="phase">The malformed configuration or missing-root scenario applied when invoking the actual API entrypoint in-process.</param>
    /// <returns>A task that completes when in-process startup failure reporting and exit-code assertions finish and process-wide state is restored.</returns>
    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-root")]
    public async Task Actual_entrypoint_in_process_contains_configuration_failure_and_preserves_private_report(string phase)
    {
        var root = Path.Combine(Path.GetTempPath(), Sentinel + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "appsettings.json"), "{ invalid-" + Sentinel);
        var previous = Environment.ExitCode;
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.ExitCode = 37;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var entry = typeof(Program).Assembly.EntryPoint ?? throw new InvalidOperationException("API entry point missing.");
            var contentRoot = phase == "missing-root" ? Path.Combine(root, Sentinel) : root;
            var returned = entry.Invoke(null, [new[] { "--environment=Production", "--contentRoot=" + contentRoot }]);
            if (returned is Task task) await task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var output = stderr.ToString();
            Assert.DoesNotContain(Sentinel, output, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
            var line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            using var json = JsonDocument.Parse(line);
            Assert.Equal(5102, json.RootElement.GetProperty("eventId").GetInt32());
            Assert.Equal("StartupFailure", json.RootElement.GetProperty("EventName").GetString());
            Assert.Equal("HostInitialization", json.RootElement.GetProperty("Operation").GetString());
            Assert.Equal("CRITICAL", json.RootElement.GetProperty("severity").GetString());
            Assert.False(json.RootElement.TryGetProperty("StackTrace", out _));
            Assert.False(json.RootElement.TryGetProperty("State", out _));
            Assert.False(json.RootElement.TryGetProperty("Scopes", out _));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Environment.ExitCode = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, string?> Settings()
    {
        using var rsa = RSA.Create(2048);
        return new()
        {
            ["ConnectionStrings:ContactRequestDbContext"] = "Host=127.0.0.1;Port=1;Database=startup_control;Username=fixture",
            ["Cache:RedisEnabled"] = "false",
            ["CORS:AllowedOrigins:0"] = "https://startup.example.test",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = "https://startup.example.test",
            ["Jwt:Audience"] = "startup-control",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["Observability:TracingEnabled"] = "false",
            ["Observability:RuntimeMetricsEnabled"] = "false"
        };
    }
    /// <summary>Checks normal production host interception, public liveness, protected contact reads, and preservation of the existing exit code.</summary>
    /// <returns>A task that completes when normal host liveness, authorization, and exit-code preservation assertions finish.</returns>
    [Fact]
    public async Task Normal_production_host_remains_interceptable_and_preserves_exit_code()
    {
        using var rsa = RSA.Create(2048);
        var previous = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 37;
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("ConnectionStrings:ContactRequestDbContext", "Host=127.0.0.1;Port=1;Database=startup_control;Username=fixture");
                builder.UseSetting("Cache:RedisEnabled", "false");
                builder.UseSetting("CORS:AllowedOrigins:0", "https://startup.example.test");
                builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())));
                builder.UseSetting("Jwt:Issuer", "https://startup.example.test");
                builder.UseSetting("Jwt:Audience", "startup-control");
                builder.UseSetting("Observability:TracingEnabled", "false");
                builder.UseSetting("Observability:RuntimeMetricsEnabled", "false");
            });
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/messages/liveness");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            using var protectedResponse = await client.GetAsync("/Messages/1");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
            Assert.Equal(37, Environment.ExitCode);
        }
        finally { Environment.ExitCode = previous; }
    }
    /// <summary>Checks that entrypoint host abort escapes unchanged without startup-failure output or exit-code mutation.</summary>
    /// <returns>A task that completes when the intercepted host abort is rethrown unchanged and process-wide startup state is restored.</returns>
    [Fact]
    public async Task Actual_entrypoint_rethrows_host_abort_without_emitting_private_failure_or_changing_exit_code()
    {
        var root = Path.Combine(Path.GetTempPath(), Sentinel + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "appsettings.json"), JsonSerializer.Serialize(Settings()));
        var previous = Environment.ExitCode;
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var interceptor = new HostAbortInterceptor();
        try
        {
            Environment.ExitCode = 37;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            interceptor.Enter();
            var escaped = await Record.ExceptionAsync(async () =>
            {
                var entry = typeof(Program).Assembly.EntryPoint ?? throw new InvalidOperationException("API entry point missing.");
                try
                {
                    var returned = entry.Invoke(null, [new[] { "--environment=Production", "--contentRoot=" + root }]);
                    if (returned is Task task) await task;
                }
                catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            });
            Assert.Equal(1, interceptor.Calls);
            Assert.Same(interceptor.Abort, escaped);
            Assert.Equal(37, Environment.ExitCode);
            Assert.DoesNotContain("StartupFailure", stdout.ToString() + stderr, StringComparison.Ordinal);
        }
        finally
        {
            interceptor.Exit();
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Environment.ExitCode = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class HostAbortInterceptor : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly AsyncLocal<bool> owned = new();
        private readonly IDisposable listeners;
        private readonly List<IDisposable> subscriptions = [];
        private Microsoft.Extensions.Hosting.IHost? host;
        internal Microsoft.Extensions.Hosting.HostAbortedException Abort { get; } = new();
        internal int Calls { get; private set; }
        internal HostAbortInterceptor() => listeners = DiagnosticListener.AllListeners.Subscribe(this);
        internal void Enter() => owned.Value = true;
        internal void Exit() => owned.Value = false;
        public void OnNext(DiagnosticListener listener)
        {
            if (owned.Value && listener.Name == "Microsoft.Extensions.Hosting") subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!owned.Value || value.Key != "HostBuilt") return;
            host = Assert.IsAssignableFrom<Microsoft.Extensions.Hosting.IHost>(value.Value);
            Calls++;
            throw Abort;
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
            listeners.Dispose();
            host?.Dispose();
        }
    }

    private const int OutputCharacterBound = 65536;
    private static readonly Dictionary<string, StartupCustody> RetainedStartupChildren = [];

    /// <summary>Verifies the bounded reader accepts only output within its limit.</summary>
    /// <param name="count">The synthetic output length.</param>
    /// <param name="accepted">Whether the output should be accepted.</param>
    /// <returns>A task that completes when the custody control has finished.</returns>
    [Theory]
    [InlineData(OutputCharacterBound, true)]
    [InlineData(OutputCharacterBound + 1, false)]
    public async Task StreamingReader_EnforcesBoundWithoutTruncatingAssertions(int count, bool accepted)
    {
        using var reader = new StringReader(new string('x', count));
        if (accepted) Assert.Equal(count, (await ReadBoundedAsync(reader, CancellationToken.None)).Length);
        else await Assert.ThrowsAsync<InvalidDataException>(() => ReadBoundedAsync(reader, CancellationToken.None));
    }

    /// <summary>Verifies ambiguous child startup retains custody and its primary failure.</summary>
    /// <returns>A task that completes when the custody control has finished.</returns>
    [Fact]
    public async Task PartialStartUncertainty_RetainsExactHandleDirectoryAndOriginalFailure()
    {
        var custody = ControlCustody();
        custody.StartAttempted = true;
        // No Start is actually called in this control: emulate failure before metadata is available.
        custody.StartResult = null;
        var primary = new IOException("synthetic primary startup failure");
        custody.Primary = primary;
        try
        {
            await SettleStartupCustodyAsync(custody);
            Assert.False(custody.Released);
            Assert.False(custody.ProcessDisposed);
            Assert.True(Directory.Exists(custody.Directory));
            Assert.Same(custody, RetainedStartupChildren[custody.Id]);
            var aggregate = Assert.IsType<AggregateException>(custody.FailureForCaller());
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            Assert.NotEmpty(custody.CleanupFailures);
        }
        finally
        {
            // This control never called Start; that local evidence allows releasing its unstarted handle.
            custody.StartAttempted = false;
            custody.StartResult = false;
            await SettleStartupCustodyAsync(custody);
        }
        Assert.True(custody.Released);
        Assert.False(RetainedStartupChildren.ContainsKey(custody.Id));

        var cancelledCustody = ControlCustody();
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await ContactRuntimeProcessIdentity.AwaitReceiptAsync(() => false, () => false, cancelled.Token);
        }
        catch (OperationCanceledException exception) { cancelledCustody.Primary = exception; }
        finally { await SettleStartupCustodyAsync(cancelledCustody); }
        Assert.True(cancelledCustody.Released);
        Assert.IsAssignableFrom<OperationCanceledException>(cancelledCustody.FailureForCaller());
    }

    /// <summary>Verifies cleanup waits for both owned output readers.</summary>
    /// <returns>A task that completes when the custody control has finished.</returns>
    [Fact]
    public async Task UnsettledReaders_PreventDisposalAndRemovalUntilBothActuallyFinish()
    {
        var custody = ControlCustody();
        var stdout = new GateReader();
        var stderr = new GateReader();
        custody.StdoutReader = stdout;
        custody.StderrReader = stderr;
        custody.Stdout = ReadBoundedAsync(stdout, custody.Readers.Token);
        custody.Stderr = ReadBoundedAsync(stderr, custody.Readers.Token);
        try
        {
            await SettleStartupCustodyAsync(custody);
            Assert.False(custody.ProcessDisposed);
            Assert.False(stdout.IsDisposed);
            Assert.False(stderr.IsDisposed);
            Assert.True(Directory.Exists(custody.Directory));
            Assert.Contains(custody.CleanupFailures, exception => exception is TimeoutException);
            stdout.Release();
            await custody.Stdout;
            await SettleStartupCustodyAsync(custody);
            Assert.False(custody.ProcessDisposed);
            Assert.True(Directory.Exists(custody.Directory));
            Assert.False(custody.Stderr.IsCompleted);
        }
        finally
        {
            stdout.Release();
            stderr.Release();
            await Task.WhenAll(custody.Stdout, custody.Stderr).WaitAsync(TimeSpan.FromSeconds(5));
            await SettleStartupCustodyAsync(custody);
        }
        Assert.True(custody.Released);
        Assert.True(custody.ProcessDisposed);
        Assert.True(stdout.IsDisposed);
        Assert.True(stderr.IsDisposed);
        Assert.False(Directory.Exists(custody.Directory));
    }

    /// <summary>Verifies directory cleanup errors preserve the primary failure.</summary>
    /// <returns>A task that completes when the custody control has finished.</returns>
    [Fact]
    public async Task DirectoryCleanupFailure_PreservesPrimaryAndEveryCleanupFailureForRecovery()
    {
        var custody = ControlCustody();
        var primary = new IOException("synthetic primary failure");
        var removal = new IOException("synthetic private-directory cleanup failure");
        var secondRemoval = new IOException("synthetic second private-directory cleanup failure");
        custody.Primary = primary;
        custody.DirectoryRemovalFault = removal;
        try
        {
            await SettleStartupCustodyAsync(custody);
            Assert.True(custody.ProcessDisposed); // Exit/readers already settled; handle release is safe.
            Assert.True(Directory.Exists(custody.Directory));
            Assert.False(custody.Released);
            Assert.Same(custody, RetainedStartupChildren[custody.Id]);
            var aggregate = Assert.IsType<AggregateException>(custody.FailureForCaller());
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            Assert.Contains(removal, aggregate.InnerExceptions);
            custody.DirectoryRemovalFault = secondRemoval;
            await SettleStartupCustodyAsync(custody);
            Assert.Equal(new Exception[] { primary, removal, secondRemoval },
                Assert.IsType<AggregateException>(custody.FailureForCaller()).InnerExceptions);
        }
        finally
        {
            custody.DirectoryRemovalFault = null;
            await SettleStartupCustodyAsync(custody);
        }
        Assert.True(custody.Released);
        Assert.Same(primary, Assert.IsType<AggregateException>(custody.FailureForCaller()).InnerExceptions[0]);
    }

    /// <summary>Verifies executable observation records the available module.</summary>
    [Fact]
    public void ExecutableObservation_PresentModuleRecordsObservedIdentity()
    {
        var observation = ObserveExecutable(() => "/observed/dotnet", () => false);
        Assert.Equal("/observed/dotnet", observation.Executable);
        Assert.False(observation.Exited);
    }

    /// <summary>Verifies a missing module requires a confirmed process exit.</summary>
    [Fact]
    public void ExecutableObservation_AbsentModuleRequiresVerifiedExit()
    {
        var observation = ObserveExecutable(() => null, () => true);
        Assert.Null(observation.Executable);
        Assert.True(observation.Exited);
        var empty = ObserveExecutable(() => string.Empty, () => true);
        Assert.Equal(string.Empty, empty.Executable);
        Assert.True(empty.Exited);
    }

    /// <summary>Verifies a live process without an observable module is rejected.</summary>
    [Fact]
    public void ExecutableObservation_AbsentLiveModuleFailsWithoutLaunchFallback()
    {
        Assert.Throws<InvalidOperationException>(() => ObserveExecutable(() => null, () => false));
        Assert.Throws<InvalidOperationException>(() => ObserveExecutable(() => string.Empty, () => false));
    }

    /// <summary>Verifies an exit observation failure retains its cause.</summary>
    [Fact]
    public void ExecutableObservation_ExitQueryFailurePreservesCause()
    {
        var cause = new IOException("exit observation failed");
        Assert.Same(cause, Assert.Throws<IOException>(() => ObserveExecutable(() => null, () => throw cause)));
    }

    /// <summary>Verifies a module observation failure retains its cause.</summary>
    [Fact]
    public void ExecutableObservation_ModuleQueryFailurePreservesCause()
    {
        var cause = new InvalidOperationException("module observation failed");
        var exitQueried = false;
        Assert.Same(cause, Assert.Throws<InvalidOperationException>(() => ObserveExecutable(() => throw cause,
            () => { exitQueried = true; return true; })));
        Assert.False(exitQueried);
    }

    /// <summary>Verifies a process exit prevents a stale termination decision.</summary>
    [Fact]
    public void ExecutableObservation_ExitBetweenObservationsPreventsTerminationDecision()
    {
        var first = ObserveExecutable(() => "/observed/dotnet", () => false);
        var cleanup = ObserveExecutable(() => null, () => true);
        Assert.False(first.Exited);
        Assert.True(cleanup.Exited);
        Assert.Null(cleanup.Executable);
        Assert.False(ShouldTerminateAfterObservation(cleanup.Exited,
            () => throw new InvalidOperationException("Verified exit must short-circuit termination.")));
        Assert.False(ShouldTerminateAfterObservation(first.Exited, () => true));
        Assert.True(ShouldTerminateAfterObservation(first.Exited, () => false));
    }

    /// <summary>Verifies cleanup observation failures follow the primary error.</summary>
    [Fact]
    public void ExecutableObservation_PrimaryPrecedesCleanupObservationFailure()
    {
        using var process = new Process();
        var custody = new StartupCustody(process, string.Empty);
        using var readers = custody.Readers;
        var primary = new IOException("original failure");
        custody.Primary = primary;
        var cleanup = Assert.Throws<InvalidOperationException>(() => ObserveExecutable(() => null, () => false));
        custody.CleanupFailures.Add(cleanup);
        var failure = Assert.IsType<AggregateException>(custody.FailureForCaller());
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.Same(cleanup, failure.InnerExceptions[1]);
    }

    /// <summary>Verifies reader failures and the primary error are preserved.</summary>
    /// <returns>A task that completes when the custody control has finished.</returns>
    [Fact]
    public async Task ReaderFailure_PreservesPrimaryAndOriginalReaderError()
    {
        var custody = ControlCustody();
        var primary = new IOException("synthetic primary failure");
        var readerFailure = new IOException("synthetic reader failure");
        custody.Primary = primary;
        custody.StdoutReader = new FaultReader(readerFailure);
        custody.Stdout = ReadBoundedAsync(custody.StdoutReader, custody.Readers.Token);
        await SettleStartupCustodyAsync(custody);
        Assert.True(custody.Released);
        var aggregate = Assert.IsType<AggregateException>(custody.FailureForCaller());
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Contains(readerFailure, aggregate.InnerExceptions);
    }

    /// <summary>Verifies a confirmed unstarted process can be released safely.</summary>
    /// <returns>A task that completes when the custody control has finished.</returns>
    [Fact]
    public async Task FailedStart_ReleasesKnownUnstartedProcessAndPreservesPrimary()
    {
        var custody = ControlCustody();
        custody.StartAttempted = true;
        custody.StartResult = false;
        var primary = new IOException("synthetic failed start");
        custody.Primary = primary;
        await SettleStartupCustodyAsync(custody);
        Assert.True(custody.Released);
        Assert.Same(primary, custody.FailureForCaller());
        Assert.False(Directory.Exists(custody.Directory));
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return text.ToString();
            if (text.Length + count > OutputCharacterBound)
                throw new InvalidDataException("Startup diagnostic stream exceeded its fixed bound.");
            text.Append(buffer, 0, count);
        }
    }

    private static (string? Executable, bool Exited) ObserveExecutable(Func<string?> module, Func<bool> exited)
    {
        // Declared launch paths are never evidence of a live process image.
        // Query exceptions propagate unchanged; only an absent module with verified exit is expected.
        var executable = module();
        var exitVerified = exited();
        if (string.IsNullOrEmpty(executable) && !exitVerified)
            throw new InvalidOperationException("Live owned startup child executable could not be observed.");
        return (executable, exitVerified);
    }

    private static bool ObserveOwnedExecutable(StartupCustody custody)
    {
        var observation = ObserveExecutable(() => custody.Process.MainModule?.FileName, () => custody.Process.HasExited);
        if (!string.IsNullOrEmpty(observation.Executable)) custody.Executable = observation.Executable;
        custody.ExecutableObservation = string.IsNullOrEmpty(observation.Executable) ? "exited-before-module-observation" : "observed-module";
        return observation.Exited;
    }

    private static bool ShouldTerminateAfterObservation(bool exitedDuringObservation, Func<bool> exited)
        => !exitedDuringObservation && !exited();

    private static async Task SettleStartupCustodyAsync(StartupCustody custody)
    {
        if (!custody.ProcessDisposed)
        {
            try
            {
                custody.ExitVerified = !custody.StartAttempted || custody.StartResult == false || custody.Process.HasExited;
                if (!custody.ExitVerified)
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { custody.Process.CloseMainWindow(); } // Graceful attempt on the retained owned handle first.
                    catch (Exception exception) { custody.CleanupFailures.Add(exception); }
                    using var graceful = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    graceful.CancelAfter(TimeSpan.FromSeconds(1));
                    try { await custody.Process.WaitForExitAsync(graceful.Token); }
                    catch (OperationCanceledException) when (graceful.IsCancellationRequested && !stop.IsCancellationRequested) { }
                    if (!custody.Process.HasExited)
                    {
                        // Recover metadata through this same handle if an earlier metadata read failed.
                        custody.Pid ??= custody.Process.Id;
                        custody.Birth ??= custody.Process.StartTime.ToUniversalTime();
                        var exitedDuringObservation = ObserveOwnedExecutable(custody);
                        if (custody.Process.StartTime.ToUniversalTime() != custody.Birth)
                            throw new InvalidOperationException("Owned startup child birth changed; termination withheld.");
                        if (ShouldTerminateAfterObservation(exitedDuringObservation, () => custody.Process.HasExited))
                            custody.Process.Kill(); // No PID lookup, names or recursive tree selector.
                        await custody.Process.WaitForExitAsync(stop.Token);
                    }
                    custody.ExitVerified = custody.Process.HasExited;
                }
            }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
            finally
            {
                if (custody.StartAttempted && custody.StartResult != false)
                {
                    try { custody.ExitVerified = custody.Process.HasExited; }
                    catch (Exception exception) { custody.ExitVerified = false; custody.CleanupFailures.Add(exception); }
                }
            }
            try { custody.Readers.Cancel(); }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
            var readers = new[] { custody.Stdout, custody.Stderr }.OfType<Task<string>>().ToArray();
            try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException || readers.Any(task => !task.IsCompleted))
                    custody.CleanupFailures.Add(exception);
            }
            foreach (var task in readers.Where(task => task.IsFaulted))
                foreach (var exception in task.Exception!.InnerExceptions)
                    if (!custody.CleanupFailures.Contains(exception)) custody.CleanupFailures.Add(exception);
            if (custody.ExitVerified && readers.All(task => task.IsCompleted))
            {
                if (!custody.StdoutReaderDisposed)
                {
                    try { custody.StdoutReader?.Dispose(); custody.StdoutReaderDisposed = true; }
                    catch (Exception exception) { custody.CleanupFailures.Add(exception); }
                }
                if (!custody.StderrReaderDisposed)
                {
                    try { custody.StderrReader?.Dispose(); custody.StderrReaderDisposed = true; }
                    catch (Exception exception) { custody.CleanupFailures.Add(exception); }
                }
                try
                {
                    if (custody.StdoutReaderDisposed && custody.StderrReaderDisposed)
                    {
                        custody.Process.Dispose();
                        custody.ProcessDisposed = true;
                    }
                }
                catch (Exception exception) { custody.CleanupFailures.Add(exception); }
            }
        }
        if (custody.ExitVerified && (custody.Stdout?.IsCompleted ?? true) && (custody.Stderr?.IsCompleted ?? true) && !custody.ReadersDisposed)
        {
            try { custody.Readers.Dispose(); custody.ReadersDisposed = true; }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
        }
        if (custody.ProcessDisposed && custody.ReadersDisposed && !custody.DirectoryRemoved)
        {
            try
            {
                if (custody.DirectoryRemovalFault is { } fault) throw fault;
                Directory.Delete(custody.Directory, recursive: true);
                custody.DirectoryRemoved = !Directory.Exists(custody.Directory);
            }
            catch (Exception exception) { custody.CleanupFailures.Add(exception); }
        }
        if (custody.Released) RetainedStartupChildren.Remove(custody.Id);
        else RetainedStartupChildren[custody.Id] = custody;
    }

    private static StartupCustody ControlCustody()
    {
        var directory = Path.Combine(Path.GetTempPath(), "contact-startup-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new StartupCustody(new Process(), directory) { StartResult = false };
    }

    private sealed class StartupCustody(Process process, string directory)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public Process Process { get; } = process;
        public string Directory { get; } = directory;
        public CancellationTokenSource Readers { get; } = new(TimeSpan.FromSeconds(20));
        public DateTimeOffset LeaseExpiresUtc { get; } = DateTimeOffset.UtcNow.AddSeconds(30);
        public bool StartAttempted { get; set; }
        public bool? StartResult { get; set; }
        public int? Pid { get; set; }
        public DateTime? Birth { get; set; }
        public string? Executable { get; set; }
        public string? DeclaredExecutable { get; set; }
        public string? ExecutableObservation { get; set; }
        public Task<string>? Stdout { get; set; }
        public Task<string>? Stderr { get; set; }
        public TextReader? StdoutReader { get; set; }
        public TextReader? StderrReader { get; set; }
        public bool StdoutReaderDisposed { get; set; }
        public bool StderrReaderDisposed { get; set; }
        public Exception? Primary { get; set; }
        public List<Exception> CleanupFailures { get; } = [];
        public Exception? DirectoryRemovalFault { get; set; } // Only deterministic fixture controls inject this.
        public bool ExitVerified { get; set; }
        public bool ProcessDisposed { get; set; }
        public bool ReadersDisposed { get; set; }
        public bool DirectoryRemoved { get; set; }
        public bool Released => ProcessDisposed && ReadersDisposed && DirectoryRemoved;
        public Exception? FailureForCaller()
        {
            var failures = (Primary is null ? Enumerable.Empty<Exception>() : new[] { Primary! }).Concat(CleanupFailures).ToArray();
            return failures.Length switch { 0 => null, 1 => failures[0], _ => new AggregateException("Startup fixture failure and owned cleanup evidence.", failures) };
        }
    }


    private sealed class GateReader : TextReader
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed { get; private set; }
        public void Release() => gate.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            // Deliberately ignores cancellation, proving that a cancellation request is not settlement.
            await gate.Task;
            return 0;
        }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
    private sealed class FaultReader(Exception failure) : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(failure);
    }


}
