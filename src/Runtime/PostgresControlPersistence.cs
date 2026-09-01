using System.Text.Json;
using LISSTech.EntitySync.Core;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

internal static class PostgresControlPersistence
{
    internal static void RequireTenant(string tenantId, string modelTenantId, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (!string.Equals(tenantId, modelTenantId, StringComparison.Ordinal))
            throw new ArgumentException("The model must belong to the requested tenant.", parameterName);
    }

    internal static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    internal static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    internal static DateTimeOffset? NullableTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    internal static EntitySyncSha256? NullableHash(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new EntitySyncSha256(reader.GetString(ordinal));

    internal static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Database value '{value}' is not a defined {typeof(T).Name}.");

    internal static string SerializeStringList(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values);

    internal static IReadOnlyList<string> DeserializeStringList(string json) =>
        JsonSerializer.Deserialize<string[]>(json)
        ?? throw new InvalidOperationException("Stored JSON string list is null.");

    internal static string SerializeFieldDiffs(IReadOnlyList<EntityFieldChange> diffs)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var diff in diffs)
            {
                writer.WriteStartObject();
                writer.WriteString("fieldName", diff.Field);
                WriteFieldValue(
                    writer, "before", diff.Before, diff.BeforeSha256, diff.Sensitive);
                WriteFieldValue(
                    writer, "desired", diff.Desired, diff.DesiredSha256, diff.Sensitive);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static IReadOnlyList<EntityFieldChange> DeserializeFieldDiffs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(element =>
            {
                var before = element.GetProperty("before");
                var desired = element.GetProperty("desired");
                var sensitive = before.GetProperty("sensitive").GetBoolean();
                if (desired.GetProperty("sensitive").GetBoolean() != sensitive)
                    throw new InvalidOperationException(
                        "Stored field sensitivity differs between before and desired values.");
                return new EntityFieldChange(
                    element.GetProperty("fieldName").GetString()
                        ?? throw new InvalidOperationException("Stored field name is null."),
                    new EntitySyncJsonValue(before.GetProperty("value").GetRawText()),
                    new EntitySyncJsonValue(desired.GetProperty("value").GetRawText()),
                    new EntitySyncSha256(before.GetProperty("sha256").GetString()
                        ?? throw new InvalidOperationException("Stored before hash is null.")),
                    new EntitySyncSha256(desired.GetProperty("sha256").GetString()
                        ?? throw new InvalidOperationException("Stored desired hash is null.")),
                    sensitive);
            })
            .ToArray();
    }

    private static void WriteFieldValue(
        Utf8JsonWriter writer,
        string propertyName,
        EntitySyncJsonValue value,
        EntitySyncSha256 sha256,
        bool sensitive)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        using (var document = JsonDocument.Parse(value.Json))
            document.RootElement.WriteTo(writer);
        writer.WriteString("sha256", sha256.Value);
        writer.WriteBoolean("sensitive", sensitive);
        writer.WriteEndObject();
    }
}
