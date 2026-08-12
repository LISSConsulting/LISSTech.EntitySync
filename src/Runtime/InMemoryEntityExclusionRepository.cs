using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

public sealed class InMemoryEntityExclusionRepository : IEntityExclusionRepository
{
    private readonly object gate = new();
    private readonly List<EntityExclusion> exclusions = [];

    public Task<IReadOnlyList<EntityExclusion>> ListActiveAsync(EntityExclusionRoute route, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<EntityExclusion>>(exclusions
                .Where(exclusion => exclusion.IsActive && exclusion.Route == route)
                .OrderBy(exclusion => exclusion.SourceEntityId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }
    }

    public Task<EntityExclusion> AddAsync(
        EntityExclusionRoute route,
        string sourceEntityId,
        string sourceName,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourceEntityId = Require(sourceEntityId, nameof(sourceEntityId));
        sourceName = Require(sourceName, nameof(sourceName));
        reason = Require(reason, nameof(reason));
        actor = Require(actor, nameof(actor));
        lock (gate)
        {
            var existingIndex = exclusions.FindIndex(exclusion => exclusion.IsActive
                && exclusion.Route == route
                && exclusion.SourceEntityId.Equals(sourceEntityId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = exclusions[existingIndex];
                var updated = existing with { SourceName = sourceName, Reason = reason };
                exclusions[existingIndex] = updated;
                return Task.FromResult(updated);
            }

            var exclusion = new EntityExclusion(Guid.NewGuid(), route, sourceEntityId, sourceName, reason, actor, DateTimeOffset.UtcNow);
            exclusions.Add(exclusion);
            return Task.FromResult(exclusion);
        }
    }

    public Task<bool> RevokeAsync(
        EntityExclusionRoute route,
        string sourceEntityId,
        string actor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourceEntityId = Require(sourceEntityId, nameof(sourceEntityId));
        actor = Require(actor, nameof(actor));
        lock (gate)
        {
            var existingIndex = exclusions.FindIndex(exclusion => exclusion.IsActive
                && exclusion.Route == route
                && exclusion.SourceEntityId.Equals(sourceEntityId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0) return Task.FromResult(false);
            exclusions[existingIndex] = exclusions[existingIndex] with { RevokedBy = actor, RevokedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }

    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException($"{name} is required.", name)
        : value.Trim();
}
