using Legacy.Maliev.ContactService.Application.Interfaces;
using Legacy.Maliev.ContactService.Application.Models;
using Legacy.Maliev.ContactService.Application.Services;
using Legacy.Maliev.ContactService.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.ContactService.Tests.Application;

public sealed class ContactRequestApplicationServiceTests
{
    [Fact]
    public async Task CreateAsync_preserves_legacy_message_fields_and_timestamps()
    {
        var repository = new InMemoryContactRequestRepository();
        var cache = new NoOpContactRequestCache();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero));
        var service = new ContactRequestApplicationService(repository, cache, timeProvider);

        var created = await service.CreateAsync(
            new UpsertContactRequestRequest("Nat", "Tester", "MALIEV", "n@example.invalid", "123", "TH", "Need a quote"),
            CancellationToken.None);

        Assert.Equal("Nat", created.FirstName);
        Assert.Equal("Tester", created.LastName);
        Assert.Equal("MALIEV", created.Company);
        Assert.Equal("n@example.invalid", created.Email);
        Assert.Equal("123", created.Telephone);
        Assert.Equal("TH", created.Country);
        Assert.Equal("Need a quote", created.MessageContent);
        Assert.Equal(new DateTime(2026, 7, 15, 1, 2, 3, DateTimeKind.Utc), created.CreatedDate);
        Assert.True(cache.Invalidated);
    }

    [Fact]
    public async Task GetPaginatedAsync_preserves_search_sort_and_not_found_boundaries()
    {
        var repository = new InMemoryContactRequestRepository(
            new ContactRequest { Id = 2, FirstName = "Beta", Email = "beta@example.invalid", CreatedDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
            new ContactRequest { Id = 1, FirstName = "Alpha", MessageContent = "hello world", CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        var service = new ContactRequestApplicationService(repository, new NoOpContactRequestCache(), TimeProvider.System);

        var page = await service.GetPaginatedAsync(
            ContactRequestSortType.MessageId_Descending,
            "hello",
            1,
            10,
            CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(1, page.Items[0].Id);
        Assert.False(page.HasPreviousPage);
        Assert.False(page.HasNextPage);
        Assert.Equal(1, page.TotalItems);
    }

    private sealed class NoOpContactRequestCache : IContactRequestCache
    {
        public bool Invalidated { get; private set; }

        public Task<IReadOnlyList<ContactRequestResponse>?> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContactRequestResponse>?>(null);

        public Task SetAllAsync(IReadOnlyList<ContactRequestResponse> ContactRequests, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task InvalidateAsync(CancellationToken cancellationToken)
        {
            Invalidated = true;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryContactRequestRepository(params ContactRequest[] seed) : IContactRequestRepository
    {
        private readonly List<ContactRequest> items = seed.ToList();

        public Task<IReadOnlyList<ContactRequest>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContactRequest>>(items.ToArray());

        public Task<ContactRequest?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(items.SingleOrDefault(item => item.Id == id));

        public Task AddAsync(ContactRequest ContactRequest, CancellationToken cancellationToken)
        {
            ContactRequest.Id = items.Count == 0 ? 1 : items.Max(item => item.Id) + 1;
            items.Add(ContactRequest);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ContactRequest ContactRequest, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(ContactRequest ContactRequest, CancellationToken cancellationToken)
        {
            items.Remove(ContactRequest);
            return Task.CompletedTask;
        }
    }
}
