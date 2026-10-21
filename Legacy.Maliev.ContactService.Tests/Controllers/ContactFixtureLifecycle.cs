namespace Legacy.Maliev.ContactService.Tests.Controllers;

/// <summary>Retains and joins each actual operation before releasing fixture ownership.</summary>
internal sealed class ContactFixtureLifecycle(
    Func<CancellationToken, Task> start,
    Func<CancellationToken, Task> migrate,
    Func<Task> dispose,
    Action<string, Exception?> observe)
{
    private readonly object gate = new();
    private Task? initialization;
    private Task? disposal;
    private Task? cleanup;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (disposal is not null)
            {
                return Task.FromException(new ObjectDisposedException(nameof(ContactFixtureLifecycle)));
            }
            // Publish the retained initialization task while holding the same gate
            // used by disposal. Its driver cannot reenter disposal before publication.
            return initialization ??= Task.Run(() => InitializeOwnedAsync(cancellationToken));
        }
    }

    private async Task InitializeOwnedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunSettledAsync(start, "start", cancellationToken);
            await RunSettledAsync(migrate, "migration", cancellationToken);
        }
        catch (Exception primary)
        {
            try
            {
                // Failure cleanup joins actual operations in RunSettledAsync first.
                // It must not join its own initialization task through DisposeAsync.
                await CleanupOnceAsync();
            }
            catch (Exception cleanup)
            {
                TryAttach(primary, "ContactFixtureCleanupFailure", cleanup);
                RecordFailure(primary, "cleanup-failed", cleanup);
            }
            throw;
        }
    }

    public Task DisposeAsync()
    {
        lock (gate)
        {
            var ownedInitialization = initialization;
            return disposal ??= Task.Run(() => DisposeAfterInitializationAsync(ownedInitialization));
        }
    }

    private async Task DisposeAfterInitializationAsync(Task? ownedInitialization)
    {
        if (ownedInitialization is not null)
        {
            try
            {
                await ownedInitialization;
            }
            catch (Exception)
            {
                // Initialization remains the primary failure on its retained task.
                // External disposal still joins that task and observes cleanup itself.
            }
        }
        await CleanupOnceAsync();
    }

    private Task CleanupOnceAsync()
    {
        lock (gate)
        {
            return cleanup ??= Task.Run(DisposeOwnedAsync);
        }
    }

    private async Task DisposeOwnedAsync()
    {
        await dispose();
        observe("cleanup-settled", null);
    }

    private async Task RunSettledAsync(Func<CancellationToken, Task> operation, string phase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep the real task. A canceled WaitAsync proxy does not release ownership.
        var actual = operation(cancellationToken);
        try
        {
            await actual.WaitAsync(cancellationToken);
        }
        catch (Exception primary)
        {
            RecordFailure(primary, phase + "-wait-failed", primary);
            try
            {
                await actual;
            }
            catch (Exception late)
            {
                if (!ReferenceEquals(primary, late))
                {
                    TryAttach(primary, "ContactFixtureLateOperationFailure", late);
                    RecordFailure(primary, phase + "-late-failed", late);
                }
            }
            RecordFailure(primary, phase + "-failed-settled", primary);
            throw;
        }
        observe(phase + "-settled", null);
    }

    private void RecordFailure(Exception primary, string phase, Exception failure)
    {
        try
        {
            observe(phase, failure);
        }
        catch (Exception evidenceFailure)
        {
            TryAttach(primary, "ContactFixtureEvidenceFailure", evidenceFailure);
        }
    }

    private static void TryAttach(Exception primary, string key, Exception secondary)
    {
        try
        {
            primary.Data[key] = secondary;
        }
        catch (Exception)
        {
            // An exception can expose a throwing or read-only Data dictionary.
            // Diagnostic attachment is best effort and cannot change task ownership.
        }
    }
}
