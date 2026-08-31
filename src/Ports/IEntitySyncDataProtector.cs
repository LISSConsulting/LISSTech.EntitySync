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

public interface IIdempotentCommandExecutor
{
    Task<IdempotentResponse> ExecuteAsync(
        string tenantId,
        string key,
        string requestHash,
        Func<CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken);
}

public sealed class IdempotencyConflictException : InvalidOperationException
{
    public IdempotencyConflictException(string message) : base(message)
    {
    }
}
