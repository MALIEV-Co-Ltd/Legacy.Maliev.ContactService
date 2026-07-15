using System.Text.Json;
using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.ContactService.Data;

/// <summary>Redis-backed cache for the stable legacy ContactRequest list.</summary>
public sealed class DistributedContactRequestCache(
    IDistributedCache distributedCache,
    ILogger<DistributedContactRequestCache>? logger = null) : IContactRequestCache
{
    private const string AllContactRequestsKey = "legacy:contact:messages:all:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContactRequestResponse>?> GetAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await distributedCache.GetAsync(AllContactRequestsKey, cancellationToken);
            return bytes is null
                ? null
                : JsonSerializer.Deserialize<ContactRequestResponse[]>(bytes, JsonOptions);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "ContactRequest cache read failed; falling back to PostgreSQL");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAllAsync(
        IReadOnlyList<ContactRequestResponse> ContactRequests,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ContactRequests, JsonOptions);
            await distributedCache.SetAsync(
                AllContactRequestsKey,
                bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) },
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "ContactRequest cache write failed; continuing without cache");
        }
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await distributedCache.RemoveAsync(AllContactRequestsKey, cancellationToken);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "ContactRequest cache invalidation failed");
        }
    }
}
