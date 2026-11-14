using System.Text.Json;

/// <summary>Test-only startup hook verifies the isolated child actually loaded both frameworks.</summary>
public static class StartupHook
{
    /// <summary>Writes only to the caller-owned test directory and never changes startup output.</summary>
    public static void Initialize()
    {
        var destination = Environment.GetEnvironmentVariable("CONTACT_RUNTIME_RECEIPT");
        if (string.IsNullOrEmpty(destination) || !Path.IsPathFullyQualified(destination))
            throw new InvalidOperationException("Runtime receipt destination is required.");
        var run = Environment.GetEnvironmentVariable("CONTACT_RUNTIME_RUN");
        if (string.IsNullOrEmpty(run)) throw new InvalidOperationException("Run identity is required.");
        var hookPath = typeof(StartupHook).Assembly.Location;
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Runtime qualification requires Linux proc identity.");
        var startTicks = ContactRuntimeProcessIdentity.ParseStartTicks(File.ReadAllText("/proc/self/stat"));
        var core = typeof(object).Assembly;
        var aspNet = typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly;
        using (var file = new FileStream(destination + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(file, new
            {
                pid = Environment.ProcessId,
                startTicks,
                run,
                hookPath,
                hookSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hookPath))),
                coreMajor = Environment.Version.Major,
                aspNetMajor = aspNet.GetName().Version?.Major,
                corePath = core.Location,
                aspNetPath = aspNet.Location,
            });
        File.Move(destination + ".pending", destination);
        var acknowledgement = destination + ".ack";
        var expected = $"{run}|{Environment.ProcessId}|{startTicks}";
        ContactRuntimeProcessIdentity.RequireAcknowledgement(
            () => File.Exists(acknowledgement) ? File.ReadAllText(acknowledgement) : null,
            expected, TimeSpan.FromSeconds(10));
    }
}

internal static class ContactRuntimeProcessIdentity
{
    internal static void RequireAcknowledgement(Func<string?> read, string expected, TimeSpan timeout, Func<TimeSpan>? elapsed = null)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        elapsed ??= () => deadline.Elapsed;
        while (true)
        {
            if (elapsed() >= timeout) throw new TimeoutException("Runtime identity acknowledgement timed out.");
            var value = read();
            if (elapsed() >= timeout) throw new TimeoutException("Runtime identity acknowledgement timed out.");
            if (value is not null)
            {
                if (value != expected) throw new InvalidOperationException("Runtime identity acknowledgement is invalid.");
                return;
            }
            Thread.Sleep(10);
        }
    }

    internal static async Task AwaitReceiptAsync(Func<bool> exists, Func<bool> exited, CancellationToken cancellationToken)
    {
        while (!exists())
        {
            if (exited()) throw new InvalidOperationException("Runtime identity child exited before receipt.");
            await Task.Delay(10, cancellationToken);
        }
        if (exited()) throw new InvalidOperationException("Runtime identity child exited before inspection.");
    }

    internal static ulong ParseStartTicks(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) throw new InvalidOperationException("Malformed proc stat.");
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var ticks) || ticks == 0)
            throw new InvalidOperationException("Malformed proc start ticks.");
        return ticks;
    }
}
