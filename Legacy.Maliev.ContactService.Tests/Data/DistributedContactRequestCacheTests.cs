using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Moq;

namespace Legacy.Maliev.ContactService.Tests.Data;

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

    [Fact]
    public async Task InvalidateAsync_does_not_swallow_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var cache = new Mock<IDistributedCache>();
        cache.Setup(value => value.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var sut = new DistributedContactRequestCache(cache.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.InvalidateAsync(cancellation.Token));
    }
}
