using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Runtime;

internal static class EntityGraphPersistence
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static EntityGraphScope ValidateScope(EntityGraphScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new EntityGraphScope(
            Require(scope.TenantId, nameof(scope.TenantId), 256),
            Require(scope.Vendor, nameof(scope.Vendor), 128),
            Require(scope.ConnectionId, nameof(scope.ConnectionId), 256),
            Require(scope.EntityType, nameof(scope.EntityType), 128));
    }

    internal static EntityGraphNodeKey ValidateKey(EntityGraphNodeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var scope = ValidateScope(new EntityGraphScope(
            key.TenantId,
            key.Vendor,
            key.ConnectionId,
            key.EntityType));
        return new EntityGraphNodeKey(
            scope.TenantId,
            scope.Vendor,
            scope.ConnectionId,
            scope.EntityType,
            Require(key.EntityId, nameof(key.EntityId), 512));
    }

    internal static EntityGraphRelationshipObservation ValidateRelationship(
        EntityGraphRelationshipObservation relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        var source = ValidateKey(relationship.Source);
        var target = ValidateKey(relationship.Target);
        if (!source.TenantId.Equals(target.TenantId, StringComparison.Ordinal))
            throw new ArgumentException("Relationship endpoints must belong to the same tenant.", nameof(relationship));
        if (relationship.Score is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(relationship), "Relationship score must be between 0 and 100.");
        var status = Require(relationship.Status, nameof(relationship.Status), 32);
        if (status is not EntityGraphRelationshipStatuses.Proposed
            and not EntityGraphRelationshipStatuses.Confirmed
            and not EntityGraphRelationshipStatuses.Removed)
        {
            throw new ArgumentException("Relationship status is invalid.", nameof(relationship));
        }
        return relationship with
        {
            Source = source,
            Target = target,
            RelationshipType = Require(relationship.RelationshipType, nameof(relationship.RelationshipType), 128),
            Status = status,
            MatchType = Require(relationship.MatchType, nameof(relationship.MatchType), 128),
            Evidence = relationship.Evidence
                .Select(value => Require(value, nameof(relationship.Evidence), 2000))
                .ToArray(),
            PlanId = Optional(relationship.PlanId, 128)
        };
    }

    internal static string Serialize(ExternalEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (string.IsNullOrWhiteSpace(entity.Id)) throw new ArgumentException("Entity ID is required.", nameof(entity));
        return JsonSerializer.Serialize(entity, JsonOptions);
    }

    internal static ExternalEntity Deserialize(string payload)
    {
        var entity = JsonSerializer.Deserialize<ExternalEntity>(payload, JsonOptions)
            ?? throw new InvalidOperationException("Stored entity payload was empty.");
        entity.ExternalIds = new Dictionary<string, string>(entity.ExternalIds, StringComparer.OrdinalIgnoreCase);
        entity.CustomFields = new Dictionary<string, string?>(entity.CustomFields, StringComparer.OrdinalIgnoreCase);
        return entity;
    }

    internal static string Hash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    internal static string Key(EntityGraphNodeKey key)
    {
        var validated = ValidateKey(key);
        return string.Join('\u001f',
            validated.TenantId,
            validated.Vendor.ToLowerInvariant(),
            validated.ConnectionId.ToLowerInvariant(),
            validated.EntityType.ToLowerInvariant(),
            validated.EntityId.ToLowerInvariant());
    }

    internal static string RelationshipKey(EntityGraphRelationshipObservation relationship) =>
        string.Join('\u001e', Key(relationship.Source), Key(relationship.Target), relationship.RelationshipType.ToLowerInvariant());

    internal static string Require(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength) throw new ArgumentException($"{name} cannot exceed {maximumLength} characters.", name);
        return trimmed;
    }

    internal static string? Optional(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength) throw new ArgumentException($"Value cannot exceed {maximumLength} characters.");
        return trimmed;
    }
}
