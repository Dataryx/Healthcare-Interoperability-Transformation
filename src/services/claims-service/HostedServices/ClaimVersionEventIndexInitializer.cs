using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.HostedServices;

/// <summary>
/// Ensures the Mongo indexes for the claim version event stream exist when the
/// service starts. These indexes keep the publish-and-retry flow reliable when
/// several writes try to land on the same version at the same time.
/// </summary>
public sealed class ClaimVersionEventIndexInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _collectionName;
    private readonly ILogger<ClaimVersionEventIndexInitializer> _logger;

    public ClaimVersionEventIndexInitializer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ClaimVersionEventIndexInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _collectionName = configuration["CosmosDb:ClaimVersionEventsContainer"] ?? "ClaimVersionEvents";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();

        var collection = db.GetCollection<ClaimVersionEvent>(_collectionName);

        // Prevent duplicate event writes for the same tenant, claim version, and event id.
        var idemKeys = Builders<ClaimVersionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ClaimVersionId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<ClaimVersionEvent>(
                idemKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_claim_event" }),
            cancellationToken: cancellationToken);

        // Keeps event ordering stable by tenant, claim version, and version number.
        // The retry path relies on this to reject duplicates cleanly.
        var orderKeys = Builders<ClaimVersionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ClaimVersionId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(
            new CreateIndexModel<ClaimVersionEvent>(
                orderKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_claim_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "ClaimVersionEvent indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
