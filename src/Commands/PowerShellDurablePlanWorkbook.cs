using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Commands;

internal static class PowerShellDurablePlanWorkbook
{
    private const string ArtifactEntryName = "entitysync/durable-plan.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    internal static void Write(
        EntitySyncDurablePlanManifest manifest,
        string path,
        IEntitySyncDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var reviewerPlan = ToReviewerPlan(manifest);
        EntitySyncPlanWorkbook.Write(reviewerPlan, path);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new DurableArtifactPayload(
                manifest.Plan,
                manifest.Items.Select(DurableArtifactItem.From).ToArray()), JsonOptions);
        var protectedPayload = protector.Protect(
            EntitySyncDataProtectionPurpose.DurablePlanArtifact,
            Convert.ToBase64String(payload));
        var envelope = new DurableArtifactEnvelope(
            manifest.Plan.TenantId,
            manifest.Plan.PlanId,
            protectedPayload);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry(ArtifactEntryName)?.Delete();
        var entry = archive.CreateEntry(ArtifactEntryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, envelope, JsonOptions);
    }

    internal static bool HasDurableEnvelope(string path)
    {
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return false;
        using var archive = ZipFile.OpenRead(path);
        return archive.GetEntry(ArtifactEntryName) is not null;
    }

    internal static EntitySyncDurablePlanManifest Read(
        string path,
        string expectedTenantId,
        IEntitySyncDataProtector protector)
    {
        _ = EntitySyncPlanWorkbook.Read(path);
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(ArtifactEntryName)
            ?? throw new InvalidOperationException(
                "Workbook does not contain a durable EntitySync plan artifact.");
        DurableArtifactEnvelope envelope;
        using (var stream = entry.Open())
        {
            envelope = JsonSerializer.Deserialize<DurableArtifactEnvelope>(
                    stream, JsonOptions)
                ?? throw new InvalidOperationException(
                    "Workbook durable plan artifact is invalid.");
        }

        if (string.IsNullOrWhiteSpace(envelope.ProtectedPayload))
            throw new InvalidOperationException(
                "Workbook durable plan artifact authentication failed.");
        if (!string.Equals(envelope.TenantId, expectedTenantId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Workbook durable plan artifact authentication failed.");
        byte[] payload;
        try
        {
            var payloadBase64 = protector.Unprotect(
                EntitySyncDataProtectionPurpose.DurablePlanArtifact,
                envelope.ProtectedPayload);
            payload = Convert.FromBase64String(payloadBase64);
        }
        catch (Exception exception)
            when (exception is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                "Workbook durable plan artifact authentication failed.",
                exception);
        }
        var artifact = JsonSerializer.Deserialize<DurableArtifactPayload>(
                payload, JsonOptions)
            ?? throw new InvalidOperationException(
                "Workbook durable plan artifact payload is invalid.");
        if (artifact.Plan.TenantId != envelope.TenantId
            || artifact.Plan.PlanId != envelope.PlanId)
            throw new InvalidOperationException(
                "Workbook durable plan artifact authentication failed.");
        return EntitySyncDurablePlanManifest.LoadPersisted(
            artifact.Plan, artifact.Items.Select(item => item.ToDomain()));
    }

    private static EntitySyncPlan ToReviewerPlan(
        EntitySyncDurablePlanManifest manifest)
    {
        var first = manifest.Items.FirstOrDefault();
        return new EntitySyncPlan
        {
            Id = manifest.Plan.PlanId.ToString(),
            TenantId = manifest.Plan.TenantId,
            SourceVendor = first?.SourceVendor ?? "Unknown",
            SourceEntityType = first?.SourceEntityType ?? "Unknown",
            TargetVendor = first?.TargetVendor ?? "Unknown",
            TargetEntityType = first?.TargetEntityType ?? "Unknown",
            CreatedAt = manifest.Plan.CreatedAt,
            ExpiresAt = manifest.Plan.ExpiresAt,
            Status = EntitySyncPlanStatuses.Draft,
            Items = manifest.Items.Select(item => new EntitySyncPlanItem
            {
                Action = item.Action,
                Source = new ExternalEntity
                {
                    Vendor = item.SourceVendor,
                    EntityType = item.SourceEntityType,
                    Id = item.SourceEntityId,
                    Name = item.SourceEntityKey
                },
                Target = item.TargetEntityId is null
                    ? null
                    : new ExternalEntity
                    {
                        Vendor = item.TargetVendor,
                        EntityType = item.TargetEntityType,
                        Id = item.TargetEntityId,
                        Name = item.TargetEntityId
                    },
                ResolvedTargetParent = item.ResolvedTargetParent,
                Score = item.MatchEvidence.Score,
                MatchType = item.MatchEvidence.MatchType,
                Reasons = item.MatchEvidence.Reasons.ToList(),
                Status = "Planned",
                DesiredStateHash = item.DesiredPayloadSha256.Value,
                DesiredStateHashVersion = 1
            }).ToList()
        };
    }

    private sealed record DurableArtifactEnvelope(
        string TenantId,
        Guid PlanId,
        string ProtectedPayload);
    private sealed record DurableArtifactPayload(
        EntitySyncDurablePlan Plan,
        IReadOnlyList<DurableArtifactItem> Items);

    private sealed record DurableArtifactItem(
        string TenantId,
        Guid PlanId,
        Guid ItemId,
        int ItemOrdinal,
        string SourceVendor,
        string SourceConnectionId,
        string SourceEntityType,
        string SourceEntityKey,
        string SourceEntityId,
        string TargetVendor,
        string TargetConnectionId,
        string TargetEntityType,
        string? TargetEntityId,
        string Action,
        DurableArtifactMatchEvidence MatchEvidence,
        EntitySyncJsonValue RedactedBefore,
        EntitySyncJsonValue RedactedDesired,
        EntitySyncSha256? BeforePayloadSha256,
        EntitySyncSha256 DesiredPayloadSha256,
        IReadOnlyList<EntityFieldChange> FieldDiffs,
        EntityWriteParent? ResolvedTargetParent)
    {
        internal static DurableArtifactItem From(EntitySyncDurablePlanItem item) =>
            new(
                item.TenantId,
                item.PlanId,
                item.ItemId,
                item.ItemOrdinal,
                item.SourceVendor,
                item.SourceConnectionId,
                item.SourceEntityType,
                item.SourceEntityKey,
                item.SourceEntityId,
                item.TargetVendor,
                item.TargetConnectionId,
                item.TargetEntityType,
                item.TargetEntityId,
                item.Action,
                DurableArtifactMatchEvidence.From(item.MatchEvidence),
                item.RedactedBefore,
                item.RedactedDesired,
                item.BeforePayloadSha256,
                item.DesiredPayloadSha256,
                item.FieldDiffs,
                item.ResolvedTargetParent);

        internal EntitySyncDurablePlanItem ToDomain() =>
            new(
                TenantId,
                PlanId,
                ItemId,
                ItemOrdinal,
                SourceVendor,
                SourceConnectionId,
                SourceEntityType,
                SourceEntityKey,
                SourceEntityId,
                TargetVendor,
                TargetConnectionId,
                TargetEntityType,
                TargetEntityId,
                Action,
                MatchEvidence.ToDomain(),
                RedactedBefore,
                RedactedDesired,
                BeforePayloadSha256,
                DesiredPayloadSha256,
                FieldDiffs,
                ResolvedTargetParent);
    }
    private sealed record DurableArtifactMatchEvidence(
        int Score,
        string MatchType,
        IReadOnlyList<string> Reasons)
    {
        internal static DurableArtifactMatchEvidence From(
            EntitySyncMatchEvidence value) =>
            new(value.Score, value.MatchType, value.Reasons);

        internal EntitySyncMatchEvidence ToDomain() =>
            new(Score, MatchType, Reasons);
    }

}
