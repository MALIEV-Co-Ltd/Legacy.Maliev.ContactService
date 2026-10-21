using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Moq;

namespace Legacy.Maliev.ContactService.Tests.Data;

/// <summary>Checks that distributed contact cache reads, writes, and invalidation propagate cancellation exceptions.</summary>
public sealed class DistributedContactRequestCacheTests
{
    private static readonly ContactRequestResponse Request = new(
        1,
        "Nat",
        "Tester",
        "MALIEV",
        "nat@example.invalid",
        "000",
        "TH",
        "Need a quote",
        DateTime.UtcNow,
        null);

    /// <summary>Checks that cancellation from the distributed cache read reaches the contact cache caller.</summary>
    /// <returns>A task that completes after asserting propagation of OperationCanceledException from the cache read.</returns>
    [Fact]
    public async Task GetAllAsync_does_not_swallow_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var cache = new Mock<IDistributedCache>();
        cache.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var sut = new DistributedContactRequestCache(cache.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.GetAllAsync(cancellation.Token));
    }

    /// <summary>Checks that cancellation from the distributed cache write reaches the contact cache caller.</summary>
    /// <returns>A task that completes after asserting propagation of OperationCanceledException from the cache write.</returns>
    [Fact]
    public async Task SetAllAsync_does_not_swallow_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var cache = new Mock<IDistributedCache>();
        cache.Setup(value => value.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var sut = new DistributedContactRequestCache(cache.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.SetAllAsync([Request], cancellation.Token));
    }

    /// <summary>Checks that cancellation from distributed cache removal reaches the contact cache invalidation caller.</summary>
    /// <returns>A task that completes after asserting propagation of OperationCanceledException from cache invalidation.</returns>
    [Fact]
    public async Task InvalidateAsync_does_not_swallow_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cache = new Mock<IDistributedCache>();
        cache.Setup(value => value.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var sut = new DistributedContactRequestCache(cache.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.InvalidateAsync(cancellation.Token));
    }
}
