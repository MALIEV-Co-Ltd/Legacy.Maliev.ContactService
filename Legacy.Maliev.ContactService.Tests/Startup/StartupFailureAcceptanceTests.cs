using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Legacy.Maliev.ContactService.Tests.Startup;

[CollectionDefinition("Contact startup state", DisableParallelization = true)]
public sealed class StartupCollection;

[Collection("Contact startup state")]
public sealed class StartupFailureAcceptanceTests
{
    private const string Sentinel = "private-startup-test-sentinel";

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-root")]
    [InlineData("host-start")]
    public async Task Actual_entrypoint_reports_private_failure_and_exits_one(string phase)
    {
        var root = Path.Combine(Path.GetTempPath(), Sentinel + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
            foreach (var argument in new[] { api, "--environment=Production", "--contentRoot=" +
                (phase == "missing-root" ? Path.Combine(root, Sentinel) : root) }) start.ArgumentList.Add(argument);
            if (phase == "host-start") start.ArgumentList.Add("--urls=http://127.0.0.1:0/" + Sentinel);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Owned test process did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                throw new TimeoutException("Owned child startup timed out; this is not behavioral failure evidence.");
            }
            Assert.Equal(1, child.ExitCode);
            var capturedStdout = await stdout;
            if (phase != "host-start") Assert.Equal(string.Empty, capturedStdout);
            var raw = capturedStdout + await stderr;
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
        finally { Directory.Delete(root, recursive: true); }
    }

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
}
