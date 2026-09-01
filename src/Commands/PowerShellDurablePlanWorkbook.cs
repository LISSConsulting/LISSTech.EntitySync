using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;

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
        string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var reviewerPlan = ToReviewerPlan(manifest);
        EntitySyncPlanWorkbook.Write(reviewerPlan, path);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new DurableArtifactPayload(manifest.Plan, manifest.Items), JsonOptions);
        var envelope = new DurableArtifactEnvelope(
            Convert.ToBase64String(payload),
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry(ArtifactEntryName)?.Delete();
        var entry = archive.CreateEntry(ArtifactEntryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, envelope, JsonOptions);
    }

    internal static EntitySyncDurablePlanManifest Read(string path)
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

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(envelope.PayloadBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Workbook durable plan artifact payload is invalid.", exception);
        }
        var actualSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualSha256),
                Encoding.ASCII.GetBytes(envelope.ArtifactSha256)))
            throw new InvalidOperationException(
                "Workbook durable plan artifact digest does not match its payload.");
        var artifact = JsonSerializer.Deserialize<DurableArtifactPayload>(
                payload, JsonOptions)
            ?? throw new InvalidOperationException(
                "Workbook durable plan artifact payload is invalid.");
        var manifest = EntitySyncDurablePlanManifest.Create(
            artifact.Plan, artifact.Items);
        if (manifest.Plan.PlanDigestSha256 != artifact.Plan.PlanDigestSha256)
            throw new InvalidOperationException(
                "Workbook durable plan manifest digest is invalid.");
        return manifest;
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
        string PayloadBase64,
        string ArtifactSha256);

    private sealed record DurableArtifactPayload(
        EntitySyncDurablePlan Plan,
        IReadOnlyList<EntitySyncDurablePlanItem> Items);
}
