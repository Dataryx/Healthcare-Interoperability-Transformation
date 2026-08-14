using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.HostedServices;

/// <summary>
/// Ensures the Mongo indexes for claim adjustments exist when the app starts.
/// These indexes protect the adjustment chain rules and prevent duplicate
/// idempotency inserts from different requests hitting the same tenant and claim.
/// </summary>
public sealed class ClaimAdjustmentIndexInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _collectionName;
    private readonly ILogger<ClaimAdjustmentIndexInitializer> _logger;

    public ClaimAdjustmentIndexInitializer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ClaimAdjustmentIndexInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _collectionName = configuration["CosmosDb:ClaimAdjustmentsContainer"] ?? "ClaimAdjustments";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();

        var collection = db.GetCollection<ClaimAdjustment>(_collectionName);
        var keys = Builders<ClaimAdjustment>.IndexKeys;

        var indexes = new[]
        {
            // Only one adjustment can be active for a given claim chain in a tenant.
            new CreateIndexModel<ClaimAdjustment>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.ClaimVersionId),
                new CreateIndexOptions { Unique = true, Name = "tenant_chain_unique" }),

            // Prevent duplicate idempotent writes for the same tenant and idempotency key.
            new CreateIndexModel<ClaimAdjustment>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.IdempotencyKey),
                new CreateIndexOptions { Unique = true, Name = "tenant_idempotency_unique" }),

            // Helps the reversal batch query sort open or recent adjustments by status.
            new CreateIndexModel<ClaimAdjustment>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.Status).Descending(x => x.CreatedAt)),

            // Makes the chain-scoped lookup for predecessor records quick and predictable.
            new CreateIndexModel<ClaimAdjustment>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.PredecessorClaimId)),
        };

        collection.Indexes.CreateMany(indexes, cancellationToken);

        _logger.LogInformation(
            "ClaimAdjustment indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
