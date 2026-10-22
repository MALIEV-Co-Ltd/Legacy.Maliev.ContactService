using System.Collections;
using System.Collections.ObjectModel;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

/// <summary>Checks fixture task joining, idempotent disposal, receipt identity, and preservation of primary failures during cleanup.</summary>
public sealed class ContactFixtureLifecycleTests
{
    /// <summary>Checks that a captured container identity remains available for receipts after its resource getter becomes unusable.</summary>
    /// <returns>A task that completes after verifying that a captured container identity remains available for receipts after its resource getter becomes unusable.</returns>
    [Fact]
    public async Task CapturedIdentity_SurvivesDisposedGetterAndReceiptWriting()
    {
        var resource = new ReceiptResource();
        var identity = new ContactFixtureReceiptIdentity();
        var receipts = new List<string?>();
        var lifecycle = new ContactFixtureLifecycle(
            async token => { await resource.StartAsync(token); identity.Capture(resource.Id); },
            _ => Task.CompletedTask,
            async () => { await resource.DisposeAsync(); receipts.Add(identity.ContainerId); },
            (_, _) => { });
        await lifecycle.InitializeAsync(CancellationToken.None);
        await lifecycle.DisposeAsync();
        Assert.Equal(1, resource.GetterCalls);
        Assert.Throws<InvalidOperationException>(() => resource.Id);
        Assert.Equal(ReceiptResource.ActualId, Assert.Single(receipts));
        Assert.Equal(ReceiptResource.ActualId, identity.ContainerId);
    }

    /// <summary>Checks that disposal failure preserves the primary migration failure and records the already captured container identity.</summary>
    /// <returns>A task that completes after verifying that disposal failure preserves the primary migration failure and records the already captured container identity.</returns>
    [Fact]
    public async Task CapturedIdentity_DisposalFaultIsRecordedWithoutReplacingPrimary()
    {
        var resource = new ReceiptResource();
        var identity = new ContactFixtureReceiptIdentity();
        var primary = new IOException("synthetic migration failure");
        var cleanup = new InvalidOperationException("synthetic dispose failure");
        var failures = new List<(string Phase, Exception? Failure, string? ContainerId)>();
        var lifecycle = new ContactFixtureLifecycle(
            async token => { await resource.StartAsync(token); identity.Capture(resource.Id); },
            _ => Task.FromException(primary),
            async () => { await resource.DisposeAsync(); throw cleanup; },
            (phase, failure) => failures.Add((phase, failure, identity.ContainerId)));
        var observed = await Assert.ThrowsAsync<IOException>(() => lifecycle.InitializeAsync(CancellationToken.None));
        Assert.Same(primary, observed);
        Assert.Same(cleanup, observed.Data["ContactFixtureCleanupFailure"]);
        var disposeFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.DisposeAsync());
        Assert.Same(cleanup, disposeFailure);
        Assert.Contains(failures, row => row.Phase == "cleanup-failed" && ReferenceEquals(row.Failure, cleanup)
            && row.ContainerId == ReceiptResource.ActualId);
        Assert.Equal(1, resource.GetterCalls);
        Assert.Equal(ReceiptResource.ActualId, identity.ContainerId);
    }

    /// <summary>Checks that failed startup leaves receipt identity unknown without probing an unavailable resource getter.</summary>
    /// <returns>A task that completes after verifying that failed startup leaves receipt identity unknown without probing an unavailable resource getter.</returns>
    [Fact]
    public async Task UnsuccessfulStartup_LeavesReceiptIdentityUnknownWithoutGetterProbe()
    {
        var resource = new ReceiptResource();
        var identity = new ContactFixtureReceiptIdentity();
        var primary = new IOException("synthetic startup failure");
        var receipts = new List<string?>();
        var lifecycle = new ContactFixtureLifecycle(_ => Task.FromException(primary), _ => Task.CompletedTask,
            async () => { await resource.DisposeAsync(); receipts.Add(identity.ContainerId); }, (_, _) => { });
        var observed = await Assert.ThrowsAsync<IOException>(() => lifecycle.InitializeAsync(CancellationToken.None));
        Assert.Same(primary, observed);
        await lifecycle.DisposeAsync();
        Assert.Null(Assert.Single(receipts));
        Assert.Null(identity.ContainerId);
        Assert.Equal(0, resource.GetterCalls);
    }

    private sealed class ReceiptResource
    {
        public const string ActualId = "synthetic-started-container";
        private bool started;
        private bool disposed;
        public int GetterCalls { get; private set; }
        public string Id
        {
            get
            {
                GetterCalls++;
                if (!started || disposed) { throw new InvalidOperationException("Synthetic resource getter unavailable."); }
                return ActualId;
            }
        }
        public Task StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            started = true;
            return Task.CompletedTask;
        }
        public Task DisposeAsync()
        {
            disposed = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Checks that immutable exception data and a throwing observer cannot replace the primary failure or skip cleanup.</summary>
    /// <param name="throwingData">Whether the primary exception Data getter throws rather than returning a read-only diagnostic dictionary.</param>
    /// <returns>A task that completes after verifying that immutable exception data and a throwing observer cannot replace the primary failure or skip cleanup.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmutableExceptionDataAndThrowingObserver_PreserveOriginalAndCleanup(bool throwingData)
    {
        var primary = new ImmutableDiagnosticException(throwingData);
        var cleanup = new IOException("synthetic cleanup failure");
        var disposals = 0;
        var events = new List<string>();
        var lifecycle = new ContactFixtureLifecycle(_ => Task.FromException(primary), _ => Task.CompletedTask,
            () => { disposals++; return Task.FromException(cleanup); },
            (phase, _) => { events.Add(phase); throw new IOException("synthetic evidence failure"); });
        var observed = await Assert.ThrowsAsync<ImmutableDiagnosticException>(() => lifecycle.InitializeAsync(CancellationToken.None));
        Assert.Same(primary, observed);
        Assert.Contains("start-failed-settled", events);
        Assert.Contains("cleanup-failed", events);
        var disposeFailure = await Assert.ThrowsAsync<IOException>(() => lifecycle.DisposeAsync());
        Assert.Same(cleanup, disposeFailure);
        Assert.Equal(1, disposals);
    }

    /// <summary>Checks that a throwing failure observer cannot skip joining the real startup task before disposal.</summary>
    /// <returns>A task that completes after verifying that a throwing failure observer cannot skip joining the real startup task before disposal.</returns>
    [Fact]
    public async Task ThrowingFailureObserver_CannotSkipActualTaskJoin()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = 0;
        using var expiry = new CancellationTokenSource();
        var lifecycle = new ContactFixtureLifecycle(_ => { entered.SetResult(); return actual.Task; },
            _ => Task.CompletedTask, () => { disposals++; return Task.CompletedTask; },
            (phase, _) =>
            {
                if (phase == "start-wait-failed")
                {
                    waitFailed.SetResult();
                    throw new IOException("synthetic evidence failure");
                }
            });
        var initializing = lifecycle.InitializeAsync(expiry.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            expiry.Cancel();
            await waitFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposing = lifecycle.DisposeAsync();
            Assert.False(initializing.IsCompleted);
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, disposals);
            actual.SetResult();
            var primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing);
            Assert.IsType<IOException>(primary.Data["ContactFixtureEvidenceFailure"]);
            await disposing;
            Assert.Equal(1, disposals);
        }
        finally
        {
            actual.TrySetResult();
            _ = await Record.ExceptionAsync(() => initializing);
            _ = await Record.ExceptionAsync(() => lifecycle.DisposeAsync());
        }
    }

    /// <summary>Checks that concurrent early disposal waits for initialization and shares exactly one cleanup task.</summary>
    /// <param name="migration">Whether the pending operation is database migration rather than container startup.</param>
    /// <param name="lateFault">Whether the pending operation faults after disposal begins rather than completing successfully.</param>
    /// <returns>A task that completes after verifying that concurrent early disposal waits for initialization and shares exactly one cleanup task.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EarlyConcurrentDisposal_JoinsInitializationAndDisposesOnce(bool migration, bool lateFault)
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new InvalidOperationException("synthetic late operation failure");
        var disposals = 0;
        Task Pending(CancellationToken token) { entered.SetResult(); return actual.Task; }
        var lifecycle = New(migration ? _ => Task.CompletedTask : Pending,
            migration ? Pending : _ => Task.CompletedTask,
            () => { Interlocked.Increment(ref disposals); return Task.CompletedTask; });
        var initializing = lifecycle.InitializeAsync(CancellationToken.None);
        Task<Task[]>[] requests = [];
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            requests = Enumerable.Range(0, 16).Select(_ => Task.Run(() => new[] { lifecycle.DisposeAsync() })).ToArray();
            var owners = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            var disposing = owners[0][0];
            Assert.All(owners, owner => Assert.Same(disposing, owner[0]));
            Assert.False(disposing.IsCompleted);
            Assert.False(initializing.IsCompleted);
            Assert.Equal(0, disposals);
            if (lateFault)
            {
                actual.SetException(primary);
                var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => initializing);
                Assert.Same(primary, observed);
            }
            else
            {
                actual.SetResult();
                await initializing;
            }
            await disposing;
            Assert.Equal(1, disposals);
        }
        finally
        {
            actual.TrySetResult();
            _ = await Record.ExceptionAsync(() => initializing);
            await Task.WhenAll(requests);
            _ = await Record.ExceptionAsync(() => lifecycle.DisposeAsync());
        }
    }

    /// <summary>Checks that disposing an uninitialized fixture prevents subsequent startup and performs cleanup once.</summary>
    /// <returns>A task that completes after verifying that disposing an uninitialized fixture prevents subsequent startup and performs cleanup once.</returns>
    [Fact]
    public async Task DisposalBeforeInitialization_PreventsLaterStartup()
    {
        var starts = 0;
        var disposals = 0;
        var lifecycle = New(_ => { starts++; return Task.CompletedTask; }, _ => Task.CompletedTask,
            () => { disposals++; return Task.CompletedTask; });
        await lifecycle.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lifecycle.InitializeAsync(CancellationToken.None));
        Assert.Equal(0, starts);
        Assert.Equal(1, disposals);
    }

    /// <summary>Checks that concurrent initialization calls retain the same task and start the underlying resource once.</summary>
    /// <returns>A task that completes after verifying that concurrent initialization calls retain the same task and start the underlying resource once.</returns>
    [Fact]
    public async Task ConcurrentInitialization_SharesOneRetainedTask()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var lifecycle = New(_ => { Interlocked.Increment(ref starts); entered.SetResult(); return actual.Task; },
            _ => Task.CompletedTask, () => Task.CompletedTask);
        var first = lifecycle.InitializeAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = lifecycle.InitializeAsync(CancellationToken.None);
            Assert.Same(first, second);
            Assert.Equal(1, starts);
            actual.SetResult();
            await first;
        }
        finally
        {
            actual.TrySetResult();
            _ = await Record.ExceptionAsync(() => first);
            _ = await Record.ExceptionAsync(() => lifecycle.DisposeAsync());
        }
    }

    /// <summary>Checks that repeated disposal after successful initialization releases the resource exactly once.</summary>
    /// <returns>A task that completes after verifying that repeated disposal after successful initialization releases the resource exactly once.</returns>
    [Fact]
    public async Task SuccessfulLifecycle_DisposesExactlyOnce()
    {
        var disposals = 0;
        var lifecycle = New(_ => Task.CompletedTask, _ => Task.CompletedTask,
            () => { disposals++; return Task.CompletedTask; });
        await lifecycle.InitializeAsync(CancellationToken.None);
        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync();
        Assert.Equal(1, disposals);
    }

    /// <summary>Checks that startup or migration failure triggers one disposal while preserving the original exception.</summary>
    /// <param name="migrationFails">Whether the synthetic primary failure occurs during migration rather than resource startup.</param>
    /// <returns>A task that completes after verifying that startup or migration failure triggers one disposal while preserving the original exception.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialStartupFailure_DisposesAndPreservesPrimary(bool migrationFails)
    {
        var primary = new InvalidOperationException("synthetic primary");
        var disposals = 0;
        var lifecycle = New(_ => migrationFails ? Task.CompletedTask : Task.FromException(primary),
            _ => migrationFails ? Task.FromException(primary) : Task.CompletedTask,
            () => { disposals++; return Task.CompletedTask; });
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.InitializeAsync(CancellationToken.None));
        Assert.Same(primary, observed);
        await lifecycle.DisposeAsync();
        Assert.Equal(1, disposals);
    }

    /// <summary>Checks that cancellation waits for an operation that ignores its token before beginning cleanup.</summary>
    /// <param name="migration">Whether the cancellation-ignoring task represents migration rather than resource startup.</param>
    /// <returns>A task that completes after verifying that cancellation waits for an operation that ignores its token before beginning cleanup.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationIgnoringOperation_IsJoinedBeforeCleanup(bool migration)
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var expiry = new CancellationTokenSource();
        var disposals = 0;
        Task Ignoring(CancellationToken token)
        {
            entered.SetResult();
            return actual.Task;
        }
        var lifecycle = new ContactFixtureLifecycle(migration ? _ => Task.CompletedTask : Ignoring,
            migration ? Ignoring : _ => Task.CompletedTask,
            () => { disposals++; return Task.CompletedTask; },
            (phase, _) => { if (phase.EndsWith("-wait-failed", StringComparison.Ordinal)) { canceledWait.TrySetResult(); } });
        var initializing = lifecycle.InitializeAsync(expiry.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            expiry.Cancel();
            await canceledWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initializing.IsCompleted);
            Assert.Equal(0, disposals);
            actual.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing);
            Assert.Equal(1, disposals);
        }
        finally
        {
            actual.TrySetResult();
            _ = await Record.ExceptionAsync(() => initializing);
        }
    }

    /// <summary>Checks that a task fault observed after cancellation is recorded without replacing the cancellation exception.</summary>
    /// <returns>A task that completes after verifying that a task fault observed after cancellation is recorded without replacing the cancellation exception.</returns>
    [Fact]
    public async Task LateFaultAfterCancellation_IsObservedWithoutReplacingCancellation()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new InvalidOperationException("synthetic late failure");
        using var expiry = new CancellationTokenSource();
        var events = new List<string>();
        var waitFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new ContactFixtureLifecycle(_ => { entered.SetResult(); return actual.Task; }, _ => Task.CompletedTask,
            () => Task.CompletedTask, (phase, _) =>
            {
                events.Add(phase);
                if (phase == "start-wait-failed") { waitFailed.SetResult(); }
            });
        var initializing = lifecycle.InitializeAsync(expiry.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            expiry.Cancel();
            // Cancellation must have won the proxy wait before the actual task faults.
            await waitFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initializing.IsCompleted);
            actual.SetException(late);
            var primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing);
            Assert.Same(late, primary.Data["ContactFixtureLateOperationFailure"]);
            Assert.Contains("start-late-failed", events);
            Assert.Equal("cleanup-settled", events[^1]);
        }
        finally
        {
            actual.TrySetResult();
            _ = await Record.ExceptionAsync(() => initializing);
        }
    }

    /// <summary>Checks that a cleanup exception is attached to the primary initialization failure and remains observable on disposal.</summary>
    /// <returns>A task that completes after verifying that a cleanup exception is attached to the primary initialization failure and remains observable on disposal.</returns>
    [Fact]
    public async Task CleanupFailure_IsRecordedWithoutReplacingPrimary()
    {
        var primary = new InvalidOperationException("synthetic primary");
        var cleanup = new IOException("synthetic cleanup failure");
        var lifecycle = New(_ => Task.FromException(primary), _ => Task.CompletedTask,
            () => Task.FromException(cleanup));
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.InitializeAsync(CancellationToken.None));
        Assert.Same(primary, observed);
        Assert.Same(cleanup, observed.Data["ContactFixtureCleanupFailure"]);
        await Assert.ThrowsAsync<IOException>(() => lifecycle.DisposeAsync());
    }

    private static ContactFixtureLifecycle New(Func<CancellationToken, Task> start,
        Func<CancellationToken, Task> migrate, Func<Task> dispose) => new(start, migrate, dispose, (_, _) => { });

    private sealed class ImmutableDiagnosticException(bool throwingData) : Exception("synthetic primary")
    {
        private readonly IDictionary readOnly = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>());
        public override IDictionary Data => throwingData ? throw new IOException("synthetic Data getter failure") : readOnly;
    }
}
