using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.HostedServices;

/// <summary>
/// Ensures the Mongo indexes for the Claims collection are created when the
/// service starts. Keeping this in a hosted initializer keeps the repository
/// itself side-effect free and avoids creating indexes on every request.
///
/// The indexes support tenant-scoped lookups, claim version tracking, and the
/// recurring accumulator rebuild queries used for Redis cache misses.
/// </summary>
public sealed class ClaimIndexInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClaimIndexInitializer> _logger;

    public ClaimIndexInitializer(
        IServiceScopeFactory scopeFactory,
        ILogger<ClaimIndexInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();

        var collection = db.GetCollection<Claim>("Claims");
        var keys = Builders<Claim>.IndexKeys;

        var indexes = new[]
        {
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.ClaimNumber)),
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.MemberId)),
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.SubmittedDate)),
            // Used for the main claim search and date-based lookups.
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.ServiceDateFrom)),
            // Keeps the version chain easy to query when we fetch the latest record or list versions.
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.ClaimVersionId).Descending(c => c.VersionNumber)),
            // Supports accumulator rebuild work when we need to aggregate by owner, plan, and service date.
            new CreateIndexModel<Claim>(keys
                .Ascending(c => c.TenantId)
                .Ascending(c => c.BenefitPlanId)
                .Ascending(c => c.MemberId)
                .Ascending(c => c.ServiceDateFrom)),
            new CreateIndexModel<Claim>(keys
                .Ascending(c => c.TenantId)
                .Ascending(c => c.BenefitPlanId)
                .Ascending(c => c.SubscriberId)
                .Ascending(c => c.ServiceDateFrom)),
        };

        collection.Indexes.CreateMany(indexes, cancellationToken);

        var txnCollection = db.GetCollection<ClaimImportTransaction>(
            Repositories.ClaimImportTransactionRepositoryMongo.CollectionName);
        txnCollection.Indexes.CreateOne(new CreateIndexModel<ClaimImportTransaction>(
            Builders<ClaimImportTransaction>.IndexKeys
                .Ascending(t => t.TenantId)
                .Descending(t => t.ReceivedAt)),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Claim indexes ensured on collection 'Claims'.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
