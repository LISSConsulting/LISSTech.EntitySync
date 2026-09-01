using System.Buffers;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class PlanManifestBuilder(IEntityMapper mapper)
{
    private const string RedactedValue = "[redacted]";
    private static readonly string[] SensitiveTerms =
    [
        "authorization", "authentication", "bearer", "clientsecret", "client_secret",
        "credential", "password", "secret", "token", "apikey", "api_key",
        "privatekey", "private_key"
    ];
    private static readonly JsonElement CanonicalNull = Canonicalize(null);
    private static readonly JsonElement CanonicalRedaction = Canonicalize(RedactedValue);

    public EntitySyncDurablePlanManifest Build(
        EntitySyncPlan plannerOutput,
        EntitySyncPolicy policy,
        Guid planId,
        EntitySyncActor actor,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        EntitySyncSelectionBounds selectionBounds,
        IReadOnlySet<string> activeExcludedSourceIds)
    {
        ArgumentNullException.ThrowIfNull(plannerOutput);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selectionBounds);
        ArgumentNullException.ThrowIfNull(activeExcludedSourceIds);
        ValidatePlannerOutput(plannerOutput, policy);

        var placeholderDigest = new EntitySyncSha256(new string('0', 64));
        var plan = new EntitySyncDurablePlan(
            policy.TenantId,
            planId,
            policy.PolicyId,
            policy.Version,
            policy.DefinitionSha256,
            policy.RouteScope,
            plannerOutput.Execution.SourceConnectionId,
            plannerOutput.Execution.SourceConnectionGeneration,
            plannerOutput.Execution.TargetConnectionId,
            plannerOutput.Execution.TargetConnectionGeneration,
            placeholderDigest,
            EntitySyncDurablePlanStatus.Draft,
            selectionBounds,
            0,
            createdAt,
            actor,
            expiresAt);

        var items = new EntitySyncDurablePlanItem[plannerOutput.Items.Count];
        for (var ordinal = 0; ordinal < items.Length; ordinal++)
        {
            items[ordinal] = BuildItem(
                plannerOutput,
                policy,
                planId,
                ordinal,
                plannerOutput.Items[ordinal],
                activeExcludedSourceIds);
        }
        return EntitySyncDurablePlanManifest.Create(plan, items);
    }

    public static EntitySyncSha256 ComputeItemDigest(EntitySyncDurablePlanItem item) =>
        EntitySyncPlanDigest.Compute(item);

    private EntitySyncDurablePlanItem BuildItem(
        EntitySyncPlan plannerOutput,
        EntitySyncPolicy policy,
        Guid planId,
        int ordinal,
        EntitySyncPlanItem plannerItem,
        IReadOnlySet<string> activeExcludedSourceIds)
    {
        var isExcluded = plannerItem.MatchType.Equals(
                "PersistentExclusion", StringComparison.OrdinalIgnoreCase)
            || plannerItem.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)
            && activeExcludedSourceIds.Contains(plannerItem.Source.Id);
        var action = isExcluded ? "None" : plannerItem.Action;
        var matchType = isExcluded ? "PersistentExclusion" : plannerItem.MatchType;
        var reasons = isExcluded
            ? new[] { "Permanently excluded by the active route policy." }
            : plannerItem.Reasons.Select(SanitizeReason).ToArray();

        var desired = BuildDesiredPayload(plannerOutput, policy, plannerItem, action);
        var before = BuildBeforePayload(
            plannerItem.Target,
            desired.Keys,
            policy.Definition.BlockedFields);
        var changes = BuildFieldChanges(before, desired);
        var redactedBefore = Redact(before);
        var redactedDesired = Redact(desired);
        var sourceKey = plannerItem.Source.Id.Trim().ToLowerInvariant();
        var itemId = StableGuid(EntitySyncCanonicalDigest.Compute(new
        {
            planId,
            ordinal,
            SourceKey = sourceKey,
            TargetId = plannerItem.Target?.Id,
            action,
            matchType
        }));

        return new EntitySyncDurablePlanItem(
            policy.TenantId,
            planId,
            itemId,
            ordinal,
            plannerOutput.SourceVendor,
            plannerOutput.Execution.SourceConnectionId,
            plannerOutput.SourceEntityType,
            sourceKey,
            plannerItem.Source.Id,
            plannerOutput.TargetVendor,
            plannerOutput.Execution.TargetConnectionId,
            plannerOutput.TargetEntityType,
            plannerItem.Target?.Id,
            action,
            new EntitySyncMatchEvidence(plannerItem.Score, matchType, reasons),
            ToJsonValue(redactedBefore),
            ToJsonValue(redactedDesired),
            plannerItem.Target is null ? null : HashPayload(before),
            HashPayload(desired),
            changes);
    }

    private Dictionary<string, JsonElement> BuildDesiredPayload(
        EntitySyncPlan plannerOutput,
        EntitySyncPolicy policy,
        EntitySyncPlanItem plannerItem,
        string action)
    {
        if (action.Equals("None", StringComparison.OrdinalIgnoreCase)
            || action.Equals("Review", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        EntityWriteRequest request;
        if (action.Equals("Create", StringComparison.OrdinalIgnoreCase))
        {
            request = mapper.MapCreate(
                plannerItem.Source,
                plannerOutput.TargetVendor,
                plannerOutput.TargetEntityType,
                plannerOutput.Execution.MatchOptions);
        }
        else
        {
            if (plannerItem.Target is null)
                throw new InvalidOperationException(
                    $"Planner action '{action}' requires an immutable target identity.");
            request = mapper.MapUpdate(
                plannerItem.Source,
                plannerItem.Target,
                plannerOutput.Execution.MatchOptions);
        }

        return CreateAllowedDesiredPayload(request, policy.Definition);
    }

    internal static Dictionary<string, JsonElement> CreateAllowedDesiredPayload(
        EntityWriteRequest request,
        EntitySyncPolicyDefinition definition)
    {
        var mapped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = request.Name
        };
        if (request.PrimarySiteId is not null)
            mapped["primarySiteId"] = request.PrimarySiteId;
        AddMappedFields(mapped, request.Fields);
        AddMappedFields(mapped, request.CustomFields);

        var desired = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var allowedField in definition.AllowedFields
                     .Order(StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value, StringComparer.Ordinal))
        {
            if (definition.BlockedFields.Contains(allowedField))
                continue;
            mapped.TryGetValue(allowedField, out var value);
            desired.Add(
                allowedField,
                RemoveBlockedProperties(Canonicalize(value), definition.BlockedFields));
        }
        return desired;
    }

    internal static Dictionary<string, JsonElement> BuildBeforePayload(
        ExternalEntity? target,
        IEnumerable<string> desiredFields,
        IReadOnlySet<string> blockedFields)
    {
        var before = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (target is null)
            return before;
        foreach (var field in desiredFields)
            before.Add(
                field,
                RemoveBlockedProperties(
                    Canonicalize(ReadTargetField(target, field)),
                    blockedFields));
        return before;
    }

    private static IReadOnlyList<EntityFieldChange> BuildFieldChanges(
        IReadOnlyDictionary<string, JsonElement> before,
        IReadOnlyDictionary<string, JsonElement> desired)
    {
        var changes = new List<EntityFieldChange>(desired.Count);
        foreach (var pair in desired.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var beforeValue = before.TryGetValue(pair.Key, out var existing)
                ? existing
                : CanonicalNull;
            var beforeHash = EntitySyncCanonicalDigest.Compute(beforeValue);
            var desiredHash = EntitySyncCanonicalDigest.Compute(pair.Value);
            if (beforeHash == desiredHash)
                continue;
            var redactWhole = IsSensitiveField(pair.Key);
            var redactedBefore = RedactValue(beforeValue, redactWhole);
            var redactedDesired = RedactValue(pair.Value, redactWhole);
            changes.Add(new EntityFieldChange(
                pair.Key,
                ToJsonValue(redactedBefore.Value),
                ToJsonValue(redactedDesired.Value),
                beforeHash,
                desiredHash,
                redactedBefore.Sensitive || redactedDesired.Sensitive));
        }
        return changes;
    }

    internal static Dictionary<string, JsonElement> Redact(
        IReadOnlyDictionary<string, JsonElement> payload)
    {
        var redacted = new Dictionary<string, JsonElement>(payload.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in payload)
            redacted.Add(pair.Key, RedactValue(pair.Value, IsSensitiveField(pair.Key)).Value);
        return redacted;
    }

    private static RedactedJsonElement RedactValue(JsonElement value, bool redactWhole)
    {
        if (redactWhole)
            return new RedactedJsonElement(CanonicalRedaction, true);
        var buffer = new ArrayBufferWriter<byte>();
        bool sensitive;
        using (var writer = new Utf8JsonWriter(buffer))
            sensitive = WriteRedactedCanonical(writer, value);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new RedactedJsonElement(document.RootElement.Clone(), sensitive);
    }

    private static bool WriteRedactedCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        var sensitive = false;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveField(property.Name))
                    {
                        CanonicalRedaction.WriteTo(writer);
                        sensitive = true;
                    }
                    else
                    {
                        sensitive |= WriteRedactedCanonical(writer, property.Value);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    sensitive |= WriteRedactedCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
        return sensitive;
    }

    internal static EntitySyncSha256 HashPayload(
        IReadOnlyDictionary<string, JsonElement> payload) =>
        EntitySyncCanonicalDigest.Compute(ToCanonicalElement(payload));

    internal static EntitySyncJsonValue ToJsonValue(
        IReadOnlyDictionary<string, JsonElement> payload) =>
        new(ToCanonicalElement(payload).GetRawText());

    private static EntitySyncJsonValue ToJsonValue(JsonElement value) =>
        new(value.GetRawText());

    private static JsonElement ToCanonicalElement(IReadOnlyDictionary<string, JsonElement> payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var pair in payload.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement Canonicalize(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, element);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement RemoveBlockedProperties(
        JsonElement value,
        IReadOnlySet<string> blockedFields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteWithoutBlockedProperties(writer, value, blockedFields);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteWithoutBlockedProperties(
        Utf8JsonWriter writer,
        JsonElement value,
        IReadOnlySet<string> blockedFields)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    if (blockedFields.Contains(property.Name)) continue;
                    writer.WritePropertyName(property.Name);
                    WriteWithoutBlockedProperties(writer, property.Value, blockedFields);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteWithoutBlockedProperties(writer, item, blockedFields);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(
                             property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void AddMappedFields<T>(
        IDictionary<string, object?> destination,
        IReadOnlyDictionary<string, T> source)
    {
        foreach (var pair in source)
        {
            if (!destination.TryAdd(pair.Key, pair.Value))
                throw new InvalidOperationException(
                    $"Mapped payload contains duplicate field '{pair.Key}'.");
        }
    }

    private static object? ReadTargetField(ExternalEntity target, string field)
    {
        if (field.Equals("name", StringComparison.OrdinalIgnoreCase)) return target.Name;
        if (field.Equals("email", StringComparison.OrdinalIgnoreCase)) return target.Email;
        if (field.Equals("phone", StringComparison.OrdinalIgnoreCase)) return target.Phone;
        if (field.Equals("website", StringComparison.OrdinalIgnoreCase)) return target.Website;
        if (field.Equals("domain", StringComparison.OrdinalIgnoreCase)) return target.Domain;
        if (field.Equals("primarySiteId", StringComparison.OrdinalIgnoreCase))
            return target.PrimarySiteId;
        if (field.Equals("isActive", StringComparison.OrdinalIgnoreCase)) return target.IsActive;
        return target.CustomFields.TryGetValue(field, out var value) ? value : null;
    }

    private static string SanitizeReason(string reason) =>
        SensitiveTerms.Any(term => reason.Contains(term, StringComparison.OrdinalIgnoreCase))
            ? "[credential redacted]"
            : reason;

    private static bool IsSensitiveField(string field) =>
        SensitiveTerms.Any(term => field.Contains(term, StringComparison.OrdinalIgnoreCase));

    private readonly record struct RedactedJsonElement(JsonElement Value, bool Sensitive);

    private static Guid StableGuid(EntitySyncSha256 digest)
    {
        var bytes = Convert.FromHexString(digest.Value);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static void ValidatePlannerOutput(EntitySyncPlan plan, EntitySyncPolicy policy)
    {
        if (!plan.TenantId.Equals(policy.TenantId, StringComparison.Ordinal)
            || !plan.SourceVendor.Equals(policy.Definition.SourceVendor, StringComparison.OrdinalIgnoreCase)
            || !plan.SourceEntityType.Equals(policy.Definition.SourceEntityType, StringComparison.OrdinalIgnoreCase)
            || !plan.TargetVendor.Equals(policy.Definition.TargetVendor, StringComparison.OrdinalIgnoreCase)
            || !plan.TargetEntityType.Equals(policy.Definition.TargetEntityType, StringComparison.OrdinalIgnoreCase)
            || !plan.Execution.SourceConnectionId.Equals(policy.Definition.SourceConnectionId, StringComparison.Ordinal)
            || !plan.Execution.TargetConnectionId.Equals(policy.Definition.TargetConnectionId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Planner output does not match the immutable policy definition.");
    }
}
