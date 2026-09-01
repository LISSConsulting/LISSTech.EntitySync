using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace LISSTech.EntitySync.Core;

public sealed record EntitySyncActor
{
    public EntitySyncActor(string actorId)
    {
        ActorId = ControlModelGuard.Required(actorId, nameof(actorId));
    }

    public string ActorId { get; }

    public override string ToString() => ActorId;
}

public sealed record EntitySyncSha256
{
    public EntitySyncSha256(string value)
    {
        var normalized = ControlModelGuard.Required(value, nameof(value)).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A SHA-256 value must contain exactly 64 hexadecimal characters.", nameof(value));
        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record EntitySyncJsonValue
{
    public EntitySyncJsonValue(string json)
    {
        var value = ControlModelGuard.Required(json, nameof(json));
        try
        {
            using var document = JsonDocument.Parse(value);
            Json = JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The value must be valid JSON.", nameof(json), exception);
        }
    }

    public string Json { get; }

    public override string ToString() => Json;
}

public sealed record EntitySyncConnectionDefinition
{
    public EntitySyncConnectionDefinition(
        string tenantId,
        string connectionId,
        string vendor,
        string displayName,
        long generation,
        bool enabled,
        EntitySyncJsonValue publicConfiguration,
        string secretCiphertext,
        DateTimeOffset createdAt,
        EntitySyncActor createdBy,
        DateTimeOffset updatedAt,
        EntitySyncActor updatedBy,
        Guid? platformInstanceId = null)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        ConnectionId = ControlModelGuard.Required(connectionId, nameof(connectionId));
        Vendor = EntitySyncVendors.Normalize(ControlModelGuard.Required(vendor, nameof(vendor)));
        DisplayName = ControlModelGuard.Required(displayName, nameof(displayName));
        Generation = ControlModelGuard.Positive(generation, nameof(generation));
        Enabled = enabled;
        PublicConfiguration = publicConfiguration ?? throw new ArgumentNullException(nameof(publicConfiguration));
        SecretCiphertext = ControlModelGuard.Required(secretCiphertext, nameof(secretCiphertext));
        CreatedAt = createdAt;
        CreatedBy = createdBy ?? throw new ArgumentNullException(nameof(createdBy));
        UpdatedAt = updatedAt;
        UpdatedBy = updatedBy ?? throw new ArgumentNullException(nameof(updatedBy));
        PlatformInstanceId = platformInstanceId is null
            ? null
            : ControlModelGuard.NonEmpty(platformInstanceId.Value, nameof(platformInstanceId));
        if (updatedAt < createdAt) throw new ArgumentException("Updated time cannot precede created time.", nameof(updatedAt));
    }

    public string TenantId { get; }
    public string ConnectionId { get; }
    public string Vendor { get; }
    public string DisplayName { get; }
    public long Generation { get; }
    public bool Enabled { get; }
    public EntitySyncJsonValue PublicConfiguration { get; }
    public string SecretCiphertext { get; }
    public Guid? PlatformInstanceId { get; }
    public DateTimeOffset CreatedAt { get; }
    public EntitySyncActor CreatedBy { get; }
    public DateTimeOffset UpdatedAt { get; }
    public EntitySyncActor UpdatedBy { get; }

    public EntitySyncConnectionDefinition NextGeneration(
        string displayName,
        bool enabled,
        EntitySyncJsonValue publicConfiguration,
        string secretCiphertext,
        EntitySyncActor actor,
        DateTimeOffset now,
        Guid? platformInstanceId = null) =>
        new(
            TenantId,
            ConnectionId,
            Vendor,
            displayName,
            checked(Generation + 1),
            enabled,
            publicConfiguration,
            secretCiphertext,
            CreatedAt,
            CreatedBy,
            now,
            actor,
            platformInstanceId ?? PlatformInstanceId);
}

internal static class ControlModelGuard
{
    public static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{parameterName} is required.", parameterName);
        return value.Trim();
    }

    public static string? Optional(string? value, string parameterName)
    {
        if (value is null) return null;
        return Required(value, parameterName);
    }

    public static Guid NonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty) throw new ArgumentException($"{parameterName} cannot be empty.", parameterName);
        return value;
    }

    public static int Positive(int value, string parameterName)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        return value;
    }

    public static long Positive(long value, string parameterName)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        return value;
    }

    public static int NonNegative(int value, string parameterName)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} cannot be negative.");
        return value;
    }

    public static T Defined<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} is not defined.");
        return value;
    }

    public static IReadOnlySet<string> StringSet(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return values
            .Select(value => Required(value, parameterName))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<T> ReadOnlyCopy<T>(IEnumerable<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return new ReadOnlyCollection<T>(values.ToArray());
    }
}
