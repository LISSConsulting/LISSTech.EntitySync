using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public enum EntitySyncDataProtectionPurpose
{
    ConnectionSecret,
    AuditValue
}

public interface IEntitySyncDataProtector
{
    string Protect(EntitySyncDataProtectionPurpose purpose, string plaintext);

    string Unprotect(EntitySyncDataProtectionPurpose purpose, string ciphertext);
}

public sealed record IdempotentResponse
{
    public IdempotentResponse(int statusCode, EntitySyncJsonValue responseBody)
    {
        if (statusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "HTTP status code must be between 100 and 599.");
        StatusCode = statusCode;
        ResponseBody = responseBody ?? throw new ArgumentNullException(nameof(responseBody));
    }

    public int StatusCode { get; }
    public EntitySyncJsonValue ResponseBody { get; }
}

public sealed record IdempotencyExecutionContext
{
    public IdempotencyExecutionContext(string tenantId, string key, string token)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Idempotency token is required.", nameof(token));
        TenantId = tenantId.Trim();
        Key = key.Trim();
        Token = token.Trim();
    }

    public string TenantId { get; }
    public string Key { get; }
    public string Token { get; }
}

public interface IIdempotentCommandExecutor
{
    Task<IdempotentResponse> ExecuteAsync(
        string tenantId,
        string key,
        string requestHash,
        Func<IdempotencyExecutionContext, CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken);
}

public sealed class IdempotencyConflictException : InvalidOperationException
{
    public IdempotencyConflictException(string message) : base(message)
    {
    }
}
